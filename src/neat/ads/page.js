// The script NEAT adds to a page before its own scripts run (ads/pagejs.cs fills in the
// __NEAT_* words below and ads/ads.cs registers the result with WebView2). One copy is made per
// host; it only acts in frames whose host is that host.
//
//   1. runs the scriptlets (uBO's `##+js(...)` rules) in the page's own world;
//   2. hides the elements the cosmetic rules name, with a style sheet that page CSP cannot block;
//   3. in the top frame, tells the app which classes and ids the page uses and hides what the
//      generic rules say about them.

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

  hide(HIDE);

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
