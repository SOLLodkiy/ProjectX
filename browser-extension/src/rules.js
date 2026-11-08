// Правила доступа к сайтам. Повторяют SiteClassifier.IsAllowed и Domain из модуля блокировки:
// побеждает самое длинное подходящее правило, а сайт без правил закрыт.

/** Имя сайта из адреса или null для адресов, которые не блокируются (не http и не https). */
export function hostOf(url) {
  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }

  if (parsed.protocol !== "http:" && parsed.protocol !== "https:" || !parsed.hostname) {
    return null;
  }

  let host = parsed.hostname.toLowerCase().replace(/\.$/, "");
  if (host.startsWith("www.") && host.split(".").length >= 3) {
    host = host.slice(4);
  }

  return host;
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

/**
 * Правила declarativeNetRequest. Закрывается только верхний уровень страницы (main_frame),
 * поэтому картинки, скрипты и встроенные фреймы на открытых сайтах продолжают работать.
 */
export function buildDnrRules(rules, blockedPage) {
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

  return result;
}
