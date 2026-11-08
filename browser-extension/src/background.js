// Фоновая часть расширения. Связь с программой идёт через мост native messaging.
// Пока программа не запущена, ничего не блокируется: так же, как после выхода из неё размораживаются программы.
import { blockedTarget, buildDnrRules, hostOf, isPageAllowed, pageOf } from "./rules.js";

const api = globalThis.browser ?? globalThis.chrome;
const HostName = "com.adhdwindows.bridge";
const BlockedPage = api.runtime.getURL("blocked.html");
const RequestTimeout = 10000;
const ReconnectDelay = 5000;
const ReportDelay = 250;
const MediaDelay = 150;
const MediaLifetime = 6000;

let port = null;
let online = false;
let rules = [];
// Пропуски заданий: коды страниц, открытых бесплатно. Страницы заданий: код → { select, scroll }.
let passes = [];
let watch = new Map();
let nextId = 1;
let reportTimer = null;
let reconnectTimer = null;
const pending = new Map();

// Плееры во вкладках: вкладка → фрейм → последний снимок. Время просмотра считает программа.
const media = new Map();
let mediaTimer = null;
let focusedWindow = api.windows.WINDOW_ID_NONE;

// Сообщения обрабатываются по одному: ответ о покупке не должен обогнать новые правила.
let queue = Promise.resolve();

function connect() {
  if (port) {
    return;
  }

  clearTimeout(reconnectTimer);
  try {
    port = api.runtime.connectNative(HostName);
  } catch (error) {
    console.warn("ADHDWindows: мост недоступен", error);
    port = null;
    scheduleReconnect();
    return;
  }

  port.onMessage.addListener((message) => {
    queue = queue.then(() => handle(message)).catch((error) => console.error("ADHDWindows", error));
  });
  port.onDisconnect.addListener(() => {
    const reason = api.runtime.lastError?.message;
    if (reason) {
      console.warn("ADHDWindows: мост отключился:", reason);
    }

    port = null;
    queue = queue.then(goOffline);
    scheduleReconnect();
  });
}

function scheduleReconnect() {
  clearTimeout(reconnectTimer);
  reconnectTimer = setTimeout(connect, ReconnectDelay);
}

async function handle(message) {
  switch (message?.type) {
    case "online":
      await sendHello();
      break;
    case "offline":
      await goOffline();
      break;
    case "state":
      online = true;
      rules = Array.isArray(message.rules) ? message.rules : [];
      passes = Array.isArray(message.passes) ? message.passes.filter((page) => typeof page === "string" && page.length > 0) : [];
      watch = readWatch(message.watch);
      await applyRules();
      await enforceTabs();
      await updateWatchers();
      reportWindows();
      reportMedia();
      break;
    case "reply": {
      const request = pending.get(message.id);
      if (request) {
        pending.delete(message.id);
        clearTimeout(request.timer);
        request.resolve(message);
      }

      break;
    }
  }
}

async function sendHello() {
  const incognito = await api.extension.isAllowedIncognitoAccess();
  post({ type: "hello", browser: browserName(), version: api.runtime.getManifest().version, incognito });
}

async function goOffline() {
  online = false;
  rules = [];
  passes = [];
  watch = new Map();
  for (const request of pending.values()) {
    clearTimeout(request.timer);
    request.resolve({ offline: true });
  }

  pending.clear();
  await setDnrRules([]);
  await releaseTabs();
  await updateWatchers();
}

function post(message) {
  try {
    port?.postMessage(message);
  } catch (error) {
    console.warn("ADHDWindows: не удалось отправить сообщение", error);
  }
}

function request(payload) {
  if (!port || !online) {
    return Promise.resolve({ offline: true });
  }

  const id = nextId++;
  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      resolve({ offline: true });
    }, RequestTimeout);
    pending.set(id, { resolve, timer });
    post({ ...payload, id });
  });
}

async function applyRules() {
  await setDnrRules(buildDnrRules(rules, BlockedPage, passes));
}

async function setDnrRules(addRules) {
  const existing = await api.declarativeNetRequest.getDynamicRules();
  await api.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: existing.map((rule) => rule.id),
    addRules,
  });
}

/**
 * Уже открытые вкладки закрытых сайтов закрываются экраном покупки, купленные и открытые пропуском открываются.
 * Снятый пропуск закрывает свою страницу.
 */
async function enforceTabs() {
  for (const tab of await api.tabs.query({})) {
    await enforceTab(tab.id, tab.url);
  }
}

async function enforceTab(tabId, url) {
  if (!online || !url) {
    return;
  }

  const target = blockedTarget(url, BlockedPage);
  if (target) {
    if (hostOf(target) && isPageAllowed(rules, passes, target)) {
      await updateTab(tabId, target);
    }

    return;
  }

  if (!isPageAllowed(rules, passes, url)) {
    await updateTab(tabId, BlockedPage + "#" + url);
  }
}

function readWatch(items) {
  const result = new Map();
  for (const item of Array.isArray(items) ? items : []) {
    if (typeof item?.page === "string" && item.page.length > 0) {
      const old = result.get(item.page);
      result.set(item.page, { select: Boolean(item.select || old?.select), scroll: Boolean(item.scroll || old?.scroll) });
    }
  }

  return result;
}

/** Что читать на странице: { select, scroll } для страниц заданий, null для остальных. */
function watchOf(url) {
  const page = online ? pageOf(url) : null;
  return page === null ? null : watch.get(page) ?? null;
}

/** Скрипт страницы в верхнем фрейме узнаёт, следить ли за выделением и прокруткой. */
async function tellWatcher(tabId, url) {
  try {
    await api.tabs.sendMessage(tabId, { type: "page-watch", watch: watchOf(url) }, { frameId: 0 });
  } catch {
    // Скрипта во вкладке нет: служебная страница или страница ещё грузится, тогда он спросит сам.
  }
}

/** Задания сменились: вкладки узнают новые настройки, а уже открытые страницы заданий засчитываются как посещённые. */
async function updateWatchers() {
  for (const tab of await api.tabs.query({ url: ["http://*/*", "https://*/*"] })) {
    await tellWatcher(tab.id, tab.url);
    if (tab.status === "complete") {
      reportPage(tab, { event: "visit" });
    }
  }
}

/** Сообщение программе о странице задания. О других страницах ничего не отправляется. */
async function reportPage(tab, details, url = tab.url) {
  if (!port || !online || !watchOf(url) || !isPageAllowed(rules, passes, url)) {
    return;
  }

  // Фокус проверяется в момент отправки: события смены фокуса приходят не во всех браузерах вовремя.
  let focused = false;
  try {
    const current = await api.tabs.get(tab.id);
    focused = Boolean(current.active) && Boolean((await api.windows.get(current.windowId)).focused);
  } catch {
    // Вкладку уже закрыли.
  }

  post({ type: "page", tabId: tab.id, url, focused, ...details });
}

/** Программа закрылась: закрытые вкладки снова показывают свои сайты. */
async function releaseTabs() {
  for (const tab of await api.tabs.query({})) {
    const target = blockedTarget(tab.url, BlockedPage);
    if (target) {
      await updateTab(tab.id, target);
    }
  }
}

async function updateTab(id, url) {
  try {
    await api.tabs.update(id, { url });
  } catch (error) {
    console.warn("ADHDWindows: вкладка недоступна", error);
  }
}

/** Заголовки активных вкладок: по ним программа отличает окна, которые расширение не видит, например инкогнито. */
function reportWindows() {
  clearTimeout(reportTimer);
  reportTimer = setTimeout(async () => {
    if (!port || !online) {
      return;
    }

    const windows = await api.windows.getAll({ populate: true });
    const titles = windows
      .map((window) => window.tabs?.find((tab) => tab.active)?.title)
      .filter((title) => typeof title === "string" && title.length > 0);
    post({ type: "windows", titles });
  }, ReportDelay);
}

/** Снимки плееров всех вкладок. Видео засчитывается, только если вкладка выбрана в окне, которое в фокусе. */
function reportMedia() {
  clearTimeout(mediaTimer);
  mediaTimer = setTimeout(async () => {
    if (!port || !online) {
      return;
    }

    const now = Date.now();
    const tabs = [];
    for (const [tabId, frames] of media) {
      for (const [frameId, frame] of frames) {
        if (now - frame.at > MediaLifetime) {
          frames.delete(frameId);
        }
      }

      if (frames.size === 0) {
        media.delete(tabId);
        continue;
      }

      let tab;
      try {
        tab = await api.tabs.get(tabId);
      } catch {
        media.delete(tabId);
        continue;
      }

      tabs.push({
        tabId,
        url: tab.url ?? "",
        focused: tab.active && tab.windowId === focusedWindow,
        media: [...frames.values()].flatMap((frame) => frame.media),
      });
    }

    post({ type: "media", tabs });
  }, MediaDelay);
}

async function trackFocus() {
  try {
    const window = await api.windows.getLastFocused();
    focusedWindow = window?.focused ? window.id : api.windows.WINDOW_ID_NONE;
  } catch {
    focusedWindow = api.windows.WINDOW_ID_NONE;
  }
}

/** Вкладки, открытые до установки расширения, получают отслеживание плееров без перезагрузки. */
async function injectMediaScripts() {
  if (!api.scripting) {
    return;
  }

  for (const tab of await api.tabs.query({ url: ["http://*/*", "https://*/*"] })) {
    try {
      await api.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, files: ["media-hook.js"], world: "MAIN" });
      await api.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, files: ["media.js"] });
      await api.scripting.executeScript({ target: { tabId: tab.id }, files: ["page.js"] });
    } catch {
      // Служебные страницы и магазин расширений недоступны.
    }
  }
}

function browserName() {
  const brands = navigator.userAgentData?.brands?.map((brand) => brand.brand) ?? [];
  for (const [brand, name] of [["Microsoft Edge", "edge"], ["YaBrowser", "yandex"], ["Yandex", "yandex"], ["Google Chrome", "chrome"]]) {
    if (brands.includes(brand)) {
      return name;
    }
  }

  return navigator.userAgent.includes("Firefox") ? "firefox" : "chromium";
}

// Страница покупки спрашивает цену и просит купить через фоновую часть: только у неё есть связь с программой.
api.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!sender.url?.startsWith(BlockedPage) || (message?.type !== "offer" && message?.type !== "unlock")) {
    return false;
  }

  const payload = message.type === "offer" ? { type: "offer", url: String(message.url) } : { type: "unlock", key: String(message.key) };
  request(payload).then(sendResponse);
  return true;
});

// Снимки плееров от контент-скриптов.
api.runtime.onMessage.addListener((message, sender) => {
  if (message?.type !== "media" || !sender.tab || sender.id !== api.runtime.id) {
    return false;
  }

  const items = Array.isArray(message.media) ? message.media.slice(0, 16) : [];
  let frames = media.get(sender.tab.id);
  if (!frames) {
    frames = new Map();
    media.set(sender.tab.id, frames);
  }

  // Номера элементов уникальны только внутри фрейма.
  const frameId = sender.frameId ?? 0;
  frames.set(frameId, { at: Date.now(), media: items.map((item) => ({ ...item, id: `${frameId}:${item.id}` })) });
  reportMedia();
  return false;
});

// Скрипт страницы: вопрос о слежении и сообщения о выделении и прокрутке. Принимаются только из верхнего фрейма.
api.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if ((message?.type !== "page-watch" && message?.type !== "page") || !sender.tab || sender.id !== api.runtime.id || (sender.frameId ?? 0) !== 0) {
    return false;
  }

  if (message.type === "page-watch") {
    sendResponse({ watch: watchOf(sender.tab.url) });
    return false;
  }

  // Адрес из сообщения должен быть страницей вкладки: страница могла уже смениться.
  const url = String(message.url ?? "");
  const config = watchOf(url);
  if (!config || pageOf(url) !== pageOf(sender.tab.url)) {
    return false;
  }

  if (message.event === "select" && config.select) {
    const text = String(message.text ?? "").trim().slice(0, 200);
    if (text.length > 0) {
      reportPage(sender.tab, { event: "select", text }, url);
    }
  } else if (message.event === "scroll" && config.scroll) {
    const share = Number(message.share);
    if (Number.isFinite(share)) {
      reportPage(sender.tab, { event: "scroll", share: Math.min(1, Math.max(0, share)) }, url);
    }
  }

  return false;
});

api.windows.onFocusChanged.addListener((windowId) => {
  focusedWindow = windowId;
  reportMedia();
});
api.tabs.onActivated.addListener(reportMedia);
api.tabs.onRemoved.addListener((tabId) => {
  if (media.delete(tabId)) {
    reportMedia();
  }
});

api.tabs.onUpdated.addListener((tabId, change) => {
  if (change.title !== undefined || change.status === "complete") {
    reportWindows();
  }
});

// Смена адреса вкладки, в том числе внутри одностраничного сайта без загрузки (SoundCloud). Правила браузера
// такие переходы не видят, поэтому закрытая страница сразу показывает экран покупки: пропуск не уводит дальше своей страницы.
// Посещение сообщается после загрузки и после смены адреса без загрузки. Повтор того же адреса
// (одностраничные сайты часто переписывают его на тот же) не сообщается.
const visited = new Map();
api.tabs.onUpdated.addListener((tabId, change, tab) => {
  if (change.url !== undefined) {
    queue = queue.then(() => enforceTab(tabId, change.url)).catch((error) => console.error("ADHDWindows", error));
    if (visited.get(tabId) !== change.url) {
      tellWatcher(tabId, change.url);
      if (tab.status === "complete") {
        visited.set(tabId, change.url);
        reportPage(tab, { event: "visit" }, change.url);
      }
    }
  } else if (change.status === "complete" && visited.get(tabId) !== tab.url) {
    visited.set(tabId, tab.url);
    reportPage(tab, { event: "visit" });
  }
});
api.tabs.onRemoved.addListener((tabId) => visited.delete(tabId));
api.tabs.onActivated.addListener(reportWindows);
api.tabs.onRemoved.addListener(reportWindows);
api.windows.onCreated.addListener(reportWindows);
api.windows.onRemoved.addListener(reportWindows);
api.windows.onFocusChanged.addListener(reportWindows);

// Запасной будильник: фоновая часть могла быть выгружена браузером без связи с мостом.
api.alarms.create("reconnect", { periodInMinutes: 0.5 });
api.alarms.onAlarm.addListener(connect);
api.runtime.onStartup.addListener(connect);
api.runtime.onInstalled.addListener(connect);
api.runtime.onInstalled.addListener(injectMediaScripts);
trackFocus();
connect();
