// Тесты правил расширения: node --test browser-extension/test
import assert from "node:assert/strict";
import test from "node:test";
import { blockedTarget, buildDnrRules, hostOf, isAllowed } from "../src/rules.js";

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
