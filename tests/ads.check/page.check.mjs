// Runs src/neat/ads/page.js in a fake page and checks what it does.
// usage: node tests/ads.check/page.check.mjs [path to a real injected script to syntax-check]
import fs from 'node:fs';
import vm from 'node:vm';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const template = fs.readFileSync(path.join(here, '..', '..', 'src', 'neat', 'ads', 'page.js'), 'utf8');

// Same filling-in as PageJs.Build in C#.
function build({ host, token, hide, generic, scriptlets, proc = [] }) {
  return template
    .replaceAll('__NEAT_HOST__', JSON.stringify(host))
    .replaceAll('__NEAT_TOKEN__', JSON.stringify(token))
    .replaceAll('__NEAT_HIDE__', JSON.stringify(hide))
    .replaceAll('__NEAT_GENERIC__', generic ? 'true' : 'false')
    .replaceAll('__NEAT_PROC__', JSON.stringify(proc))
    .replaceAll('/*__NEAT_SCRIPTLETS__*/', () => scriptlets);
}

function fakePage({ hostname, top = true, badSelector = null }) {
  const posted = [];
  const listeners = [];
  const timers = [];
  const rules = [];
  class FakeSheet {
    constructor() { this.cssRules = rules; }
    insertRule(text, i) {
      if (badSelector && text.includes(badSelector)) throw new Error('SyntaxError');
      rules.splice(i, 0, text);
      return i;
    }
  }
  const els = [];
  const mk = (id, classes) => ({ nodeType: 1, id, classList: classes, });
  const root = {
    nodeType: 1, id: '', classList: [],
    querySelectorAll: () => els,
  };
  let observed = null;
  const win = {
    chrome: {
      webview: {
        postMessage: m => posted.push(m),
        addEventListener: (t, f) => listeners.push(f),
      },
    },
  };
  win.window = win;
  win.top = top ? win : {};
  const ctx = vm.createContext({
    ...win,
    window: undefined,
    location: { hostname },
    document: {
      adoptedStyleSheets: [],
      documentElement: root,
      readyState: 'complete',
      addEventListener: () => {},
    },
    CSSStyleSheet: FakeSheet,
    MutationObserver: class { constructor(cb) { observed = cb; } observe() {} },
    setTimeout: (f) => { timers.push(f); return timers.length; },
    JSON, Set, Array,
  });
  ctx.window = ctx;
  ctx.top = top ? ctx : {};
  return { ctx, posted, listeners, timers, rules, els, mk, root, mutate: (l) => observed(l) };
}

let n = 0;
const ok = (name, f) => { f(); n++; console.log('ok   ' + name); };

ok('hides selectors and runs the scriptlets on the right host', () => {
  const p = fakePage({ hostname: 'site.test' });
  const code = build({ host: 'site.test', token: 't0k', hide: ['.ad', '#banner'], generic: false, scriptlets: 'globalThis.scriptletRan = 1;' });
  vm.runInContext(code, p.ctx);
  assert.equal(p.ctx.scriptletRan, 1);
  assert.deepEqual(p.rules, ['.ad,#banner{display:none!important}']);
  assert.equal(p.listeners.length, 0, 'no generic work when generic is false');
});

ok('does nothing on another host', () => {
  const p = fakePage({ hostname: 'other.test' });
  vm.runInContext(build({ host: 'site.test', token: 't', hide: ['.ad'], generic: true, scriptlets: 'globalThis.scriptletRan = 1;' }), p.ctx);
  assert.equal(p.ctx.scriptletRan, undefined);
  assert.equal(p.rules.length, 0);
  assert.equal(p.listeners.length, 0);
});

ok('one invalid selector does not stop the others', () => {
  const p = fakePage({ hostname: 'site.test', badSelector: ':bad' });
  vm.runInContext(build({ host: 'site.test', token: 't', hide: ['.a', 'x:bad', '.b'], generic: false, scriptlets: '' }), p.ctx);
  assert.deepEqual(p.rules, ['.a{display:none!important}', '.b{display:none!important}']);
});

ok('a scriptlet that throws does not stop the hiding', () => {
  const p = fakePage({ hostname: 'site.test' });
  vm.runInContext(build({ host: 'site.test', token: 't', hide: ['.ad'], generic: false, scriptlets: 'throw new Error("x");' }), p.ctx);
  // The scriptlet block is its own statement, so the throw ends it, not the page script.
  assert.deepEqual(p.rules, ['.ad{display:none!important}']);
});

ok('generic: reports classes and ids once, hides what comes back', () => {
  const p = fakePage({ hostname: 'site.test' });
  p.els.push(p.mk('top', ['ad-box', 'x']), p.mk('', ['x']));
  vm.runInContext(build({ host: 'site.test', token: 'sec', hide: [], generic: true, scriptlets: '' }), p.ctx);
  assert.equal(p.timers.length, 1);
  p.timers.shift()();
  assert.equal(p.posted.length, 1);
  assert.ok(p.posted[0].startsWith('neat-ads:sec:'));
  const sent = JSON.parse(p.posted[0].slice('neat-ads:sec:'.length));
  assert.deepEqual(sent, { c: ['ad-box', 'x'], i: ['top'] });

  // A reply with the right prefix is applied, any other message is ignored.
  p.listeners[0]({ data: 'neat-ads:wrong:' + JSON.stringify(['.evil']) });
  p.listeners[0]({ data: 'neat-ads:sec:' + JSON.stringify(['.ad-box', 5, '']) });
  assert.deepEqual(p.rules, ['.ad-box{display:none!important}']);

  // New nodes are scanned, names already sent are not sent again.
  p.mutate([{ type: 'childList', addedNodes: [p.mk('top', ['ad-box', 'fresh'])] }]);
  p.timers.shift()();
  assert.equal(p.posted.length, 2);
  assert.deepEqual(JSON.parse(p.posted[1].slice('neat-ads:sec:'.length)), { c: ['fresh'], i: [] });
});

ok('generic: not in a subframe', () => {
  const p = fakePage({ hostname: 'site.test', top: false });
  vm.runInContext(build({ host: 'site.test', token: 't', hide: ['.ad'], generic: true, scriptlets: '' }), p.ctx);
  assert.deepEqual(p.rules, ['.ad{display:none!important}']);
  assert.equal(p.listeners.length, 0);
});

ok('a second run in the same document does nothing', () => {
  const p = fakePage({ hostname: 'site.test' });
  const code = build({ host: 'site.test', token: 't', hide: ['.ad'], generic: false, scriptlets: 'globalThis.runs = (globalThis.runs || 0) + 1;' });
  vm.runInContext(code, p.ctx);
  vm.runInContext(code, p.ctx);
  assert.equal(p.ctx.runs, 1, 'the scriptlets ran once');
  assert.deepEqual(p.rules, ['.ad{display:none!important}'], 'the hiding was made once');
});

if (process.argv[2]) {
  // Syntax check of a real injected script (from neat_cosmetic_resources) inside the template.
  const real = fs.readFileSync(process.argv[2], 'utf8');
  new vm.Script(build({ host: 'www.youtube.com', token: 't', hide: [], generic: false, scriptlets: real }));
  n++;
  console.log('ok   real injected script parses inside the template (' + real.length + ' bytes)');
}

console.log(`${n} checks passed`);
