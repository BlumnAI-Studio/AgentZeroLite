// The code view's Markdown preview — in its own file because the page's CSP allows
// scripts from this origin only (an inline block is refused, which is how it was first shipped).
(function () {
  // Same transport probe as the terminal page: NativeWebView's host function, else WebView2's,
  // else WKWebView's message handler. On macOS the host function is injected AFTER this script
  // runs, so the probe is repeated and messages queue until it answers — probing once left
  // `ready` unsent and the preview blank forever (plain files, which need no page, still showed).
  function transport() {
    if (typeof window.invokeCSharpAction === 'function') return function (s) { window.invokeCSharpAction(s); };
    if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage)
      return function (s) { window.chrome.webview.postMessage(s); };
    if (window.webkit && window.webkit.messageHandlers) {
      var hs = window.webkit.messageHandlers;
      var h = hs.invokeCSharpAction || hs.avaloniaWebView || hs.webview;
      if (!h) { var keys = Object.keys(hs); if (keys.length) h = hs[keys[0]]; }
      if (h && h.postMessage) return function (s) { h.postMessage(s); };
    }
    return null;
  }
  var queue = [];
  function send(o) {
    var s = JSON.stringify(o);
    var t = transport();
    if (!t) { queue.push(s); return; }
    try { t(s); } catch (e) { queue.push(s); }
  }
  function drain() {
    var t = transport();
    if (!t) { setTimeout(drain, 50); return; }
    while (queue.length) { try { t(queue.shift()); } catch (e) { break; } }
  }
  setTimeout(drain, 0);

  if (window.mermaid) {
    mermaid.initialize({ startOnLoad: false, theme: 'dark', securityLevel: 'strict', fontFamily: 'Segoe UI, sans-serif' });
  }

  // The host calls this with the document's HTML; mermaid blocks are drawn after it lands.
  window.renderDoc = function (html) {
    var doc = document.getElementById('doc');
    doc.innerHTML = html;
    window.scrollTo(0, 0);
    var blocks = doc.querySelectorAll('pre.mermaid');
    if (!blocks.length || !window.mermaid) { send({ type: 'rendered', mermaid: 0 }); return; }
    mermaid.run({ nodes: blocks, suppressErrors: true }).then(function () {
      var failed = 0;
      blocks.forEach(function (b) {
        if (!b.querySelector('svg')) {
          failed++;
          var note = document.createElement('div');
          note.className = 'mermaid-error';
          note.textContent = 'Mermaid could not draw this diagram — the source is shown.';
          b.parentNode.insertBefore(note, b);
        }
      });
      send({ type: 'rendered', mermaid: blocks.length, failed: failed });
    });
  };

  // Links: the host decides (a browser for http, the code view for a workspace file).
  document.addEventListener('click', function (e) {
    var a = e.target.closest && e.target.closest('a[href]');
    if (!a) return;
    var href = a.getAttribute('href');
    if (!href || href.charAt(0) === '#') return;  // in-page anchors scroll as usual
    e.preventDefault();
    send({ type: 'link', href: href });
  });

  send({ type: 'ready' });
})();
