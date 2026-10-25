// The script NEAT adds to a page before its own scripts run (ads/pagejs.cs fills in the
// __NEAT_* words below and ads/ads.cs registers the result with WebView2). One copy is made per
// host; it only acts in frames whose host is that host.
//
//   1. runs the scriptlets (uBO's `##+js(...)` rules) in the page's own world;
//   2. hides the elements the cosmetic rules name, with a style sheet that page CSP cannot block;
//   3. runs the procedural rules (`:has-text`, `:upward`, `:xpath`, `:matches-css`, ... and the
//      `:remove()` / `:style()` actions) now and again whenever the page changes;
//   4. in the top frame, tells the app which classes and ids the page uses and hides what the
//      generic rules say about them.

// The script can run twice in one document (registered for it, and run again by the app when
// its registration was late). Only the first run does anything.
if ((() => {
  try {
    if (location.hostname !== __NEAT_HOST__) return false;
    const key = Symbol.for('neat-ads-ran');
    if (window[key]) return false;
    Object.defineProperty(window, key, { value: true });
    return true;
  } catch (e) { return true; }
})()) {

try {
  (function () {
    if (location.hostname !== __NEAT_HOST__) return;
    /*__NEAT_SCRIPTLETS__*/
  })();
} catch (e) { /* a broken scriptlet must not stop the hiding below */ }

(() => {
  'use strict';
  if (location.hostname !== __NEAT_HOST__) return;

  const TOKEN = __NEAT_TOKEN__;
  const HIDE = __NEAT_HIDE__;
  const GENERIC = __NEAT_GENERIC__;
  const PROC = __NEAT_PROC__;       // the engine's procedural rules, each one a JSON text
  const w = window.chrome && window.chrome.webview;

  // ---- hiding ----

  let sheet = null;

  function rule(list) {
    return list.join(',') + '{display:none!important}';
  }

  function hide(list) {
    if (!list.length) return;
    if (!sheet) {
      try {
        sheet = new CSSStyleSheet();
        document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
      } catch (e) {
        sheet = null;
        return;
      }
    }
    // Fifty selectors per rule. One invalid selector spoils its whole rule, so a rule that is
    // refused is retried one selector at a time.
    for (let i = 0; i < list.length; i += 50) {
      const chunk = list.slice(i, i + 50);
      try {
        sheet.insertRule(rule(chunk), sheet.cssRules.length);
      } catch (e) {
        for (const one of chunk) {
          try {
            sheet.insertRule(rule([one]), sheet.cssRules.length);
          } catch (e2) { /* an invalid selector is skipped */ }
        }
      }
    }
  }

  // ---- procedural rules ----
  //
  // Each rule is {selector: [operator...], action?}. The operators narrow a list of elements one
  // after the other, the way uBO's do; the action (default: hide) is then applied to what is left.
  // The rules run once the page has a document, and again (a little later, and never in a tight
  // loop) whenever the page changes.

  const MARK = 'data-neat-hidden';
  const TEMP = 'data-neat-from';
  const NOT_THERE = () => false;

  // "text" matches when the text contains it; "/re/flags" is a regular expression.
  function textMatcher(arg) {
    const m = /^\/([\s\S]+)\/([a-z]*)$/.exec(arg);
    if (m) {
      try {
        const re = new RegExp(m[1], m[2].replace(/[gy]/g, ''));
        return s => re.test(s);
      } catch (e) { return NOT_THERE; }
    }
    return s => s.includes(arg);
  }

  // Like textMatcher, but a plain text must be the whole value.
  function wholeMatcher(arg) {
    const m = /^\/([\s\S]+)\/([a-z]*)$/.exec(arg);
    if (m) return textMatcher(arg);
    return s => s === arg;
  }

  function unquote(s) {
    s = s.trim();
    if (s.length > 1 && (s[0] === '"' || s[0] === "'") && s[s.length - 1] === s[0])
      return s.slice(1, -1);
    return s;
  }

  // "name", "name=value", with each part possibly quoted or a /regex/.
  function splitPair(arg) {
    let quote = '';
    for (let i = 0; i < arg.length; i++) {
      const c = arg[i];
      if (quote) { if (c === quote) quote = ''; continue; }
      if (c === '"' || c === "'") { quote = c; continue; }
      if (c === '=') return [unquote(arg.slice(0, i)), unquote(arg.slice(i + 1))];
    }
    return [unquote(arg), null];
  }

  // Splits "a: b; c: d" at the semicolons that are not inside brackets or quotes.
  function declarations(text) {
    const out = [];
    let depth = 0;
    let quote = '';
    let from = 0;
    for (let i = 0; i <= text.length; i++) {
      const c = text[i];
      if (quote) { if (c === quote) quote = ''; continue; }
      if (c === '"' || c === "'") quote = c;
      else if (c === '(') depth++;
      else if (c === ')') depth = Math.max(0, depth - 1);
      else if ((c === ';' && depth === 0) || i === text.length) {
        const d = text.slice(from, i).trim();
        from = i + 1;
        const k = d.indexOf(':');
        if (k > 0) out.push([d.slice(0, k).trim(), d.slice(k + 1).trim()]);
      }
    }
    return out;
  }

  function descendants(els, css) {
    const out = [];
    const arg = css.trim();
    for (const el of els) {
      try {
        if (arg[0] === '+' || arg[0] === '~') {
          // Siblings are not inside the element: mark it and look from its parent.
          const from = el.parentNode;
          if (!from || !from.querySelectorAll) continue;
          el.setAttribute(TEMP, '');
          try {
            for (const r of from.querySelectorAll('[' + TEMP + ']' + arg)) out.push(r);
          } finally { el.removeAttribute(TEMP); }
        } else {
          // ":scope" is the element itself: ":scope > x" its children, ":scope x" what is inside it.
          for (const r of el.querySelectorAll(':scope ' + arg)) out.push(r);
        }
      } catch (e) { /* a selector this browser refuses matches nothing */ }
    }
    return out;
  }

  function evaluate(f) {
    let els = null;                 // null: the whole document, not looked at yet
    const all = () => els === null ? Array.from(document.querySelectorAll('*')) : els;

    for (const op of f.selector) {
      const a = op.arg;
      switch (op.type) {
        case 'css-selector':
          els = els === null ? Array.from(document.querySelectorAll(a)) : descendants(els, a);
          break;
        case 'has-text': {
          const m = textMatcher(a);
          els = all().filter(e => m(e.textContent || ''));
          break;
        }
        case 'min-text-length': {
          const n = Number(a);
          els = all().filter(e => (e.textContent || '').length >= n);
          break;
        }
        case 'matches-attr': {
          const [name, value] = splitPair(a);
          const nm = wholeMatcher(name);
          const vm = value === null ? null : wholeMatcher(value);
          els = all().filter(e => {
            for (const at of e.attributes) {
              if (nm(at.name) && (vm === null || vm(at.value))) return true;
            }
            return false;
          });
          break;
        }
        case 'matches-css':
        case 'matches-css-before':
        case 'matches-css-after': {
          const [prop, value] = splitPair(a.replace(':', '='));
          const vm = wholeMatcher(value === null ? '' : value);
          const pseudo = op.type === 'matches-css-before' ? '::before' : op.type === 'matches-css-after' ? '::after' : null;
          els = all().filter(e => vm(getComputedStyle(e, pseudo).getPropertyValue(prop)));
          break;
        }
        case 'matches-path': {
          const m = textMatcher(a);
          if (!m(location.pathname + location.search)) return [];
          break;
        }
        case 'upward': {
          const out = [];
          const n = /^\d+$/.test(a) ? Number(a) : 0;
          for (const e of all()) {
            let r = null;
            if (n > 0 && n < 256) {
              r = e;
              for (let i = 0; i < n && r; i++) r = r.parentElement;
            } else if (n === 0 && e.parentElement) {
              try { r = e.parentElement.closest(a); } catch (x) { r = null; }
            }
            if (r) out.push(r);
          }
          els = out;
          break;
        }
        case 'xpath': {
          const out = [];
          for (const e of (els === null ? [document] : els)) {
            try {
              const snap = document.evaluate(a, e, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
              for (let i = 0; i < snap.snapshotLength; i++) {
                const nd = snap.snapshotItem(i);
                if (nd && nd.nodeType === 1) out.push(nd);
              }
            } catch (x) { /* a bad expression matches nothing */ }
          }
          els = out;
          break;
        }
        default:
          return [];                // an operator this runtime does not know: do nothing
      }
      if (els !== null && els.length === 0) return els;
    }
    return els === null ? [] : els;
  }

  function act(el, action) {
    if (!action) {
      if (!el.hasAttribute(MARK)) el.setAttribute(MARK, '');
      return;
    }
    switch (action.type) {
      case 'remove':
        el.remove();
        break;
      case 'remove-attr':
        el.removeAttribute(action.arg);
        break;
      case 'remove-class':
        el.classList.remove(action.arg);
        break;
      case 'style':
        for (const [name, value] of declarations(action.arg)) {
          const important = /\s*!important\s*$/i.test(value);
          el.style.setProperty(name, value.replace(/\s*!important\s*$/i, ''), important ? 'important' : '');
        }
        break;
    }
  }

  function procedural(texts) {
    const rules = [];
    for (const t of texts) {
      try {
        const f = JSON.parse(t);
        if (f && Array.isArray(f.selector) && f.selector.length) rules.push(f);
      } catch (e) { /* a rule that is not JSON is skipped */ }
    }
    if (!rules.length) return;

    hide(['[' + MARK + ']']);

    let observer = null;
    let timer = 0;
    let wait = 100;

    function watch() {
      if (!observer) return;
      observer.observe(document, { subtree: true, childList: true, characterData: true, attributes: true });
    }

    function run() {
      timer = 0;
      const t0 = Date.now();
      // The changes made here are not changes of the page.
      if (observer) observer.disconnect();
      try {
        for (const f of rules) {
          try {
            for (const el of evaluate(f)) act(el, f.action);
          } catch (e) { /* one broken rule must not stop the others */ }
        }
      } finally {
        watch();
      }
      // A slow page gets more time between runs.
      wait = Math.max(100, (Date.now() - t0) * 4);
    }

    function later() {
      if (!timer) timer = setTimeout(run, wait);
    }

    observer = new MutationObserver(later);
    watch();
    run();
    if (document.readyState === 'loading')
      document.addEventListener('DOMContentLoaded', later, { once: true });
    window.addEventListener('load', later, { once: true });
  }

  hide(HIDE);
  if (PROC.length) {
    try { procedural(PROC); } catch (e) { /* the rest of the script goes on */ }
  }

  // ---- generic rules: only the top frame, and only when the page did not ask for none ----

  if (!GENERIC || window !== window.top || !w) return;

  const PREFIX = 'neat-ads:' + TOKEN + ':';
  const post = w.postMessage.bind(w);
  const LIMIT = 20000;     // distinct classes (and ids) remembered per page
  const BATCH = 1000;      // most names sent in one message
  const seenC = new Set();
  const seenI = new Set();
  let waitC = [];
  let waitI = [];
  let timer = 0;

  function note(el) {
    if (!el || el.nodeType !== 1) return;
    const id = el.id;
    if (typeof id === 'string' && id && !seenI.has(id) && seenI.size < LIMIT) {
      seenI.add(id);
      waitI.push(id);
    }
    const cl = el.classList;
    if (cl) {
      for (let i = 0; i < cl.length; i++) {
        const c = cl[i];
        if (!seenC.has(c) && seenC.size < LIMIT) {
          seenC.add(c);
          waitC.push(c);
        }
      }
    }
  }

  function scan(root) {
    note(root);
    if (root.querySelectorAll) {
      for (const el of root.querySelectorAll('[id],[class]'))
        note(el);
    }
  }

  function flush() {
    timer = 0;
    if (!waitC.length && !waitI.length) return;
    const c = waitC.slice(0, BATCH);
    const i = waitI.slice(0, BATCH);
    waitC = waitC.slice(BATCH);
    waitI = waitI.slice(BATCH);
    try {
      post(PREFIX + JSON.stringify({ c, i }));
    } catch (e) { return; }
    if (waitC.length || waitI.length)
      later();
  }

  function later() {
    if (!timer)
      timer = setTimeout(flush, 150);
  }

  w.addEventListener('message', ev => {
    const d = ev.data;
    if (typeof d !== 'string' || !d.startsWith(PREFIX)) return;
    try {
      const list = JSON.parse(d.slice(PREFIX.length));
      if (Array.isArray(list))
        hide(list.filter(x => typeof x === 'string' && x));
    } catch (e) { /* not for us */ }
  });

  function start() {
    const root = document.documentElement;
    if (!root) return;
    scan(root);
    later();
    new MutationObserver(list => {
      for (const m of list) {
        if (m.type === 'attributes') {
          note(m.target);
        } else {
          for (const n of m.addedNodes)
            scan(n);
        }
      }
      later();
    }).observe(root, { subtree: true, childList: true, attributes: true, attributeFilter: ['class', 'id'] });
  }

  if (document.readyState === 'loading')
    document.addEventListener('DOMContentLoaded', start, { once: true });
  else
    start();
})();

}
