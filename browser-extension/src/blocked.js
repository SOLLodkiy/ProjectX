// Экран покупки сайта. Адрес закрытой страницы передаётся после «#».
import { drawPanel, drawSprite, drawText, loadAtlas, loadFont, loadJson, loadPalette, measure, wrap } from "./pixel.js";

const api = globalThis.browser ?? globalThis.chrome;
const target = decodeTarget();
const canvas = document.getElementById("screen");
const buy = document.getElementById("buy");
const status = document.getElementById("status");
const context = canvas.getContext("2d");

const Width = 240;
const Padding = 8;
const ButtonHeight = 18;
const RefreshInterval = 3000;

let assets = null;
let offer = null;
let message = "";
let busy = false;
let frame = 0;
let button = null;

function decodeTarget() {
  const hash = location.href.indexOf("#");
  return hash < 0 ? "" : location.href.slice(hash + 1);
}

function format(template, ...values) {
  return template.replace(/\{(\d+)\}/g, (_, index) => String(values[Number(index)] ?? ""));
}

function text(key, ...values) {
  return format(assets.texts[key] ?? key, ...values);
}

/** Сокращение чисел как в HUD программы. */
function coins(value) {
  const units = [[1e9, "B"], [1e6, "M"], [1e3, "K"]];
  for (const [size, unit] of units) {
    if (value >= size * 10) {
      return Math.floor(value / size) + unit;
    }
  }

  return String(value);
}

async function ask(payload) {
  try {
    return (await api.runtime.sendMessage(payload)) ?? { offline: true };
  } catch {
    return { offline: true };
  }
}

/** Защита от петли: если сайт снова и снова закрывается сразу после открытия, страница остаётся на месте. */
function open() {
  const key = "adhdwindows.opened";
  const now = Date.now();
  let opened = [];
  try {
    opened = JSON.parse(sessionStorage.getItem(key) ?? "[]").filter((time) => now - time < 5000);
    opened.push(now);
    sessionStorage.setItem(key, JSON.stringify(opened));
  } catch {
    opened = [now];
  }

  if (opened.length > 3) {
    message = text("site.error");
    offer = null;
    render();
    return;
  }

  location.replace(target);
}

async function refresh() {
  const answer = await ask({ type: "offer", url: target });
  if (answer.offline) {
    message = text("site.offline");
    offer = null;
    render();
    setTimeout(open, 600);
    return;
  }

  if (answer.free) {
    open();
    return;
  }

  offer = answer;
  render();
}

async function purchase(event) {
  // Покупка только настоящим щелчком мыши: detail равен нулю у нажатий с клавиатуры.
  if (!offer || busy || !event.isTrusted || event.detail === 0) {
    return;
  }

  busy = true;
  render();
  const answer = await ask({ type: "unlock", key: offer.key });
  busy = false;
  if (answer.unlocked) {
    open();
    return;
  }

  if (answer.denied) {
    offer = { ...offer, price: answer.price, balance: answer.balance };
  } else if (answer.offline) {
    message = text("site.offline");
  } else {
    message = text("site.error");
  }

  render();
}

function layout() {
  const body = assets.body;
  const lines = [];
  const add = (value, color, font = body) => {
    for (const line of wrap(font, value, Width - Padding * 2)) {
      lines.push({ line, color, font });
    }
  };

  if (offer) {
    add(offer.name, assets.colors.gold, assets.title);
    add(text("site.text"), assets.colors.text);
  } else {
    add(message || text("site.loading"), assets.colors.mist);
  }

  return lines;
}

function render() {
  if (!assets) {
    return;
  }

  const { colors, body, title } = assets;
  const lines = layout();
  const lineHeight = (font) => font.lineHeight + 1;
  const header = 20;
  const textHeight = lines.reduce((sum, item) => sum + lineHeight(item.font), 0);
  const offerHeight = offer ? 4 + 2 * lineHeight(body) + 4 + ButtonHeight + (shortBy() > 0 ? lineHeight(body) * 2 + 2 : 0) : 0;
  const height = header + Padding + textHeight + offerHeight + Padding;

  // Целочисленный масштаб в пикселях экрана: без сглаживания на любом масштабе Windows.
  const ratio = window.devicePixelRatio || 1;
  const screenWidth = Math.floor(window.innerWidth * ratio);
  const screenHeight = Math.floor(window.innerHeight * ratio);
  const scale = Math.max(1, Math.min(Math.floor(screenWidth / (Width + 16)), Math.floor(screenHeight / (height + 16)), 4));
  canvas.width = screenWidth;
  canvas.height = screenHeight;
  canvas.style.width = screenWidth / ratio + "px";
  canvas.style.height = screenHeight / ratio + "px";

  context.setTransform(1, 0, 0, 1, 0, 0);
  context.imageSmoothingEnabled = false;
  context.fillStyle = colors.ink;
  context.fillRect(0, 0, screenWidth, screenHeight);

  const left = Math.floor((screenWidth / scale - Width) / 2);
  const top = Math.floor((screenHeight / scale - height) / 2);
  context.setTransform(scale, 0, 0, scale, 0, 0);

  drawPanel(context, colors, left, top, Width, height);
  drawPanel(context, colors, left, top, Width, header, colors.panelHi);
  drawSprite(context, assets.atlas, "lock", left + 4, top + 2);
  drawText(context, title, text("site.title"), left + 24, top + 4, colors.text);

  let y = top + header + Padding;
  for (const item of lines) {
    drawText(context, item.font, item.line, left + Padding, y, item.color);
    y += lineHeight(item.font);
  }

  button = null;
  if (offer) {
    y += 4;
    drawSprite(context, assets.atlas, "coin", left + Padding, y - 3, frame);
    drawText(context, body, format(assets.texts["lock.price"], coins(offer.price)), left + Padding + 18, y, colors.gold);
    y += lineHeight(body);
    drawText(context, body, format(assets.texts["lock.balance"], coins(offer.balance)), left + Padding + 18, y, colors.mist);
    y += lineHeight(body) + 4;

    const short = shortBy();
    if (short > 0) {
      for (const line of wrap(body, text("site.short", coins(short)), Width - Padding * 2)) {
        drawText(context, body, line, left + Padding, y, colors.red);
        y += lineHeight(body);
      }

      y += 2;
    }

    const label = text("lock.buy");
    const width = measure(body, label) + 16;
    const x = left + Width - Padding - width;
    const enabled = short <= 0 && !busy;
    drawPanel(context, colors, x, y, width, ButtonHeight, enabled ? colors.greenDark : colors.night);
    drawText(context, body, label, x + 8, y + 3, enabled ? colors.white : colors.line);
    button = { x, y, width, height: ButtonHeight, enabled };
  }

  placeButton(scale, ratio);
  status.textContent = offer
    ? `${offer.name}. ${text("lock.price", offer.price)}. ${text("lock.balance", offer.balance)}.`
    : message || text("site.loading");
}

function shortBy() {
  return offer ? offer.price - offer.balance : 0;
}

function placeButton(scale, ratio) {
  if (!button) {
    buy.disabled = true;
    buy.style.display = "none";
    return;
  }

  buy.style.display = "block";
  buy.disabled = !button.enabled;
  buy.style.left = (button.x * scale) / ratio + "px";
  buy.style.top = (button.y * scale) / ratio + "px";
  buy.style.width = (button.width * scale) / ratio + "px";
  buy.style.height = (button.height * scale) / ratio + "px";
  buy.setAttribute("aria-label", text("lock.buy"));
}

async function start() {
  const [body, title, atlas, colors, texts] = await Promise.all([
    loadFont("pixel/body.json"),
    loadFont("pixel/title.json"),
    loadAtlas(),
    loadPalette(),
    loadJson("pixel/ru.json"),
  ]);
  assets = { body, title, atlas, colors, texts };
  document.title = texts["site.pageTitle"] ?? document.title;
  render();
  await refresh();
  setInterval(() => {
    if (offer && !busy) {
      refresh();
    }
  }, RefreshInterval);
  setInterval(() => {
    frame++;
    if (offer) {
      render();
    }
  }, 125);
}

buy.addEventListener("click", purchase);
buy.addEventListener("keydown", (event) => event.preventDefault());
window.addEventListener("resize", render);
start();
