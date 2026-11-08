// Часть отслеживания плееров, которая работает в мире страницы. Некоторые сайты, например SoundCloud,
// играют звук через элемент, которого нет в документе, или прячут плеер в теневом DOM. Такие элементы видны
// только здесь, в момент вызова play(). Их состояние и название трека из Media Session передаются
// скрипту расширения событием документа.
(() => {
  if (globalThis.__adhdwindowsMediaHook) {
    return;
  }

  globalThis.__adhdwindowsMediaHook = true;
  const EventName = "adhdwindows:media";
  const ReportInterval = 2000;
  const MaxTracked = 32;
  const tracked = [];
  const ids = new WeakMap();
  let nextId = 1;

  const play = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = function (...args) {
    try {
      remember(this);
    } catch {
      // Отслеживание не должно мешать странице.
    }

    return play.apply(this, args);
  };

  function remember(element) {
    if (tracked.some((ref) => ref.deref() === element)) {
      return;
    }

    if (tracked.length >= MaxTracked) {
      tracked.shift();
    }

    tracked.push(new WeakRef(element));
  }

  function hidden(element) {
    return !element.isConnected || element.getRootNode() !== document;
  }

  function idOf(element) {
    if (!ids.has(element)) {
      ids.set(element, `h${nextId++}`);
    }

    return ids.get(element);
  }

  function title() {
    const metadata = navigator.mediaSession?.metadata;
    if (!metadata) {
      return null;
    }

    const text = [metadata.artist, metadata.title].filter((part) => typeof part === "string" && part.length > 0).join(" — ");
    return text.length > 0 ? text : null;
  }

  setInterval(() => {
    const media = [];
    for (let index = tracked.length - 1; index >= 0; index--) {
      const element = tracked[index].deref();
      if (!element) {
        tracked.splice(index, 1);
        continue;
      }

      if (hidden(element)) {
        media.push({
          id: idOf(element),
          video: element instanceof HTMLVideoElement,
          position: element.currentTime,
          duration: Number.isFinite(element.duration) ? element.duration : null,
          playing: !element.paused && !element.ended && !element.seeking && element.readyState >= 2,
          rate: element.playbackRate,
          audible: !element.muted && element.volume > 0,
        });
      }
    }

    document.dispatchEvent(new CustomEvent(EventName, { detail: JSON.stringify({ media, title: title() }) }));
  }, ReportInterval);
})();
