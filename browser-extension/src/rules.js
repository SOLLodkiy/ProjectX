// Правила доступа к сайтам. Повторяют SiteClassifier.IsAllowed и Domain из модуля блокировки:
// побеждает самое длинное подходящее правило, а сайт без правил закрыт.

function parse(url) {
  let parsed;
  try {
    parsed = new URL(String(url ?? "").trim());
  } catch {
    return null;
  }

  return (parsed.protocol === "http:" || parsed.protocol === "https:") && parsed.hostname ? parsed : null;
}

function siteName(parsed) {
  let host = parsed.hostname.toLowerCase().replace(/\.$/, "");
  if (host.startsWith("www.") && host.split(".").length >= 3) {
    host = host.slice(4);
  }

  return host;
}

/** Имя сайта из адреса или null для адресов, которые не блокируются (не http и не https). */
export function hostOf(url) {
  const parsed = parse(url);
  return parsed ? siteName(parsed) || null : null;
}

/** Раскодирует %XX, а неверные последовательности UTF-8 оставляет как есть, как Uri.UnescapeDataString. */
function unescape(path) {
  try {
    return decodeURIComponent(path);
  } catch {
    return path.replace(/(?:%[0-9a-fA-F]{2})+/g, (run) => {
      const codes = run.match(/%[0-9a-fA-F]{2}/g);
      let text = "";
      for (let index = 0; index < codes.length; ) {
        let length = 4;
        for (; length > 0; length--) {
          try {
            text += decodeURIComponent(codes.slice(index, index + length).join(""));
            break;
          } catch {
            // Короче.
          }
        }

        if (length === 0) {
          text += codes[index];
          length = 1;
        }

        index += length;
      }

      return text;
    });
  }
}

/**
 * Код страницы для заданий и пропусков, как PageAddress.Normalize в программе: имя сайта и раскодированный путь
 * без схемы, порта, запроса, якоря и завершающей косой черты. null — адрес не страница сайта.
 */
export function pageOf(url) {
  const parsed = parse(url);
  if (!parsed) {
    return null;
  }

  const host = siteName(parsed);
  return host ? host + unescape(parsed.pathname).replace(/\/+$/, "") : null;
}

/** Открыта ли вкладка: пропуск открывает только свою страницу, остальное решают правила сайтов. */
export function isPageAllowed(rules, passes, url) {
  const host = hostOf(url);
  if (!host) {
    return true;
  }

  const page = pageOf(url);
  return (page !== null && Array.isArray(passes) && passes.includes(page)) || isAllowed(rules, host);
}

export function isUnder(host, domain) {
  return host === domain || host.endsWith("." + domain);
}

export function depth(domain) {
  return /^[\d.]+$/.test(domain) || domain.startsWith("[") ? 1 : domain.split(".").length;
}

/** Открыт ли сайт по правилам [{domain, allow}]. */
export function isAllowed(rules, host) {
  let best = null;
  for (const rule of rules) {
    if (isUnder(host, rule.domain) && (best === null || depth(rule.domain) > depth(best.domain))) {
      best = rule;
    }
  }

  return best !== null && best.allow;
}

/** Адрес сайта, который закрыт страницей покупки, или null. */
export function blockedTarget(url, blockedPage) {
  if (!url || !url.startsWith(blockedPage)) {
    return null;
  }

  const hash = url.indexOf("#");
  return hash < 0 ? null : url.slice(hash + 1);
}

const PageFilter = "^https?://.*$";

// Пропуск должен быть выше любого правила сайта: у тех приоритет 1 + число частей имени.
export const PassPriority = 1000;

// Знаки пути, которые браузер всегда кодирует (как в стандарте URL), и знаки, которые кодирует программа:
// ? и # из пути страницы иначе начали бы запрос или якорь, а % — уже раскодированный знак.
const Encoded = /[\x00-\x20"#%<>?`{}\x7f]/;

function encodePath(path) {
  let result = "";
  for (const char of path) {
    if (char.codePointAt(0) > 127 || Encoded.test(char)) {
      for (const byte of new TextEncoder().encode(char)) {
        result += "%" + byte.toString(16).toUpperCase().padStart(2, "0");
      }
    } else {
      result += char;
    }
  }

  return result;
}

/**
 * Шаблоны urlFilter для пропуска на страницу с кодом page. Браузер сравнивает закодированный адрес,
 * поэтому путь кодируется так же. Допускаются www. и поддомены (||), порт, завершающая косая черта,
 * запрос и якорь, но не другие пути. Регулярные выражения здесь не годятся: Chrome отклоняет уже
 * ~180 знаков с закодированной кириллицей (memoryLimitExceeded), а у urlFilter такого предела нет.
 * Неточности (поддомен, регистр, порт с другим путём) закрывает фоновая часть после загрузки по коду страницы.
 */
export function passFilters(page) {
  const slash = page.indexOf("/");
  const host = slash < 0 ? page : page.slice(0, slash);
  const path = encodePath(slash < 0 ? "" : page.slice(slash));
  const ends = path.length === 0 ? ["/|", "/?", "/#"] : ["|", "/|", "?", "/?", "#", "/#"];
  return [`||${host}${path}`, `||${host}:*${path}`].flatMap((start) => ends.map((end) => start + end));
}

/**
 * Правила declarativeNetRequest. Закрывается только верхний уровень страницы (main_frame),
 * поэтому картинки, скрипты и встроенные фреймы на открытых сайтах продолжают работать.
 */
export function buildDnrRules(rules, blockedPage, passes = []) {
  const redirect = { type: "redirect", redirect: { regexSubstitution: blockedPage + "#\\0" } };
  const result = [
    {
      id: 1,
      priority: 1,
      action: redirect,
      condition: { regexFilter: PageFilter, resourceTypes: ["main_frame"] },
    },
  ];

  rules.forEach((rule, index) => {
    const condition = { requestDomains: [rule.domain], resourceTypes: ["main_frame"] };
    if (!rule.allow) {
      condition.regexFilter = PageFilter;
    }

    result.push({
      id: index + 2,
      priority: 1 + depth(rule.domain),
      action: rule.allow ? { type: "allow" } : redirect,
      condition,
    });
  });

  // Пропуск открывает только свою страницу; переходы внутри страницы без загрузки ловит фоновая часть.
  let id = rules.length + 2;
  for (const page of new Set(passes)) {
    if (typeof page !== "string" || page.length === 0) {
      continue;
    }

    for (const urlFilter of passFilters(page)) {
      result.push({
        id: id++,
        priority: PassPriority,
        action: { type: "allow" },
        condition: { urlFilter, isUrlFilterCaseSensitive: false, resourceTypes: ["main_frame"] },
      });
    }
  }

  return result;
}
