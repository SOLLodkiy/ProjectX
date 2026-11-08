// Действия на странице задания: выделение текста и прокрутка. Работает только в верхнем фрейме.
// Фоновая часть говорит, следить ли за этой страницей. На остальных страницах выделение и прокрутка не читаются вообще:
// обработчики ставятся только на страницах текущих заданий и снимаются, как только задание сменилось или адрес ушёл.
(() => {
  if (globalThis.__adhdwindowsPage || window.top !== window) {
    return;
  }

  globalThis.__adhdwindowsPage = true;
  const api = globalThis.browser ?? globalThis.chrome;
  const SelectDelay = 400;
  const ScrollInterval = 1000;
  const ScrollStep = 0.05;
  const ScrollEnd = 0.98;
  const MaxText = 200;

  let config = null;
  let alive = true;
  let selectTimer = null;
  let lastText = "";
  let scrollTimer = null;
  let scrollAt = 0;
  let lastShare = 0;

  function send(message) {
    if (!alive) {
      return;
    }

    try {
      const sent = api.runtime.sendMessage({ ...message, url: location.href });
      sent?.catch?.(() => {});
    } catch {
      // Расширение обновили или выключили: этот скрипт больше не нужен.
      alive = false;
      apply(null);
    }
  }

  function onSelection() {
    clearTimeout(selectTimer);
    selectTimer = setTimeout(() => {
      const text = String(getSelection()?.toString() ?? "").replace(/\s+/g, " ").trim().slice(0, MaxText);
      if (text.length > 0 && text !== lastText) {
        lastText = text;
        send({ type: "page", event: "select", text });
      } else if (text.length === 0) {
        lastText = "";
      }
    }, SelectDelay);
  }

  function share() {
    const root = document.scrollingElement ?? document.documentElement;
    const height = Math.max(root.scrollHeight, document.body?.scrollHeight ?? 0);
    if (height <= 0) {
      return 0;
    }

    return Math.min(1, Math.max(0, (root.scrollTop + window.innerHeight) / height));
  }

  /** Не чаще раза в секунду и только при росте на 5% или по достижении конца. */
  function checkScroll() {
    const value = share();
    if (value >= lastShare + ScrollStep || (value >= ScrollEnd && lastShare < ScrollEnd)) {
      lastShare = value;
      send({ type: "page", event: "scroll", share: Math.round(value * 1000) / 1000 });
    }
  }

  function onScroll() {
    if (scrollTimer !== null) {
      return;
    }

    const wait = Math.max(0, scrollAt + ScrollInterval - Date.now());
    scrollTimer = setTimeout(() => {
      scrollTimer = null;
      scrollAt = Date.now();
      checkScroll();
    }, wait);
  }

  function apply(next) {
    const select = Boolean(next?.select);
    const scroll = Boolean(next?.scroll);
    if (select !== Boolean(config?.select)) {
      if (select) {
        lastText = "";
        document.addEventListener("selectionchange", onSelection);
      } else {
        document.removeEventListener("selectionchange", onSelection);
        clearTimeout(selectTimer);
      }
    }

    if (scroll !== Boolean(config?.scroll)) {
      if (scroll) {
        lastShare = 0;
        addEventListener("scroll", onScroll, { passive: true, capture: true });
        // Страница могла уже быть прокручена, например после перезагрузки.
        if ((document.scrollingElement?.scrollTop ?? 0) > 0) {
          onScroll();
        }
      } else {
        removeEventListener("scroll", onScroll, { capture: true });
        clearTimeout(scrollTimer);
        scrollTimer = null;
      }
    }

    config = select || scroll ? { select, scroll } : null;
  }

  api.runtime.onMessage.addListener((message) => {
    if (message?.type === "page-watch") {
      // Новое задание или новый адрес: прогресс прокрутки и выделение считаются заново.
      apply(null);
      apply(message.watch);
    }

    return false;
  });

  try {
    const asked = api.runtime.sendMessage({ type: "page-watch" });
    asked?.then?.((reply) => apply(reply?.watch), () => {});
  } catch {
    alive = false;
  }
})();
