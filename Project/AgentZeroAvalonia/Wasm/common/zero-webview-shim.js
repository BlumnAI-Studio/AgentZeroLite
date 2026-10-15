// zero-webview-shim.js — the Avalonia host's stand-in for WebView2's chrome.webview.
//
// WebDev plugins (and the shared zero-bridge.js they load) talk to the host through
// window.chrome.webview: postMessage(obj) out, addEventListener('message', e => e.data)
// in. That object is WebView2's, so on its own it exists only on Windows, and even
// there Avalonia's NativeWebView never posts *to* the page through it — the host
// reaches the page with InvokeScript. The plugin server injects this file as the
// first script of every plugin page so the contract holds unchanged on both engines:
//
//   page → host : chrome.webview.postMessage(obj | string)
//                 → one JSON string through the engine's channel: WebView2's own
//                   postMessage when present (captured before it is replaced),
//                   else Avalonia's invokeCSharpAction, else a WKWebView handler.
//                   Messages queue until a channel exists.
//   host → page : window.__zeroWebview.deliver([msg, …]) (InvokeScript)
//                 → every 'message' listener gets { data: msg }, exactly the shape
//                   WebView2's PostWebMessageAsJson produces.
//
// Nothing here knows the bridge's ops — zero-bridge.js stays byte-identical to the
// WPF host's copy.
(function () {
  if (window.__zeroWebview) return;

  var native = window.chrome && window.chrome.webview;
  var nativePost = native && typeof native.postMessage === 'function' ? native.postMessage.bind(native) : null;
  var nativeAdd = native && typeof native.addEventListener === 'function' ? native.addEventListener.bind(native) : null;
  var nativeRemove = native && typeof native.removeEventListener === 'function' ? native.removeEventListener.bind(native) : null;

  var listeners = [];
  var outbox = [];
  var retry = null;

  // Avalonia defines invokeCSharpAction as a call to window.chrome.webview.postMessage
  // on Windows, looked up when it runs — so with WebView2 present the captured native
  // function is used directly, and replacing chrome.webview cannot loop back here.
  function channel() {
    if (nativePost) return nativePost;
    if (typeof window.invokeCSharpAction === 'function') return function (s) { window.invokeCSharpAction(s); };
    var hs = window.webkit && window.webkit.messageHandlers;
    if (hs) {
      var h = hs.invokeCSharpAction || hs.avaloniaWebView || hs.webview;
      if (!h) { var keys = Object.keys(hs); if (keys.length) h = hs[keys[0]]; }
      if (h && typeof h.postMessage === 'function') return function (s) { h.postMessage(s); };
    }
    return null;
  }

  function flush() {
    var send = channel();
    if (!send) return false;
    while (outbox.length) {
      var s = outbox[0];
      try { send(s); } catch (e) { return false; }
      outbox.shift();
    }
    return true;
  }

  function post(message) {
    outbox.push(typeof message === 'string' ? message : JSON.stringify(message));
    if (flush() || retry) return;
    retry = setInterval(function () {
      if (flush()) { clearInterval(retry); retry = null; }
    }, 50);
  }

  function addEventListener(type, handler, options) {
    if (type === 'message') {
      if (typeof handler === 'function' && listeners.indexOf(handler) < 0) listeners.push(handler);
      return;
    }
    if (nativeAdd) nativeAdd(type, handler, options);
  }

  function removeEventListener(type, handler, options) {
    if (type === 'message') {
      var i = listeners.indexOf(handler);
      if (i >= 0) listeners.splice(i, 1);
      return;
    }
    if (nativeRemove) nativeRemove(type, handler, options);
  }

  function dispatch(message) {
    var data = message;
    if (typeof data === 'string') { try { data = JSON.parse(data); } catch (e) { /* a plain string stays one */ } }
    var event = { type: 'message', data: data };
    var snapshot = listeners.slice();
    for (var i = 0; i < snapshot.length; i++) {
      try { snapshot[i](event); } catch (err) { console.error('[zero] message listener failed', err); }
    }
  }

  var shim = {
    postMessage: post,
    addEventListener: addEventListener,
    removeEventListener: removeEventListener,
  };

  window.__zeroWebview = {
    version: 1,
    deliver: function (batch) {
      if (!Array.isArray(batch)) batch = [batch];
      for (var i = 0; i < batch.length; i++) dispatch(batch[i]);
    },
  };

  // Install. A configurable property is replaced outright; if the engine's object
  // cannot be replaced, its methods are overridden in place instead.
  if (!window.chrome) window.chrome = {};
  var installed = false;
  try {
    Object.defineProperty(window.chrome, 'webview', { value: shim, configurable: true, writable: true, enumerable: true });
    installed = window.chrome.webview === shim;
  } catch (e) { installed = false; }
  if (!installed && native) {
    try {
      native.postMessage = post;
      native.addEventListener = addEventListener;
      native.removeEventListener = removeEventListener;
    } catch (e) {
      console.warn('[zero] could not install the webview shim', e);
    }
  }
})();
