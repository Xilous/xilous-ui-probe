// The extension APIs content.js and settings.js use, implemented over a message
// channel to the Xilous Probe desktop app.
//
// This file is one function expression. The app inlines it and calls it with a
// transport: { send(string) } for outgoing messages, and it calls the returned
// receive(string) with every message the app sends back. Over CDP the transport
// is a Runtime binding; in the settings window it is WebView2's postMessage.
//
// Nothing here is installed on the page's globals. The returned `chrome` and
// `navigator` are handed to content.js as parameters, so the page's own
// navigator.clipboard and window.chrome are never touched.
(function createXilousProbeShim(transport) {
  "use strict";

  let nextId = 1;
  const pending = new Map();
  const messageListeners = [];
  const storageListeners = [];
  const cache = Object.create(null); // last known values, to fill oldValue in onChanged

  function request(kind, payload) {
    const id = nextId++;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      try {
        transport.send(JSON.stringify({ id, kind, payload }));
      } catch (err) {
        pending.delete(id);
        reject(err);
      }
    });
  }

  function notify(kind, payload) {
    try { transport.send(JSON.stringify({ id: 0, kind, payload })); } catch { /* app gone */ }
  }

  function receive(raw) {
    let msg;
    try { msg = typeof raw === "string" ? JSON.parse(raw) : raw; } catch { return; }
    if (!msg) return;
    if (msg.id) {
      const waiter = pending.get(msg.id);
      if (!waiter) return;
      pending.delete(msg.id);
      if (msg.error) waiter.reject(new Error(msg.error)); else waiter.resolve(msg.result);
      return;
    }
    if (msg.event === "message") {
      for (const fn of messageListeners.slice()) {
        try { fn(msg.message, { id: "xilous-probe" }, () => {}); } catch (err) { console.error(err); }
      }
    } else if (msg.event === "storage") {
      const changes = {};
      for (const [key, newValue] of Object.entries(msg.changes || {})) {
        const change = { oldValue: cache[key] };
        if (newValue === null || newValue === undefined) delete cache[key];
        else { cache[key] = newValue; change.newValue = newValue; }
        changes[key] = change;
      }
      for (const fn of storageListeners.slice()) {
        try { fn(changes, "local"); } catch (err) { console.error(err); }
      }
    }
  }

  // chrome.storage.local.get accepts a key, a list of keys, an object of
  // defaults, or nothing (everything), and answers by callback or promise.
  function normalizeKeys(keys) {
    if (keys === null || keys === undefined) return { list: null, defaults: {} };
    if (typeof keys === "string") return { list: [keys], defaults: {} };
    if (Array.isArray(keys)) return { list: keys.slice(), defaults: {} };
    return { list: Object.keys(keys), defaults: Object.assign({}, keys) };
  }

  function withCallback(promise, cb) {
    if (typeof cb === "function") {
      promise.then((v) => cb(v), () => cb(undefined));
      return undefined;
    }
    return promise;
  }

  const local = {
    get(keys, cb) {
      if (typeof keys === "function") { cb = keys; keys = null; }
      const { list, defaults } = normalizeKeys(keys);
      const p = request("storage.get", { keys: list }).then((found) => {
        const out = Object.assign({}, defaults, found || {});
        for (const [k, v] of Object.entries(found || {})) cache[k] = v;
        return out;
      });
      return withCallback(p, cb);
    },
    set(items, cb) {
      return withCallback(request("storage.set", { items: items || {} }).then(() => undefined), cb);
    },
    remove(keys, cb) {
      const list = typeof keys === "string" ? [keys] : Array.from(keys || []);
      return withCallback(request("storage.remove", { keys: list }).then(() => undefined), cb);
    },
  };

  const chrome = {
    storage: {
      local,
      onChanged: {
        addListener(fn) { storageListeners.push(fn); },
        removeListener(fn) { const i = storageListeners.indexOf(fn); if (i >= 0) storageListeners.splice(i, 1); },
      },
    },
    runtime: {
      id: "xilous-probe",
      getURL() { return ""; },
      openOptionsPage() { notify("runtime.message", { type: "OPEN_SETTINGS" }); },
      sendMessage(message, cb) {
        return withCallback(request("runtime.message", message), cb);
      },
      onMessage: {
        addListener(fn) { messageListeners.push(fn); },
        removeListener(fn) { const i = messageListeners.indexOf(fn); if (i >= 0) messageListeners.splice(i, 1); },
      },
    },
  };

  function blobToBase64(blob) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ""));
      reader.onerror = () => reject(reader.error);
      reader.readAsDataURL(blob);
    });
  }

  // The app writes the Windows clipboard itself, so a copy works whether or not
  // the page has focus, and the page's clipboard permission never comes into it.
  const clipboard = {
    writeText(text) { return request("clipboard.text", { text: String(text) }).then(() => undefined); },
    async write(items) {
      for (const item of items || []) {
        const types = Array.from(item.types || []);
        if (types.includes("image/png")) {
          const blob = await item.getType("image/png");
          await request("clipboard.png", { base64: await blobToBase64(blob) });
          return;
        }
        if (types.includes("text/plain")) {
          const blob = await item.getType("text/plain");
          await request("clipboard.text", { text: await blob.text() });
          return;
        }
      }
      throw new Error("Nothing on the clipboard item that Xilous Probe can write");
    },
    readText() { return Promise.reject(new Error("Reading the clipboard is not supported")); },
  };

  const realNavigator = globalThis.navigator;
  const navigator = new Proxy(realNavigator, {
    get(target, prop) {
      if (prop === "clipboard") return clipboard;
      const value = Reflect.get(target, prop, target);
      return typeof value === "function" ? value.bind(target) : value;
    },
  });

  return { chrome, navigator, receive, notify };
})
