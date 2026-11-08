// Фоновая часть расширения. Связь с программой идёт через мост native messaging.
// Пока программа не запущена, ничего не блокируется: так же, как после выхода из неё размораживаются программы.
import { blockedTarget, buildDnrRules, hostOf, isAllowed } from "./rules.js";

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
      await applyRules();
      await enforceTabs();
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
  for (const request of pending.values()) {
    clearTimeout(request.timer);
    request.resolve({ offline: true });
  }

  pending.clear();
  await setDnrRules([]);
  await releaseTabs();
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
  await setDnrRules(buildDnrRules(rules, BlockedPage));
}

async function setDnrRules(addRules) {
  const existing = await api.declarativeNetRequest.getDynamicRules();
  await api.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: existing.map((rule) => rule.id),
    addRules,
  });
}

/** Уже открытые вкладки закрытых сайтов закрываются экраном покупки, купленные открываются. */
async function enforceTabs() {
  for (const tab of await api.tabs.query({})) {
    const target = blockedTarget(tab.url, BlockedPage);
    if (target) {
      const host = hostOf(target);
      if (host && isAllowed(rules, host)) {
        await updateTab(tab.id, target);
      }

      continue;
    }

    const host = hostOf(tab.url);
    if (host && !isAllowed(rules, host)) {
      await updateTab(tab.id, BlockedPage + "#" + tab.url);
    }
  }
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
