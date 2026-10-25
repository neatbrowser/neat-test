// Runs the procedural rules of src/neat/ads/page.js in a real Chromium page.
// The rule texts are what neat_adblock (adblock-rust with css-validation) writes for the uBO
// rules shown in each case's comment.
// usage: node tests/ads.check/procedural.check.mjs   (needs the `playwright` package and Chromium)
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
let chromium;
try {
  ({ chromium } = require('playwright'));
} catch (e) {
  console.log('skip procedural.check.mjs: the playwright package is not installed');
  process.exit(0);
}

const here = path.dirname(fileURLToPath(import.meta.url));
const template = fs.readFileSync(path.join(here, '..', '..', 'src', 'neat', 'ads', 'page.js'), 'utf8');

const css = arg => ({ type: 'css-selector', arg });
const op = (type, arg) => ({ type, arg });
const rule = (selector, action) => JSON.stringify(action ? { selector, action } : { selector });

function build(procs) {
  return template
    .replaceAll('__NEAT_HOST__', JSON.stringify('site.test'))
    .replaceAll('__NEAT_TOKEN__', JSON.stringify('t'))
    .replaceAll('__NEAT_HIDE__', '[]')
    .replaceAll('__NEAT_GENERIC__', 'false')
    .replaceAll('__NEAT_PROC__', JSON.stringify(procs))
    .replaceAll('/*__NEAT_SCRIPTLETS__*/', () => '');
}

const PAGE = `<!doctype html><html><head><style>.flex{display:flex}</style></head><body>
<div id="t1"><div class="box" id="a1">Sponsored post</div><div class="box" id="a2">Normal post</div></div>
<div class="wrap" id="w1"><div class="mid"><div class="box2" id="b1">Big PROMO here</div></div></div>
<div class="wrap2" id="w2"><div class="u" id="u1">x</div></div>
<div id="gp"><div><span class="x" id="x1">x</span></div></div>
<div class="m flex" id="m1">flex one</div><div class="m" id="m2">block one</div>
<div class="t" id="tl1">short</div><div class="t" id="tl2">this text is long enough to pass</div>
<div class="at" id="at1" data-ad="yes">1</div><div class="at" id="at2" data-ad="no">2</div>
<div class="r" id="r1">gone</div>
<div class="ra" id="ra1" onclick="x()">1</div>
<div class="rc bad keep" id="rc1">1</div>
<div class="st" id="st1">1</div><div class="st" id="st2">2</div>
<div class="p" id="p1">a<span class="c" id="c1">1</span><span class="d" id="d1">2</span></div>
<div class="p" id="p2">zzz<span class="c" id="c2">2</span></div>
<div class="path" id="path1">1</div><div class="path2" id="path2">2</div>
<div class="sib" id="sib1">1</div><div class="after" id="sib2">2</div><div class="after" id="sib3">3</div>
</body></html>`;

const cases = [
  // site.test##.box:has-text(Sponsored)
  ['has-text hides only matching text', [rule([css('.box'), op('has-text', 'Sponsored')])], { hidden: ['a1'], shown: ['a2'] }],
  // site.test##.box2:has-text(/promo/i):upward(2)
  ['regex has-text then upward(2)', [rule([css('.box2'), op('has-text', '/promo/i'), op('upward', '2')])], { hidden: ['w1'], shown: ['b1'] }],
  // site.test##.u:upward(.wrap2)
  ['upward(selector)', [rule([css('.u'), op('upward', '.wrap2')])], { hidden: ['w2'], shown: ['u1'] }],
  // site.test##.x:xpath(../..)
  ['xpath', [rule([css('.x'), op('xpath', '../..')])], { hidden: ['gp'], shown: ['x1'] }],
  // site.test##.m:matches-css(display: flex)
  ['matches-css', [rule([css('.m'), op('matches-css', 'display: flex')])], { hidden: ['m1'], shown: ['m2'] }],
  // site.test##.t:min-text-length(20)
  ['min-text-length', [rule([css('.t'), op('min-text-length', '20')])], { hidden: ['tl2'], shown: ['tl1'] }],
  // site.test##.at:matches-attr(data-ad="yes")
  ['matches-attr name=value', [rule([css('.at'), op('matches-attr', 'data-ad="yes"')])], { hidden: ['at1'], shown: ['at2'] }],
  // site.test##.p:has-text(a) > .c
  ['operator then child combinator', [rule([css('.p'), op('has-text', 'a'), css(' > .c')])], { hidden: ['c1'], shown: ['d1', 'c2', 'p1'] }],
  // site.test##.sib:has-text(1) + .after
  ['operator then sibling combinator', [rule([css('.sib'), op('has-text', '1'), css(' + .after')])], { hidden: ['sib2'], shown: ['sib3', 'sib1'] }],
  // site.test##.path:matches-path(/news)   (the page is /news/today)
  ['matches-path, path matches', [rule([css('.path'), op('matches-path', '/news')])], { hidden: ['path1'], shown: ['path2'] }],
  // site.test##.path:matches-path(/^\/shop/)
  ['matches-path, path does not match', [rule([css('.path'), op('matches-path', '/^\\/shop/')])], { hidden: [], shown: ['path1'] }],
  // site.test##.r:remove()
  ['remove()', [rule([css('.r')], { type: 'remove' })], { gone: ['r1'] }],
  // site.test##.ra:remove-attr(onclick)
  ['remove-attr', [rule([css('.ra')], { type: 'remove-attr', arg: 'onclick' })], { check: async p => assert.equal(await p.$eval('#ra1', e => e.hasAttribute('onclick')), false) }],
  // site.test##.rc:remove-class(bad)
  ['remove-class', [rule([css('.rc')], { type: 'remove-class', arg: 'bad' })], { check: async p => assert.equal(await p.$eval('#rc1', e => e.className), 'rc keep') }],
  // site.test##.st:has-text(1):style(color: red !important)
  ['style()', [rule([css('.st'), op('has-text', '1')], { type: 'style', arg: 'color: red !important; margin-left: 5px' })], {
    check: async p => {
      assert.equal(await p.$eval('#st1', e => getComputedStyle(e).color), 'rgb(255, 0, 0)');
      assert.equal(await p.$eval('#st1', e => getComputedStyle(e).marginLeft), '5px');
      assert.notEqual(await p.$eval('#st2', e => getComputedStyle(e).color), 'rgb(255, 0, 0)');
    },
  }],
  // a rule that is broken or unknown must not stop the others
  ['bad rules are skipped', ['not json', rule([css('.box'), op('no-such-operator', 'x')]), rule([css('.box'), op('has-text', 'Sponsored')])], { hidden: ['a1'], shown: ['a2'] }],
];

const browser = await chromium.launch();
let n = 0;
try {
  for (const [name, procs, want] of cases) {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    await page.route('http://site.test/**', r => r.fulfill({ status: 200, contentType: 'text/html', body: PAGE }));
    await page.addInitScript(build(procs));
    await page.goto('http://site.test/news/today');
    await page.waitForTimeout(150);
    const visible = id => page.$eval('#' + id, e => getComputedStyle(e).display !== 'none').catch(() => false);
    for (const id of want.hidden ?? []) assert.equal(await visible(id), false, `${name}: #${id} should be hidden`);
    for (const id of want.shown ?? []) assert.equal(await visible(id), true, `${name}: #${id} should be shown`);
    for (const id of want.gone ?? []) assert.equal(await page.$('#' + id), null, `${name}: #${id} should be removed`);
    if (want.check) await want.check(page);
    await ctx.close();
    n++;
    console.log('ok   ' + name);
  }

  // The page changes after the rules ran: new elements are handled too.
  {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    await page.route('http://site.test/**', r => r.fulfill({ status: 200, contentType: 'text/html', body: PAGE }));
    await page.addInitScript(build([rule([css('.box'), op('has-text', 'Sponsored')])]));
    await page.goto('http://site.test/');
    await page.evaluate(() => {
      const d = document.createElement('div');
      d.className = 'box';
      d.id = 'late';
      d.textContent = 'Late Sponsored thing';
      document.body.append(d);
    });
    await page.waitForTimeout(400);
    assert.equal(await page.$eval('#late', e => getComputedStyle(e).display), 'none');
    // A text change that makes an element match is noticed as well.
    await page.evaluate(() => { document.getElementById('a2').textContent = 'now Sponsored'; });
    await page.waitForTimeout(400);
    assert.equal(await page.$eval('#a2', e => getComputedStyle(e).display), 'none');
    await ctx.close();
    n++;
    console.log('ok   elements added or changed later are handled');
  }

  // The rules must not keep the page busy in a loop: with a rule that changes the page itself
  // (style), the number of runs stays small.
  {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    await page.route('http://site.test/**', r => r.fulfill({ status: 200, contentType: 'text/html', body: PAGE }));
    await page.addInitScript(build([rule([css('.st')], { type: 'style', arg: 'color: red' })]));
    await page.addInitScript(() => {
      window.__changes = 0;
      new MutationObserver(l => { window.__changes += l.length; }).observe(document, { subtree: true, attributes: true, childList: true });
    });
    await page.goto('http://site.test/');
    await page.waitForTimeout(400);
    await page.evaluate(() => { window.__changes = 0; });      // what loading the page itself changed does not count
    await page.waitForTimeout(1000);
    const changes = await page.evaluate(() => window.__changes);
    assert.ok(changes < 5, `the rules keep changing the page (${changes} changes in a second)`);
    await ctx.close();
    n++;
    console.log(`ok   no endless loop (${changes} changes in a second)`);
  }
} finally {
  await browser.close();
}
console.log(`${n} procedural checks passed`);
