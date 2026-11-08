// Сквозная проверка блокировки сайтов в настоящем браузере на базе Chromium.
// Запуск: node tools/browser-check/check.mjs <путь к браузеру> [имя]
//
// Скрипт запускает собранную программу ADHDWindows в режиме разработчика со своей папкой данных и своим каналом,
// поднимает локальный сайт на 127.0.0.1 и загружает расширение через протокол отладки (Extensions.loadUnpacked):
// в новых версиях Chrome флаг --load-extension больше не работает. Окна открываются на втором мониторе.
// Настоящие профили браузера и настоящие сохранения программы не затрагиваются.
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
const Temp = fs.mkdtempSync(path.join(process.env.ADHDWINDOWS_CHECK_TEMP ?? os.tmpdir(), "adhd-browser-"));
const Bridge = `ADHDWindows.Browser.check-${process.pid}`;
const Screen = process.env.ADHDWINDOWS_TEST_SCREEN ?? "2720,624";
const [ScreenX, ScreenY] = Screen.split(",").map(Number);
const BlockedPage = `chrome-extension://${ExtensionId}/blocked.html`;

const results = [];
const started = [];
const requests = [];

function report(name, ok, detail = "") {
  results.push({ name, ok, detail });
  console.log(`${ok ? "OK  " : "FAIL"} ${name}${detail ? ": " + detail : ""}`);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function until(check, timeout = 15000, step = 200) {
  const end = Date.now() + timeout;
  let last;
  while (Date.now() < end) {
    try {
      last = await check();
      if (last) {
        return last;
      }
    } catch (error) {
      last = error;
    }

    await sleep(step);
  }

  return null;
}

// --- Локальный сайт ---------------------------------------------------------

const pixel = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64");
const server = http.createServer((request, response) => {
  const host = (request.headers.host ?? "").split(":")[0];
  requests.push(host + request.url);
  const cors = { "Access-Control-Allow-Origin": "*", "Cache-Control": "no-store" };
  if (request.url.startsWith("/pixel.png")) {
    response.writeHead(200, { ...cors, "Content-Type": "image/png" });
    response.end(pixel);
    return;
  }

  if (request.url.startsWith("/data")) {
    response.writeHead(200, { ...cors, "Content-Type": "application/json" });
    response.end('{"ok":true}');
    return;
  }

  const port = server.address().port;
  const body = request.url.startsWith("/resources")
    ? `<img id="img" src="http://cdn.locked.test:${port}/pixel.png">
       <iframe id="frame" src="http://frame.locked.test:${port}/frame"></iframe>
       <script>window.fetched = fetch("http://api.locked.test:${port}/data").then(r => r.json()).then(j => j.ok, () => false);</script>`
    : "";
  response.writeHead(200, { ...cors, "Content-Type": "text/html; charset=utf-8" });
  response.end(`<!doctype html><title>page ${host}</title><h1>${host}</h1>${body}`);
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const Port = server.address().port;
const site = (host, page = "/") => `http://${host}:${Port}${page}`;

// --- Программа --------------------------------------------------------------

function writeStore(file, values) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ schemaVersion: 1, values }));
}

function startApp(seed) {
  const user = path.join(Temp, "user");
  if (seed) {
    writeStore(path.join(user, "save.json"), { "economy/balance": 1000 });
  }

  writeStore(path.join(user, "settings.json"), {
    "safety/hotkey": "Ctrl+Alt+Shift+F8",
    "modules/locks/onlyUnder": path.join(Temp, "programs"),
    "modules/locks/steamPath": path.join(Temp, "steam"),
    "modules/locks/epicManifests": path.join(Temp, "epic"),
    "modules/locks/browserProfile": Temp,
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
    ? fs.readdirSync(folder).map((name) => fs.readFileSync(path.join(folder, name), "utf8")).join("\n")
    : "";
}

function kill(child) {
  try {
    execFileSync("taskkill", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore" });
  } catch {
    // Уже закрыт.
  }
}

// --- Браузер и протокол отладки ----------------------------------------------

class Cdp {
  constructor(child) {
    this.input = child.stdio[3];
    this.nextId = 1;
    this.pending = new Map();
    this.events = [];
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
        } else {
          this.events.push(message);
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

function startBrowser(profile, extra = []) {
  const child = spawn(
    browserExe,
    [
      "--remote-debugging-pipe",
      "--enable-unsafe-extension-debugging",
      `--user-data-dir=${profile}`,
      "--no-first-run",
      "--no-default-browser-check",
      "--disable-search-engine-choice-screen",
      `--window-position=${ScreenX - 700},${ScreenY - 380}`,
      "--window-size=1100,760",
      "--host-resolver-rules=MAP *.test 127.0.0.1, MAP claude.ai 127.0.0.1, MAP *.claude.ai 127.0.0.1",
      ...extra,
      "about:blank",
    ],
    { stdio: ["ignore", "ignore", "ignore", "pipe", "pipe"], env: { ...process.env, ADHDWINDOWS_BRIDGE: Bridge } },
  );
  started.push(child);
  return { child, cdp: new Cdp(child) };
}

async function attachPage(cdp, predicate = () => true) {
  // Только что запущенный браузер может ещё не открыть ни одной вкладки.
  const pages = await until(async () => {
    const { targetInfos } = await cdp.send("Target.getTargets");
    const found = targetInfos.filter((info) => info.type === "page");
    return found.length > 0 ? found : null;
  }, 15000, 300) ?? [];
  // Некоторые браузеры вместо about:blank открывают свою стартовую страницу.
  const target = pages.find(predicate) ?? pages.find((info) => /^https?:|^about:/.test(info.url)) ?? pages[0];
  const { sessionId } = await cdp.send("Target.attachToTarget", { targetId: target.targetId, flatten: true });
  const evaluate = async (expression) =>
    (await cdp.send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true }, sessionId)).result.value;
  return {
    targetId: target.targetId,
    sessionId,
    evaluate,
    href: () => evaluate("location.href"),
    navigate: (url) => cdp.send("Page.navigate", { url }, sessionId),
  };
}

async function windowState(cdp, targetId) {
  const { bounds } = await cdp.send("Browser.getWindowForTarget", { targetId });
  return bounds.windowState;
}

// --- Проверки ----------------------------------------------------------------

try {
  startApp(true);
  const profile = path.join(Temp, "profile");
  const { cdp } = startBrowser(profile);
  const loaded = await cdp.send("Extensions.loadUnpacked", { path: ExtensionDir });
  report("расширение загружено с постоянным кодом", loaded.id === ExtensionId, loaded.id);

  const page = await attachPage(cdp, (info) => info.url === "about:blank");
  const blockedUrl = site("shop.blocked.test");
  const blocked = await until(async () => {
    await page.navigate(blockedUrl);
    return await until(async () => (await page.href()).startsWith(BlockedPage), 3000);
  }, 30000, 1000);
  report("закрытый сайт показывает экран покупки", Boolean(blocked), await page.href());

  const status = await until(async () => {
    const text = await page.evaluate("document.getElementById('status').textContent");
    return text.includes("100") ? text : null;
  });
  report("экран покупки показывает цену и баланс", Boolean(status), status ?? await page.evaluate("document.getElementById('status').textContent + ' | ' + document.title"));

  if (process.env.ADHDWINDOWS_CHECK_SHOT) {
    const { data } = await cdp.send("Page.captureScreenshot", { format: "png" }, page.sessionId);
    fs.writeFileSync(path.join(process.env.ADHDWINDOWS_CHECK_SHOT, `${label}-blocked.png`), Buffer.from(data, "base64"));
  }

  const rect = await page.evaluate("JSON.stringify(document.getElementById('buy').getBoundingClientRect())");
  const keyboard = await page.evaluate("document.getElementById('buy').focus(); document.getElementById('buy').dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter'})); document.getElementById('buy').click(); new Promise(r => setTimeout(() => r(location.href), 1500))");
  report("нажатие без мыши не покупает", keyboard.startsWith(BlockedPage));

  const box = JSON.parse(rect);
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  for (const type of ["mousePressed", "mouseReleased"]) {
    await cdp.send("Input.dispatchMouseEvent", { type, x, y, button: "left", clickCount: 1 }, page.sessionId);
  }

  const opened = await until(async () => (await page.href()) === blockedUrl);
  report("после покупки сайт открывается", Boolean(opened), await page.href());

  await page.navigate(site("news.blocked.test"));
  await sleep(1500);
  report("поддомен купленного сайта открыт", (await page.href()).startsWith(site("news.blocked.test")), await page.href());

  await page.navigate(site("claude.ai"));
  await sleep(1500);
  const claude = await page.href();
  report("claude.ai доступен всегда", !claude.startsWith(BlockedPage) && new URL(claude).hostname === "claude.ai", claude);

  await page.navigate(site("app.claude.ai", "/resources"));
  const resources = await until(async () =>
    page.evaluate("document.readyState === 'complete' && window.fetched ? window.fetched.then(ok => ok && document.getElementById('img').naturalWidth > 0) : false"));
  const framed = requests.some((item) => item.startsWith("frame.locked.test/frame"));
  report("картинки, запросы и фреймы закрытого сайта внутри открытой страницы работают", Boolean(resources) && framed,
    `картинка и запрос: ${Boolean(resources)}, фрейм: ${framed}`);

  // Инкогнито без разрешения: окно сворачивается, программа просит включить разрешение.
  const privateFlag = browserExe.toLowerCase().includes("msedge") ? "--inprivate" : "--incognito";
  const incognito = spawn(browserExe, [`--user-data-dir=${profile}`, privateFlag, site("private.test", "/incognito")], { stdio: "ignore" });
  started.push(incognito);
  const privatePage = await until(async () => {
    const { targetInfos } = await cdp.send("Target.getTargets");
    return targetInfos.find((info) => info.type === "page" && info.url.includes("/incognito"));
  });
  const problem = await until(() => appLog().includes("(Incognito)"), 30000, 500);
  const privateState = privatePage ? await until(async () => (await windowState(cdp, privatePage.targetId)) === "minimized" ? "minimized" : null, 5000) : null;
  report("окно инкогнито без разрешения свёрнуто и показана просьба", Boolean(problem) && privateState === "minimized",
    `запись: ${Boolean(problem)}, окно: ${privateState}`);
  const mainState = await windowState(cdp, page.targetId);
  report("обычное окно при этом не свёрнуто", mainState !== "minimized", mainState);
  if (privatePage) {
    await cdp.send("Target.closeTarget", { targetId: privatePage.targetId });
  }

  // Выход из программы: закрытые вкладки открываются, как после разморозки программ.
  const lockedUrl = site("locked.test");
  await page.navigate(lockedUrl);
  const lockedNow = await until(async () => (await page.href()).startsWith(BlockedPage));
  started.filter((child) => child.spawnfile?.endsWith("ADHDWindows.exe")).forEach(kill);
  const released = await until(async () => (await page.href()) === lockedUrl, 15000);
  report("после закрытия программы вкладка возвращается на сайт", Boolean(lockedNow) && Boolean(released), await page.href());

  // Браузер без расширения: окно сворачивается после ожидания подключения.
  startApp(false);
  const reopened = await until(async () => {
    await page.navigate(site("again.locked.test"));
    return await until(async () => (await page.href()).startsWith(BlockedPage), 3000);
  }, 30000, 1000);
  await page.navigate(blockedUrl);
  await sleep(1500);
  report("после перезапуска программы купленный сайт открыт, а некупленный снова закрыт",
    Boolean(reopened) && (await page.href()) === blockedUrl, await page.href());

  const bare = startBrowser(path.join(Temp, "bare"));
  const barePage = await attachPage(bare.cdp, (info) => info.url === "about:blank");
  await barePage.navigate(site("bare.test"));
  const missing = await until(() => appLog().includes("(Missing)"), 40000, 500);
  if (process.env.ADHDWINDOWS_CHECK_SHOT && missing) {
    await sleep(1500);
    const file = path.join(process.env.ADHDWINDOWS_CHECK_SHOT, `${label}-extension-screen.png`);
    const command = "Add-Type -AssemblyName System.Windows.Forms,System.Drawing;"
      + `$s=[System.Windows.Forms.Screen]::AllScreens | ? { $_.Bounds.Contains(${ScreenX},${ScreenY}) } | select -First 1;`
      + "$b=New-Object System.Drawing.Bitmap $s.Bounds.Width,$s.Bounds.Height;$g=[System.Drawing.Graphics]::FromImage($b);"
      + `$g.CopyFromScreen($s.Bounds.Location,[System.Drawing.Point]::Empty,$s.Bounds.Size);$b.Save('${file}')`;
    execFileSync("powershell", ["-NoProfile", "-Command", command], { stdio: "ignore" });
  }

  const bareState = await until(async () => (await windowState(bare.cdp, barePage.targetId)) === "minimized" ? "minimized" : null, 5000);
  report("браузер без расширения свёрнут и показана просьба", Boolean(missing) && bareState === "minimized",
    `запись: ${Boolean(missing)}, окно: ${bareState}`);
} catch (error) {
  report("проверка завершилась ошибкой", false, error.stack ?? String(error));
} finally {
  if (results.some((result) => !result.ok)) {
    const lines = appLog().split(/\r?\n/).filter((line) => /Окно без вкладки|Браузер без|Расширение/.test(line));
    console.log(lines.join("\n"));
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
