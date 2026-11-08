// Сквозная проверка монеток за хронометраж в настоящем браузере на базе Chromium.
// Запуск: node tools/browser-check/media-check.mjs <путь к браузеру> [имя]
//
// Как и check.mjs, скрипт запускает собранную программу с --dev, своей папкой данных и своим каналом,
// локальный сайт и браузер с временным профилем на втором мониторе. Сайты проверки задаются настройкой
// modules/time/testSites: 127.0.0.1 считается музыкой, localhost — видео. Оба бесплатны, покупать их не нужно.
// Начисления сверяются по строкам «Хронометраж всего» в журнале программы: в режиме проверки они пишутся при каждом зачислении.
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";

const [browserExe, label = path.basename(process.argv[2] ?? "browser")] = process.argv.slice(2);
if (!browserExe) {
  console.error("Укажите путь к браузеру");
  process.exit(2);
}

const ExtensionId = "diodcdmoopmnbkgblgffcbbhompfhpgg";
const Repo = path.resolve(import.meta.dirname, "..", "..");
const AppDir = path.join(Repo, "src", "ADHDWindows.App", "bin", "Debug", "net10.0-windows");
const ExtensionDir = path.join(AppDir, "browser-extension", "chromium");
const Temp = fs.mkdtempSync(path.join(process.env.ADHDWINDOWS_CHECK_TEMP ?? os.tmpdir(), "adhd-media-"));
const Bridge = `ADHDWindows.Browser.media-${process.pid}`;
const Screen = process.env.ADHDWINDOWS_TEST_SCREEN ?? "2720,624";
const [ScreenX, ScreenY] = Screen.split(",").map(Number);

// Музыка: 2 монетки в минуту, то есть 1/30 в секунду.
const MusicPerSecond = 2 / 60;

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

// --- Локальный сайт: тон в WAV и страницы с плеерами ---------------------------

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
    // Тихий тон: звук идёт, но не мешает.
    data[44 + index] = 128 + Math.round(4 * Math.sin((2 * Math.PI * 440 * index) / rate));
  }

  return data;
}

const tones = new Map();
const server = http.createServer((request, response) => {
  const url = new URL(request.url, "http://local");
  if (url.pathname === "/tone.wav") {
    const seconds = Number(url.searchParams.get("seconds") ?? 60);
    if (!tones.has(seconds)) {
      tones.set(seconds, tone(seconds));
    }

    const body = tones.get(seconds);
    const range = /bytes=(\d+)-(\d*)/.exec(request.headers.range ?? "");
    if (range) {
      const start = Number(range[1]);
      const end = range[2] ? Number(range[2]) : body.length - 1;
      response.writeHead(206, {
        "Content-Type": "audio/wav",
        "Accept-Ranges": "bytes",
        "Content-Range": `bytes ${start}-${end}/${body.length}`,
        "Content-Length": end - start + 1,
      });
      response.end(body.subarray(start, end + 1));
    } else {
      response.writeHead(200, { "Content-Type": "audio/wav", "Accept-Ranges": "bytes", "Content-Length": body.length });
      response.end(body);
    }

    return;
  }

  const seconds = Number(url.searchParams.get("seconds") ?? 600);
  const title = url.searchParams.get("title") ?? "Трек";
  let player = `<audio id="player" src="/tone.wav?seconds=${seconds}"></audio>`;
  if (url.pathname === "/video") {
    player = `<video id="player" src="/tone.wav?seconds=${seconds}"></video>`;
  } else if (url.pathname === "/detached") {
    // Как на SoundCloud: элемента нет в документе.
    player = `<script>window.player = new Audio("/tone.wav?seconds=${seconds}");</script>`;
  }

  response.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
  response.end(`<!doctype html><title>${title}</title><h1>${title}</h1>${player}
    <script>window.player ??= document.getElementById("player");</script>`);
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const Port = server.address().port;
const music = (page = "/music", query = "") => `http://127.0.0.1:${Port}${page}${query ? "?" + query : ""}`;
const video = (query = "") => `http://localhost:${Port}/video${query ? "?" + query : ""}`;

// --- Программа ----------------------------------------------------------------

function writeStore(file, values) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ schemaVersion: 1, values }));
}

function startApp() {
  const user = path.join(Temp, "user");
  writeStore(path.join(user, "settings.json"), {
    "safety/hotkey": "Ctrl+Alt+Shift+F8",
    "modules/locks/onlyUnder": path.join(Temp, "programs"),
    "modules/locks/steamPath": path.join(Temp, "steam"),
    "modules/locks/epicManifests": path.join(Temp, "epic"),
    "modules/locks/browserProfile": Temp,
    "modules/time/testSites": "127.0.0.1=music, localhost=video",
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

/** Строки «Хронометраж всего» с временем: [{ at, video, music, game }]. */
function totals() {
  const lines = [];
  for (const match of appLog().matchAll(/^(\d\d):(\d\d):(\d\d)\.(\d{3}) INFO \[time\] Хронометраж всего: video=([\d.]+) music=([\d.]+) game=([\d.]+)/gm)) {
    const at = ((Number(match[1]) * 60 + Number(match[2])) * 60 + Number(match[3])) * 1000 + Number(match[4]);
    lines.push({ at, video: Number(match[5]), music: Number(match[6]), game: Number(match[7]) });
  }

  return lines;
}

/** Начисления за следующие ~seconds секунд: разница между двумя зачислениями и время между ними. */
async function measure(seconds) {
  const count = totals().length;
  const first = await until(() => totals().length > count && totals().at(-1), 20000, 250);
  const target = Math.max(1, Math.round(seconds / 10));
  const last = await until(() => totals().length >= count + 1 + target && totals().at(-1), seconds * 1000 + 25000, 250);
  if (!first || !last) {
    return null;
  }

  const span = (last.at - first.at) / 1000;
  return { span, video: last.video - first.video, music: last.music - first.music, game: last.game - first.game };
}

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
      `--window-position=${ScreenX - 700},${ScreenY - 380}`,
      "--window-size=1100,760",
      "about:blank",
    ],
    { stdio: ["ignore", "ignore", "ignore", "pipe", "pipe"], env: { ...process.env, ADHDWINDOWS_BRIDGE: Bridge } },
  );
  started.push(child);
  return { child, cdp: new Cdp(child) };
}

async function attach(cdp, targetId) {
  const { sessionId } = await cdp.send("Target.attachToTarget", { targetId, flatten: true });
  const evaluate = async (expression) =>
    (await cdp.send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true }, sessionId)).result.value;
  return {
    targetId,
    evaluate,
    navigate: async (url) => {
      await cdp.send("Page.navigate", { url }, sessionId);
      await until(() => evaluate("document.readyState === 'complete' && Boolean(window.player)"), 15000);
    },
    // Плеер запускается из скрипта страницы, как это делают сайты.
    play: () => evaluate("window.player.play().then(() => true, (error) => String(error))"),
    activate: () => cdp.send("Target.activateTarget", { targetId }),
  };
}

async function firstPage(cdp) {
  const pages = await until(async () => {
    const { targetInfos } = await cdp.send("Target.getTargets");
    const found = targetInfos.filter((info) => info.type === "page");
    return found.length > 0 ? found : null;
  }, 15000, 300) ?? [];
  return pages.find((info) => info.url === "about:blank") ?? pages[0];
}

const near = (value, expected, tolerance) => value !== null && Math.abs(value - expected) <= tolerance;
const show = (sample) => (sample ? `за ${sample.span.toFixed(1)} с: музыка ${sample.music.toFixed(3)}, видео ${sample.video.toFixed(3)}` : "нет зачислений");

// --- Проверки -------------------------------------------------------------------

try {
  startApp();
  const { cdp } = startBrowser(path.join(Temp, "profile"));
  const loaded = await cdp.send("Extensions.loadUnpacked", { path: ExtensionDir });
  report("расширение загружено", loaded.id === ExtensionId, loaded.id);
  await until(() => appLog().includes("Расширение подключилось"), 30000, 500);

  const tab = await attach(cdp, (await firstPage(cdp)).targetId);
  await tab.navigate(music("/music", "seconds=900&title=Long"));
  const playing = await tab.play();

  // Вторая вкладка выбрана, вкладка с музыкой играет фоном.
  const { targetId: otherId } = await cdp.send("Target.createTarget", { url: "about:blank" });
  const other = await attach(cdp, otherId);
  await other.activate();

  const background = await measure(30);
  report("музыка в фоновой вкладке приносит 2 монетки в минуту",
    playing === true && background && near(background.music / background.span, MusicPerSecond, MusicPerSecond * 0.2), `${playing}; ${show(background)}`);

  await tab.evaluate("window.player.pause(), true");
  await sleep(3000);
  const paused = await measure(20);
  report("пауза останавливает начисление", paused && paused.music < 0.02, show(paused));

  await tab.evaluate("window.player.playbackRate = 2, true");
  await tab.play();
  const fast = await measure(30);
  report("ускорение вдвое не даёт лишних монет",
    fast && near(fast.music / fast.span, MusicPerSecond, MusicPerSecond * 0.2), show(fast));

  await tab.evaluate("window.player.playbackRate = 1, true");
  await tab.evaluate("window.seeker = setInterval(() => { window.player.currentTime += 5; }, 1000), true");
  const seeking = await measure(20);
  await tab.evaluate("clearInterval(window.seeker), true");
  report("перемотка не засчитывается", seeking && seeking.music < 0.05, show(seeking));

  await tab.evaluate("window.player.muted = true, true");
  await sleep(3000);
  const muted = await measure(20);
  await tab.evaluate("window.player.muted = false, window.player.pause(), true");
  report("выключенный звук останавливает начисление", muted && muted.music < 0.02, show(muted));

  // Элемент вне документа, как у SoundCloud.
  await tab.navigate(music("/detached", "seconds=900&title=Detached"));
  await tab.play();
  const detached = await measure(20);
  await tab.evaluate("window.player.pause(), true");
  report("звук без элемента в документе тоже считается",
    detached && near(detached.music / detached.span, MusicPerSecond, MusicPerSecond * 0.25), show(detached));

  // Досмотр: трек на 25 секунд целиком.
  await tab.navigate(music("/music", "seconds=25&title=Short"));
  await tab.play();
  const completed = await until(() => appLog().includes("Досмотрено (music)"), 45000, 500);
  report("трек, дослушанный до конца, даёт бонус", Boolean(completed));

  // Повторы: один и тот же трек с начала четыре раза подряд.
  await tab.navigate(music("/music", "seconds=600&title=Repeat"));
  for (let play = 0; play < 4; play++) {
    await tab.evaluate("window.player.currentTime = 0, true");
    await tab.play();
    await sleep(9000);
  }

  await tab.evaluate("window.player.pause(), true");
  report("четвёртый повтор подряд приносит меньше", /Повтор подряд №4 \(music\)/.test(appLog()));

  // Видео: вкладка не выбрана — монет нет. Выбрана, но браузер не активное окно — тоже нет.
  await tab.navigate(video("seconds=900"));
  await tab.play();
  const hidden = await measure(20);
  report("видео в невыбранной вкладке не приносит монет", hidden && hidden.video < 0.02, show(hidden));

  await tab.activate();
  const front = execFileSync("powershell", ["-NoProfile", "-Command",
    "Add-Type -Name W -Namespace N -MemberDefinition '[DllImport(\"user32.dll\")] public static extern System.IntPtr GetForegroundWindow(); [DllImport(\"user32.dll\")] public static extern int GetWindowThreadProcessId(System.IntPtr h, out int p);';"
    + "$p=0; [void][N.W]::GetWindowThreadProcessId([N.W]::GetForegroundWindow(), [ref]$p); (Get-Process -Id $p).ProcessName"]).toString().trim();
  const selected = await measure(20);
  const browserName = path.basename(browserExe, ".exe").toLowerCase();
  if (front.toLowerCase() === browserName) {
    report("видео в активном окне приносит 6 монеток в минуту", selected && near(selected.video / selected.span, 6 / 60, 0.02), show(selected));
  } else {
    report("видео в выбранной вкладке за другим активным окном не приносит монет", selected && selected.video < 0.02, `активно: ${front}; ${show(selected)}`);
  }
} catch (error) {
  report("проверка завершилась ошибкой", false, error.stack ?? String(error));
} finally {
  if (results.some((result) => !result.ok)) {
    console.log(appLog().split(/\r?\n/).filter((line) => /\[time\]|Расширение|ERROR/.test(line)).slice(-40).join("\n"));
  }

  for (const child of started.reverse()) {
    kill(child);
  }

  server.close();
  await sleep(500);
  try {
    fs.rmSync(Temp, { recursive: true, force: true });
  } catch {
    // Файлы браузера могут быть ещё заняты.
  }
}

const failed = results.filter((result) => !result.ok);
console.log(`\n${label}: ${results.length - failed.length} из ${results.length}`);
process.exit(failed.length === 0 ? 0 : 1);
