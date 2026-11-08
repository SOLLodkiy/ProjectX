// Тесты правил расширения: node --test browser-extension/test
import assert from "node:assert/strict";
import test from "node:test";
import fs from "node:fs";
import { blockedTarget, buildDnrRules, hostOf, isAllowed, isPageAllowed, pageOf, passFilters, PassPriority } from "../src/rules.js";

const BlockedPage = "chrome-extension://id/blocked.html";

test("имя сайта из адреса совпадает с программой", () => {
  assert.equal(hostOf("https://WWW.YouTube.com./watch?v=1"), "youtube.com");
  assert.equal(hostOf("http://music.yandex.ru:8080/"), "music.yandex.ru");
  assert.equal(hostOf("https://www.com/"), "www.com");
  assert.equal(hostOf("https://кто.рф/"), "xn--j1ail.xn--p1ai");
  assert.equal(hostOf("chrome://extensions"), null);
  assert.equal(hostOf("file:///C:/a.html"), null);
  assert.equal(hostOf("не адрес"), null);
});

test("побеждает самое длинное правило, а без правил сайт закрыт", () => {
  const rules = [
    { domain: "yandex.ru", allow: true },
    { domain: "music.yandex.ru", allow: false },
    { domain: "claude.ai", allow: true },
  ];
  assert.equal(isAllowed(rules, "maps.yandex.ru"), true);
  assert.equal(isAllowed(rules, "music.yandex.ru"), false);
  assert.equal(isAllowed(rules, "a.music.yandex.ru"), false);
  assert.equal(isAllowed(rules, "claude.ai"), true);
  assert.equal(isAllowed(rules, "notclaude.ai"), false);
  assert.equal(isAllowed(rules, "youtube.com"), false);
});

test("адрес закрытой страницы читается после #", () => {
  assert.equal(blockedTarget(`${BlockedPage}#https://a.com/x#y`, BlockedPage), "https://a.com/x#y");
  assert.equal(blockedTarget("https://a.com/", BlockedPage), null);
});

test("правила браузера закрывают только верхний уровень и учитывают глубину", () => {
  const dnr = buildDnrRules([{ domain: "yandex.ru", allow: true }, { domain: "music.yandex.ru", allow: false }], BlockedPage);

  assert.equal(dnr.length, 3);
  assert.ok(dnr.every((rule) => rule.condition.resourceTypes.length === 1 && rule.condition.resourceTypes[0] === "main_frame"));
  assert.deepEqual(new Set(dnr.map((rule) => rule.id)).size, 3);
  assert.equal(dnr[0].priority, 1);
  assert.equal(dnr[0].action.redirect.regexSubstitution, `${BlockedPage}#\\0`);
  assert.equal(dnr[1].action.type, "allow");
  assert.equal(dnr[2].action.type, "redirect");
  assert.ok(dnr[2].priority > dnr[1].priority);
  assert.ok(dnr[2].condition.regexFilter);
});

test("код страницы совпадает с PageAddress.Normalize на общих примерах", () => {
  const { cases } = JSON.parse(fs.readFileSync(new URL("./page-cases.json", import.meta.url), "utf8"));
  assert.ok(cases.length > 10);
  for (const item of cases) {
    assert.equal(pageOf(item.url), item.page, item.url);
  }
});

test("неверные %XX в пути остаются как есть", () => {
  assert.equal(pageOf("https://a.com/x%zz%D0%9E%FF"), "a.com/x%zzО%FF");
});

test("пропуск открывает только свою страницу", () => {
  const rules = [{ domain: "claude.ai", allow: true }];
  const passes = ["ru.wikipedia.org/wiki/Огурец"];
  assert.equal(isPageAllowed(rules, passes, "https://ru.wikipedia.org/wiki/%D0%9E%D0%B3%D1%83%D1%80%D0%B5%D1%86?x#y"), true);
  assert.equal(isPageAllowed(rules, passes, "https://www.ru.wikipedia.org/wiki/Огурец/"), true);
  assert.equal(isPageAllowed(rules, passes, "https://ru.wikipedia.org/wiki/Помидор"), false);
  assert.equal(isPageAllowed(rules, passes, "https://ru.wikipedia.org/wiki/Огурец/История"), false);
  assert.equal(isPageAllowed(rules, passes, "https://en.wikipedia.org/wiki/Огурец"), false);
  assert.equal(isPageAllowed(rules, [], "https://ru.wikipedia.org/wiki/Огурец"), false);
  assert.equal(isPageAllowed(rules, passes, "https://claude.ai/chat"), true);
  assert.equal(isPageAllowed(rules, passes, "chrome://extensions"), true);
});

// Сопоставление urlFilter по документации declarativeNetRequest: || — начало имени сайта или поддомена,
// | — начало или конец адреса, * — любые знаки, ^ — разделитель (не буква, не цифра и не _ - . %) или конец.
function urlFilterMatches(filter, url) {
  let pattern = filter;
  let prefix = "";
  if (pattern.startsWith("||")) {
    prefix = "^[a-z][a-z0-9+.-]*://(?:[^/?#:]*\\.)?";
    pattern = pattern.slice(2);
  } else if (pattern.startsWith("|")) {
    prefix = "^";
    pattern = pattern.slice(1);
  }

  let suffix = "";
  if (pattern.endsWith("|")) {
    suffix = "$";
    pattern = pattern.slice(0, -1);
  }

  const body = [...pattern]
    .map((char) => (char === "*" ? ".*" : char === "^" ? "(?:[^A-Za-z0-9_\\-.%]|$)" : char.replace(/[\\^$.|?*+()[\]{}]/g, "\\$&")))
    .join("");
  return new RegExp(prefix + body + suffix, "i").test(url);
}

const allowedBy = (page, url) => passFilters(page).some((filter) => urlFilterMatches(filter, new URL(url).href));

test("пропуск совпадает с закодированным адресом своей страницы и не пускает дальше", () => {
  const page = "ru.wikipedia.org/wiki/Огурец_обыкновенный";
  for (const url of [
    "https://ru.wikipedia.org/wiki/Огурец_обыкновенный",
    "https://www.ru.wikipedia.org/wiki/Огурец_обыкновенный/",
    "http://ru.wikipedia.org:8080/wiki/Огурец_обыкновенный?oldid=1",
    "https://ru.wikipedia.org/wiki/Огурец_обыкновенный#История",
    "https://ru.wikipedia.org/wiki/%d0%9e%d0%b3%d1%83%d1%80%d0%b5%d1%86_%d0%be%d0%b1%d1%8b%d0%ba%d0%bd%d0%be%d0%b2%d0%b5%d0%bd%d0%bd%d1%8b%d0%b9",
  ]) {
    assert.ok(allowedBy(page, url), url);
  }

  for (const url of [
    "https://ru.wikipedia.org/wiki/Огурец",
    "https://ru.wikipedia.org/wiki/Огурец_обыкновенный_2",
    "https://ru.wikipedia.org/wiki/Огурец_обыкновенный/Сорта",
    "https://ru.wikipedia.org/wiki/",
    "https://ru.wikipedia.org.evil.com/wiki/Огурец_обыкновенный",
    "https://xru.wikipedia.org/wiki/Огурец_обыкновенный",
    "https://evil.com/?https://ru.wikipedia.org/wiki/Огурец_обыкновенный",
  ]) {
    assert.ok(!allowedBy(page, url), url);
  }
});

test("пропуск на корень сайта и на путь со знаками", () => {
  assert.ok(allowedBy("soundcloud.com", "https://soundcloud.com/"));
  assert.ok(allowedBy("soundcloud.com", "https://soundcloud.com/?ref=x"));
  assert.ok(!allowedBy("soundcloud.com", "https://soundcloud.com/artist/track"));

  const track = "soundcloud.com/artist/Track-Name";
  assert.ok(allowedBy(track, "https://soundcloud.com/artist/Track-Name?in=artist/sets/x"));
  assert.ok(!allowedBy(track, "https://soundcloud.com/artist/Track-Name/comments"));
  assert.ok(!allowedBy(track, "https://soundcloud.com/artist/Track-Name-2"));

  const signs = "a.com/a b(1)+x?.y";
  assert.ok(allowedBy(signs, "https://a.com/a%20b(1)+x%3F.y"));
  assert.ok(!allowedBy(signs, "https://a.com/a%20b(1)+x?.y"), "? из пути — только в %XX");
  assert.ok(!allowedBy(signs, "https://a.com/a%20b(1)+xx.y"));
  assert.ok(passFilters(signs).every((filter) => /^[\x21-\x7e]+$/.test(filter)), "urlFilter только из ASCII");

  for (const item of pageCases()) {
    assert.ok(allowedBy(item.page, item.url), item.url);
  }
});

function pageCases() {
  const { cases } = JSON.parse(fs.readFileSync(new URL("./page-cases.json", import.meta.url), "utf8"));
  // Путь с .. браузер сокращает до запроса. Точку в конце имени и несколько косых черт в конце пути
  // пропуск не допускает: такой адрес остаётся закрытым, нужная страница открывается по обычному адресу.
  return cases.filter((item) => item.page !== null && !item.url.includes("..")
    && !/\.(:|\/)/.test(new URL(item.url).host + "/") && !new URL(item.url).pathname.endsWith("//"));
}

test("пропуски дают разрешающие правила выше любых правил сайтов", () => {
  const rules = [{ domain: "wikipedia.org", allow: false }, { domain: "a.b.c.d.e.f.wikipedia.org", allow: false }];
  const dnr = buildDnrRules(rules, BlockedPage, ["ru.wikipedia.org/wiki/Огурец", "ru.wikipedia.org/wiki/Огурец", "", null]);
  const passRules = dnr.slice(3);
  assert.equal(passRules.length, passFilters("ru.wikipedia.org/wiki/Огурец").length);
  assert.equal(new Set(dnr.map((rule) => rule.id)).size, dnr.length);
  assert.ok(passRules.every((rule) => rule.action.type === "allow" && rule.priority === PassPriority));
  assert.ok(dnr.slice(0, 3).every((rule) => rule.priority < PassPriority));
  assert.ok(passRules.every((rule) => rule.condition.resourceTypes.length === 1 && rule.condition.resourceTypes[0] === "main_frame"));
  assert.ok(passRules.every((rule) => typeof rule.condition.urlFilter === "string" && !rule.condition.regexFilter));
  assert.equal(buildDnrRules(rules, BlockedPage).length, 3);
});
