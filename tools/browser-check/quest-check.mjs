// Сквозная проверка заданий на страницах в настоящем браузере на базе Chromium.
// Запуск: node tools/browser-check/quest-check.mjs <путь к браузеру> [имя] [--live] [--wiki=<адрес>] [--track=<адрес>]
//
// Как check.mjs и media-check.mjs, скрипт запускает собранную программу с --dev, своей папкой данных и своим каналом
// и браузер с временным профилем на втором мониторе. Пул заданий заменяется настройкой modules/quests/testTemplates.
//
// Обычный режим: локальный сайт на 127.0.0.2 — статья со словом, вторая страница и страница с <audio> на 20 секунд.
// 127.0.0.1 и localhost всегда бесплатны, а 127.0.0.2 — обычный закрытый сайт, который при этом отвечает
// на проверку ссылки задания по сети. Задания: выделить слово (лёгкое), дочитать статью (среднее), прослушать трек (сложное).
//
// --live: те же шаги на настоящих страницах. Статья и слово — из шаблона select со словом «зелёный» в data/quests
// (или --wiki), трек — первый шаблон listen на SoundCloud в data/quests (или --track). Нужен интернет. SoundCloud без входа
// показывает баннер согласия и окно входа: скрипт отклоняет согласие, закрывает окно и нажимает «играть» мышью.
// Проверка перемотки в --live не выполняется: плеер SoundCloud недоступен скрипту страницы.
// Выполнение заданий сверяется по строкам «Выполнено задание» в журнале программы.
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";

const args = process.argv.slice(2);
const options = Object.fromEntries(args.filter((arg) => arg.startsWith("--")).map((arg) => {
  const [key, ...value] = arg.slice(2).split("=");
  return [key, value.length > 0 ? value.join("=") : true];
}));
const [browserExe, label = path.basename(args[0] ?? "browser")] = args.filter((arg) => !arg.startsWith("--"));
if (!browserExe) {
  console.error("Укажите путь к браузеру");
  process.exit(2);
}

const Live = Boolean(options.live);
const ExtensionId = "diodcdmoopmnbkgblgffcbbhompfhpgg";
const Repo = path.resolve(import.meta.dirname, "..", "..");
const AppDir = path.join(Repo, "src", "ADHDWindows.App", "bin", "Debug", "net10.0-windows");
const ExtensionDir = path.join(AppDir, "browser-extension", "chromium");
const Temp = fs.mkdtempSync(path.join(process.env.ADHDWINDOWS_CHECK_TEMP ?? os.tmpdir(), "adhd-quest-"));
const Bridge = `ADHDWindows.Browser.quest-${process.pid}`;
// В отличие от других проверок окно по умолчанию на основном экране: вне экрана Chrome считает вкладку скрытой,
// откладывает загрузку звука, а задание «дочитать» засчитывается только во вкладке в фокусе.
const Screen = process.env.ADHDWINDOWS_TEST_SCREEN ?? "700,380";
const [ScreenX, ScreenY] = Screen.split(",").map(Number);
const BlockedPage = `chrome-extension://${ExtensionId}/blocked.html`;
const TrackSeconds = 20;

const results = [];
const started = [];

function report(name, ok, detail = "") {
  results.push({ name, ok, detail });
  console.log(`${ok ? "OK  " : "FAIL"} ${name}${detail ? ": " + detail : ""}`);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function until(check, timeout = 15000, step = 200) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    try {
      const value = await check();
      if (value) {
        return value;
      }
    } catch {
      // Ещё не готово.
    }

    await sleep(step);
  }

  return null;
}

// --- Данные заданий ---------------------------------------------------------------

/** Шаблоны из data/quests (JSON с комментариями). */
function poolTemplates() {
  const folder = path.join(Repo, "data", "quests");
  const templates = [];
  for (const name of fs.existsSync(folder) ? fs.readdirSync(folder) : []) {
    if (!name.endsWith(".json")) {
      continue;
    }

    const text = fs.readFileSync(path.join(folder, name), "utf8").replace(/^\s*\/\/.*$/gm, "");
    try {
      templates.push(...(JSON.parse(text).templates ?? []));
    } catch (error) {
      console.warn(`Не удалось прочитать ${name}: ${error.message}`);
    }
  }

  return templates;
}

// --- Локальный сайт -------------------------------------------------------------

function tone(seconds) {
  const rate = 8000;
  const samples = rate * seconds;
  const data = Buffer.alloc(44 + samples);
  data.write("RIFF", 0);
  data.writeUInt32LE(36 + samples, 4);
  data.write("WAVEfmt ", 8);
  data.writeUInt32LE(16, 16);
  data.writeUInt16LE(1, 20);
  data.writeUInt16LE(1, 22);
  data.writeUInt32LE(rate, 24);
  data.writeUInt32LE(rate, 28);
  data.writeUInt16LE(1, 32);
  data.writeUInt16LE(8, 34);
  data.write("data", 36);
  data.writeUInt32LE(samples, 40);
  for (let index = 0; index < samples; index++) {
    data[44 + index] = 128 + Math.round(4 * Math.sin((2 * Math.PI * 440 * index) / rate));
  }

  return data;
}

const Paragraph = "Огурец — однолетнее травянистое растение семейства тыквенных. Плоды бывают разной формы и размера. ";
function articleHtml() {
  // Слово в середине длинной статьи: выделить его можно, не дочитав до конца.
  const filler = Array.from({ length: 40 }, (_, index) => `<p>${index + 1}. ${Paragraph.repeat(4)}</p>`).join("\n");
  const special = "<p id=\"word\">Плод огурца обычно <b>зелёный</b>, реже белый или жёлтый.</p>";
  return `<!doctype html><meta charset="utf-8"><title>Огурец — статья</title><h1>Огурец</h1>
    <p><a id="other" href="/other">Другая статья</a></p>${filler}${special}${filler}<p id="end">Конец статьи.</p>`;
}

let server = null;
let Origin = "";
if (!Live) {
  const wave = tone(TrackSeconds);
  server = http.createServer((request, response) => {
    const url = new URL(request.url, "http://local");
    const html = (body) => {
      response.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
      response.end(body);
    };

    if (url.pathname === "/tone.wav") {
      const range = /bytes=(\d+)-(\d*)/.exec(request.headers.range ?? "");
      if (range) {
        const start = Number(range[1]);
        const end = range[2] ? Number(range[2]) : wave.length - 1;
        response.writeHead(206, {
          "Content-Type": "audio/wav",
          "Accept-Ranges": "bytes",
          "Content-Range": `bytes ${start}-${end}/${wave.length}`,
          "Content-Length": end - start + 1,
        });
        response.end(wave.subarray(start, end + 1));
      } else {
        response.writeHead(200, { "Content-Type": "audio/wav", "Accept-Ranges": "bytes", "Content-Length": wave.length });
        response.end(wave);
      }
    } else if (url.pathname === "/article") {
      html(articleHtml());
    } else if (url.pathname === "/track") {
      html(`<!doctype html><meta charset="utf-8"><title>Тестовый трек</title><h1>Тестовый трек</h1>
        <audio id="player" src="/tone.wav" controls></audio><script>window.player = document.getElementById("player");</script>`);
    } else {
      html(`<!doctype html><meta charset="utf-8"><title>Другая страница</title><h1>Другая страница ${url.pathname}</h1>`);
    }
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.2", resolve));
  Origin = `http://127.0.0.2:${server.address().port}`;
}

const cucumber = poolTemplates().find((item) => item.type === "select" && /зел[её]ный/i.test(item.word ?? ""));
const soundcloud = poolTemplates().find((item) => item.type === "listen" && /soundcloud\.com/.test(item.url ?? ""));
const Pages = Live
  ? {
    article: options.wiki ?? cucumber?.url ?? "https://ru.wikipedia.org/wiki/Огурец_обыкновенный",
    word: cucumber?.word ?? "зелёный",
    other: "https://ru.wikipedia.org/wiki/Помидор",
    track: options.track ?? soundcloud?.url,
    trackMinutes: soundcloud?.goal ?? 5,
  }
  : {
    article: `${Origin}/article`,
    word: "зелёный",
    other: `${Origin}/other`,
    track: `${Origin}/track`,
    trackMinutes: TrackSeconds / 60,
  };

if (!Pages.track) {
  console.error("Нет трека SoundCloud: добавьте шаблон listen в data/quests или передайте --track=<адрес>");
  process.exit(2);
}

const Templates = [
  { id: "check.select", tier: "easy", type: "select", url: Pages.article, word: Pages.word, reward: [20, 20], title: "Выдели слово «{word}»", icon: "star" },
  { id: "check.read", tier: "medium", type: "read", url: Pages.article, reward: [50, 50], title: "Дочитай статью", icon: "quest" },
  { id: "check.listen", tier: "hard", type: "listen", url: Pages.track, goal: Pages.trackMinutes, reward: [100, 100], title: "Прослушай трек", icon: "clock" },
];

// --- Программа ----------------------------------------------------------------

function writeStore(file, values) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ schemaVersion: 1, values }));
}

function startApp() {
  const user = path.join(Temp, "user");
  writeStore(path.join(user, "save.json"), { "economy/balance": 0 });
  writeStore(path.join(user, "settings.json"), {
    "safety/hotkey": "Ctrl+Alt+Shift+F8",
    "modules/locks/onlyUnder": path.join(Temp, "programs"),
    "modules/locks/steamPath": path.join(Temp, "steam"),
    "modules/locks/epicManifests": path.join(Temp, "epic"),
    "modules/locks/browserProfile": Temp,
    "modules/quests/testTemplates": Templates,
  });
  const app = spawn(path.join(AppDir, "ADHDWindows.exe"), ["--dev", `--user-dir=${user}`], {
    cwd: AppDir,
    env: { ...process.env, ADHDWINDOWS_BRIDGE: Bridge, ADHDWINDOWS_TEST_SCREEN: Screen },
    stdio: "ignore",
  });
  started.push(app);
  return app;
}

function appLog() {
  const folder = path.join(Temp, "user", "logs");
  return fs.existsSync(folder)
    ? fs.readdirSync(folder).sort().map((name) => fs.readFileSync(path.join(folder, name), "utf8")).join("\n")
    : "";
}

const issued = (id) => new RegExp(`Выдано задание ${id.replace(".", "\\.")} `).test(appLog());
const completed = (id) => new RegExp(`Выполнено задание ${id.replace(".", "\\.")} \\(\\w+\\): \\+(\\d+)`).exec(appLog());

function kill(child) {
  try {
    execFileSync("taskkill", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore" });
  } catch {
    // Уже закрыт.
  }
}

// --- Браузер и протокол отладки ------------------------------------------------

class Cdp {
  constructor(child) {
    this.input = child.stdio[3];
    this.nextId = 1;
    this.pending = new Map();
    let buffer = "";
    child.stdio[4].on("data", (chunk) => {
      buffer += chunk.toString("utf8");
      let end;
      while ((end = buffer.indexOf("\0")) >= 0) {
        const message = JSON.parse(buffer.slice(0, end));
        buffer = buffer.slice(end + 1);
        if (message.id && this.pending.has(message.id)) {
          const { resolve, reject } = this.pending.get(message.id);
          this.pending.delete(message.id);
          message.error ? reject(new Error(message.error.message)) : resolve(message.result);
        }
      }
    });
  }

  send(method, params = {}, sessionId = undefined) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.input.write(JSON.stringify({ id, method, params, sessionId }) + "\0");
      setTimeout(() => {
        if (this.pending.delete(id)) {
          reject(new Error(`${method}: нет ответа`));
        }
      }, 20000);
    });
  }
}

function startBrowser(profile) {
  const child = spawn(
    browserExe,
    [
      "--remote-debugging-pipe",
      "--enable-unsafe-extension-debugging",
      `--user-data-dir=${profile}`,
      "--no-first-run",
      "--no-default-browser-check",
      "--disable-search-engine-choice-screen",
      "--autoplay-policy=no-user-gesture-required",
      // Окно проверки могут перекрыть другие окна: тогда Chrome считает вкладку скрытой, и SoundCloud не запускает трек.
      "--disable-features=CalculateNativeWinOcclusion",
      "--disable-backgrounding-occluded-windows",
      "--disable-renderer-backgrounding",
      `--window-position=${ScreenX - 700},${ScreenY - 380}`,
      "--window-size=1100,760",
      "about:blank",
    ],
    { stdio: ["ignore", "ignore", "ignore", "pipe", "pipe"], env: { ...process.env, ADHDWINDOWS_BRIDGE: Bridge } },
  );
  started.push(child);
  return { child, cdp: new Cdp(child) };
}

async function attach(cdp) {
  const pages = await until(async () => {
    const { targetInfos } = await cdp.send("Target.getTargets");
    const found = targetInfos.filter((info) => info.type === "page");
    return found.length > 0 ? found : null;
  }, 15000, 300) ?? [];
  const target = pages.find((info) => info.url === "about:blank") ?? pages.find((info) => /^https?:|^about:/.test(info.url)) ?? pages[0];
  const { sessionId } = await cdp.send("Target.attachToTarget", { targetId: target.targetId, flatten: true });
  const evaluate = async (expression) =>
    (await cdp.send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true }, sessionId)).result.value;
  const href = async () => decodeURI(await evaluate("location.href"));
  const mouse = async (x, y) => {
    for (const type of ["mouseMoved", "mousePressed", "mouseReleased"]) {
      await cdp.send("Input.dispatchMouseEvent", { type, x, y, button: "left", clickCount: 1 }, sessionId);
    }
  };

  return {
    evaluate,
    href,
    mouse,
    front: () => cdp.send("Page.bringToFront", {}, sessionId),
    /** Переход и ожидание загрузки: адрес вкладки после всех перенаправлений. */
    navigate: async (url) => {
      await cdp.send("Page.navigate", { url }, sessionId);
      await sleep(300);
      await until(() => evaluate("document.readyState === 'complete'"), 20000);
      await sleep(700);
      return href();
    },
    /** Центр элемента по селектору или null. */
    center: (selector) => evaluate(`(() => { const r = document.querySelector(${JSON.stringify(selector)})?.getBoundingClientRect();
      return r && r.width > 0 ? [r.x + r.width / 2, r.y + r.height / 2] : null; })()`),
  };
}

/**
 * Окно браузера на передний план: задание «дочитать» засчитывается только во вкладке в фокусе.
 * Windows разрешает SetForegroundWindow фоновому процессу после нажатия Alt. Возвращает имя процесса активного окна.
 */
function focusBrowser(pid) {
  const script = "Add-Type -Name W -Namespace N -MemberDefinition '"
    + "[DllImport(\"user32.dll\")] public static extern bool SetForegroundWindow(System.IntPtr h);"
    + "[DllImport(\"user32.dll\")] public static extern void keybd_event(byte k, byte s, int f, int e);"
    + "[DllImport(\"user32.dll\")] public static extern System.IntPtr GetForegroundWindow();"
    + "[DllImport(\"user32.dll\")] public static extern int GetWindowThreadProcessId(System.IntPtr h, out int p);';"
    + `$h = (Get-Process -Id ${pid}).MainWindowHandle;`
    + "[N.W]::keybd_event(0x12, 0, 0, 0); [N.W]::keybd_event(0x12, 0, 2, 0); [void][N.W]::SetForegroundWindow($h); Start-Sleep -Milliseconds 300;"
    + "$p = 0; [void][N.W]::GetWindowThreadProcessId([N.W]::GetForegroundWindow(), [ref]$p); (Get-Process -Id $p).ProcessName";
  try {
    return execFileSync("powershell", ["-NoProfile", "-Command", script]).toString().trim();
  } catch (error) {
    return `ошибка: ${error.message}`;
  }
}

/** Запуск <audio> из скрипта страницы, не дольше 5 секунд. */
const play = (tab) => tab.evaluate("Promise.race([window.player.play().then(() => true, String), new Promise((r) => setTimeout(() => r('play: нет ответа'), 5000))])");

const isBlocked = (href) => href.startsWith(BlockedPage);
const same = (href, url) => href.split(/[?#]/)[0].replace(/\/+$/, "") === decodeURI(url).split(/[?#]/)[0].replace(/\/+$/, "");

/** Плавная прокрутка до конца, как у читающего человека. */
async function scrollToEnd(tab) {
  for (let step = 1; step <= 20; step++) {
    await tab.evaluate(`window.scrollTo(0, (document.scrollingElement.scrollHeight - innerHeight) * ${step / 20}), true`);
    await sleep(400);
  }
}

/** Выделяет первое вхождение слова в тексте страницы. */
function selectWord(tab, word) {
  return tab.evaluate(`(() => {
    const word = ${JSON.stringify(word)};
    const walker = document.createTreeWalker(document.querySelector("#mw-content-text") ?? document.body, NodeFilter.SHOW_TEXT);
    for (let node; (node = walker.nextNode()); ) {
      const index = node.data.indexOf(word);
      if (index >= 0) {
        node.parentElement.scrollIntoView({ block: "center" });
        const range = document.createRange();
        range.setStart(node, index);
        range.setEnd(node, index + word.length);
        getSelection().removeAllRanges();
        getSelection().addRange(range);
        return getSelection().toString();
      }
    }

    return null;
  })()`);
}

/** SoundCloud без входа: отклонить согласие, закрыть окно входа и нажать «играть». */
async function playSoundCloud(tab) {
  await sleep(3000);
  await tab.evaluate("document.querySelector('#onetrust-reject-all-handler')?.click(), true");
  await sleep(1500);
  const close = await tab.center(".modal__closeButton, button[title='Close']");
  if (close) {
    await tab.mouse(...close);
    await sleep(1000);
  }

  for (const selector of [".sc-button-play.playButton", ".playControls__play"]) {
    if (await tab.evaluate("navigator.mediaSession?.playbackState") === "playing") {
      break;
    }

    const button = await tab.center(selector);
    if (button) {
      await tab.mouse(...button);
      await sleep(3000);
    }
  }

  return tab.evaluate("navigator.mediaSession?.playbackState === 'playing'");
}

// --- Проверки -------------------------------------------------------------------

try {
  console.log(`Статья: ${Pages.article}\nТрек: ${Pages.track}`);
  startApp();
  const { child: browser, cdp } = startBrowser(path.join(Temp, "profile"));
  const loaded = await cdp.send("Extensions.loadUnpacked", { path: ExtensionDir });
  report("расширение загружено", loaded.id === ExtensionId, loaded.id);
  await until(() => appLog().includes("Расширение подключилось"), 30000, 500);

  // Прогрев модуля заданий 5 секунд и проверка ссылок по сети.
  const given = await until(() => Templates.every((item) => issued(item.id)), 60000, 500);
  report("задания на страницы выданы", Boolean(given), Templates.filter((item) => !issued(item.id)).map((item) => item.id).join(", "));

  const tab = await attach(cdp);
  await tab.front();

  // Сайт закрыт, но страницы заданий открыты по пропуску. Правила приходят в браузер не сразу, поэтому ждём.
  const other = await until(async () => isBlocked(await tab.navigate(Pages.other)) && (await tab.href()), 20000, 1000);
  report("другая страница сайта закрыта", Boolean(other), other || await tab.href());

  const article = await until(async () => {
    const href = await tab.navigate(Pages.article);
    return !isBlocked(href) && href;
  }, 20000, 1000);
  report("страница задания открыта по пропуску", Boolean(article) && same(article, Pages.article), article || await tab.href());

  // Переход внутри вкладки: обычная ссылка и смена адреса без загрузки, как на одностраничных сайтах.
  if (!Live) {
    await tab.evaluate("document.getElementById('other').click(), true");
    const linked = await until(async () => isBlocked(await tab.href()) && (await tab.href()), 10000);
    report("переход по ссылке на другую страницу сайта закрыт", Boolean(linked), linked || await tab.href());
    await tab.navigate(Pages.article);
  }

  await tab.evaluate("history.pushState({}, '', '/other-page'), true");
  const pushed = await until(async () => isBlocked(await tab.href()) && (await tab.href()), 10000);
  report("смена адреса без загрузки уводит на экран покупки", Boolean(pushed), pushed || await tab.href());

  const track = await tab.navigate(Pages.track);
  report("страница трека открыта по пропуску", !isBlocked(track), track);

  // Выделение слова.
  await tab.navigate(Pages.article);
  await tab.front();
  const selected = await selectWord(tab, Pages.word);
  const selectDone = await until(() => completed("check.select"), 15000, 500);
  report("выделение слова выполняет задание и начисляет награду", Boolean(selectDone), `выделено: ${selected}; ${selectDone?.[0] ?? "нет строки в журнале"}`);

  // Прокрутка до конца во вкладке в фокусе.
  await tab.front();
  const front = focusBrowser(browser.pid);
  await tab.evaluate("window.scrollTo(0, 0), true");
  await sleep(1500);
  await scrollToEnd(tab);
  const readDone = await until(() => completed("check.read"), 15000, 500);
  const scrolled = await tab.evaluate("JSON.stringify({ top: document.scrollingElement.scrollTop, inner: innerHeight, height: document.scrollingElement.scrollHeight, focus: document.hasFocus(), visible: document.visibilityState })");
  report("прокрутка до конца выполняет задание и начисляет награду", Boolean(readDone), `активное окно: ${front}; страница ${scrolled}; ${readDone?.[0] ?? "нет строки в журнале"}`);

  // Перемотка в конец не засчитывает прослушивание.
  await tab.navigate(Pages.track);
  if (!Live) {
    await until(() => tab.evaluate("window.player.readyState >= 1"), 10000);
    await play(tab);
    await sleep(2500);
    await tab.evaluate("window.player.currentTime = window.player.duration - 1, true");
    const ended = await until(() => tab.evaluate("window.player.ended"), 10000);
    await sleep(5000);
    report("перемотка в конец не засчитывает прослушивание", Boolean(ended) && !completed("check.listen"),
      `трек ${ended ? "доигран после перемотки" : "не доигран"}; ${completed("check.listen")?.[0] ?? "не засчитано"}`);

    await tab.navigate(Pages.track);
    await until(() => tab.evaluate("window.player.readyState >= 1"), 10000);
    const playing = await play(tab);
    console.log("     плеер:", await tab.evaluate("JSON.stringify({ ready: player.readyState, network: player.networkState, error: player.error?.message ?? player.error?.code, paused: player.paused, time: player.currentTime, duration: player.duration })"));
    const listenDone = await until(() => completed("check.listen"), (TrackSeconds + 20) * 1000, 500);
    report("полное прослушивание выполняет задание и начисляет награду", Boolean(listenDone), `${playing}; ${listenDone?.[0] ?? "нет строки в журнале"}`);
  } else {
    const playing = await playSoundCloud(tab);
    report("трек SoundCloud играет", playing);
    const listenDone = await until(() => completed("check.listen"), (Pages.trackMinutes * 60 + 60) * 1000, 1000);
    report("полное прослушивание трека SoundCloud выполняет задание", Boolean(listenDone), listenDone?.[0] ?? "нет строки в журнале");
  }

  const rewards = Templates.map((item) => Number(completed(item.id)?.[1] ?? 0));
  const lines = appLog().match(/Выполнено задание check\./g)?.length ?? 0;
  report("награды начислены по одной на задание", rewards.every((value) => value > 0) && lines === Templates.length,
    `награды ${rewards.join(", ")}; строк в журнале ${lines}`);
} catch (error) {
  report("проверка завершилась ошибкой", false, error.stack ?? String(error));
} finally {
  if (results.some((result) => !result.ok)) {
    console.log(appLog().split(/\r?\n/).filter((line) => /\[quests\]|\[locks\]|Расширение|ERROR|WARN/.test(line)).slice(-50).join("\n"));
  }

  for (const child of started.reverse()) {
    kill(child);
  }

  server?.close();
  await sleep(500);
  try {
    fs.rmSync(Temp, { recursive: true, force: true });
  } catch {
    // Файлы браузера могут быть ещё заняты.
  }
}

const failed = results.filter((result) => !result.ok);
console.log(`\n${label}${Live ? " (live)" : ""}: ${results.length - failed.length} из ${results.length}`);
process.exit(failed.length === 0 ? 0 : 1);
