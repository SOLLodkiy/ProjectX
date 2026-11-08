// Отслеживание плееров на странице для монеток за хронометраж. Работает в каждом фрейме.
// Раз в 2 секунды, пока что-то играет, и сразу при паузе, перемотке и смене скорости отправляет фоновой части
// снимок всех видео и звуков фрейма: позицию, длительность, скорость и звук. Время считает программа.
(() => {
  if (globalThis.__adhdwindowsMedia) {
    return;
  }

  globalThis.__adhdwindowsMedia = true;
  const api = globalThis.browser ?? globalThis.chrome;
  const EventName = "adhdwindows:media";
  const ReportInterval = 2000;
  const HookLifetime = 5000;
  const MaxMedia = 16;
  const MaxTitle = 300;
  const Events = ["play", "playing", "pause", "seeking", "seeked", "ratechange", "volumechange", "ended", "emptied", "loadedmetadata"];
  const ids = new WeakMap();
  let nextId = 1;
  let hooked = { media: [], title: null, at: 0 };
  let wasPlaying = false;
  let soon = null;
  let alive = true;

  // Снимок из мира страницы: элементы вне документа и название трека.
  document.addEventListener(EventName, (event) => {
    try {
      const data = JSON.parse(String(event.detail));
      hooked = {
        media: Array.isArray(data.media) ? data.media.slice(0, MaxMedia) : [],
        title: typeof data.title === "string" ? data.title : null,
        at: Date.now(),
      };
    } catch {
      // Неверный снимок пропускается.
    }
  });

  for (const type of Events) {
    // События плеера не всплывают, но проходят через документ при перехвате.
    document.addEventListener(type, schedule, true);
  }

  addEventListener("pagehide", () => {
    wasPlaying = true;
    send([]);
  });

  setInterval(() => report(false), ReportInterval);

  function schedule() {
    clearTimeout(soon);
    soon = setTimeout(() => report(true), 100);
  }

  function idOf(element) {
    if (!ids.has(element)) {
      ids.set(element, `e${nextId++}`);
    }

    return ids.get(element);
  }

  function title() {
    const fresh = Date.now() - hooked.at < HookLifetime;
    const text = (fresh && hooked.title) || document.title || "";
    return text.slice(0, MaxTitle);
  }

  function describe(element) {
    return {
      id: idOf(element),
      video: element instanceof HTMLVideoElement,
      position: element.currentTime,
      duration: Number.isFinite(element.duration) ? element.duration : null,
      playing: !element.paused && !element.ended && !element.seeking && element.readyState >= 2,
      rate: element.playbackRate,
      audible: !element.muted && element.volume > 0,
    };
  }

  function collect() {
    const media = [];
    for (const element of document.querySelectorAll("video, audio")) {
      // Пустой элемент без источника ещё ничего не играл.
      if (!element.currentSrc && !element.srcObject) {
        continue;
      }

      media.push(describe(element));
      if (media.length >= MaxMedia) {
        break;
      }
    }

    if (Date.now() - hooked.at < HookLifetime) {
      media.push(...hooked.media.slice(0, MaxMedia - media.length));
    }

    const name = title();
    return media.map((item) => ({ ...item, title: name }));
  }

  /** Тихая страница не шлёт ничего, а последний снимок после остановки отправляется один раз. */
  function report(changed) {
    const media = collect();
    const playing = media.some((item) => item.playing);
    if (!playing && !wasPlaying && !changed) {
      return;
    }

    if (!playing && media.length === 0 && !wasPlaying) {
      return;
    }

    wasPlaying = playing;
    send(media);
  }

  function send(media) {
    if (!alive) {
      return;
    }

    try {
      const sent = api.runtime.sendMessage({ type: "media", media });
      sent?.catch?.(() => {});
    } catch {
      // Расширение обновили или выключили: этот скрипт больше не нужен.
      alive = false;
    }
  }
})();
