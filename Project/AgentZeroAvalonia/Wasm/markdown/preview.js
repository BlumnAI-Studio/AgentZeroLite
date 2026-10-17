// The code view's Markdown preview — in its own file because the page's CSP allows
// scripts from this origin only (an inline block is refused, which is how it was first shipped).
(function () {
  // Same transport probe as the terminal page: NativeWebView's host function, else WebView2's.
  function poster() {
    if (typeof window.invokeCSharpAction === 'function') return function (s) { window.invokeCSharpAction(s); };
    if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage)
      return function (s) { window.chrome.webview.postMessage(s); };
    return function () {};
  }
  var post = poster();
  function send(o) { try { post(JSON.stringify(o)); } catch (e) {} }

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
