/* Key Inject — Property Inspector logic.
 *
 * Registers with the Stream Deck app directly, using the documented "manual"
 * WebSocket protocol (window.connectElgatoStreamDeckSocket). Settings are
 * therefore stored BY Stream Deck itself per button and survive closing the
 * inspector — no CDN (ajsdk.js) required.
 *
 * Physical button presses are executed by the bridge app itself (it receives
 * keyDown/keyUp from Stream Deck), so buttons work even while this page is
 * closed. This page provides: the settings editor, a live test pad and a
 * release-all safety button over ws://127.0.0.1:12345.
 */
(function () {
  "use strict";

  var BRIDGE_URL = "ws://127.0.0.1:12345";

  function parseMaybe(v) {
    if (typeof v === "string") { try { return JSON.parse(v); } catch (e) { return null; } }
    return v || null;
  }

  // ------------------------------------------------------------------ //
  // Connection to the Stream Deck app (settings live there)
  // ------------------------------------------------------------------ //
  var app = {
    sock: null, up: false, port: 0,
    uuid: "", registerEvent: "",
    action: "", context: "", device: "", controller: "Keypad"
  };
  var rawActionInfo = "";

  window.connectElgatoStreamDeckSocket = function (port, pluginUuid, registerEvent, info, actionInfo) {
    app.port = port;
    app.uuid = String(pluginUuid || "");
    app.registerEvent = String(registerEvent || "registerPropertyInspector");
    rawActionInfo = typeof actionInfo === "string" ? actionInfo : JSON.stringify(actionInfo || "");

    var ai = parseMaybe(actionInfo);
    if (ai) {
      app.action = ai.action || app.action;
      app.context = ai.context || app.context;
      app.device = typeof ai.device === "string" ? ai.device : String(ai.device || "");
      app.controller = ai.controller || app.controller;
      showSlot(ai);
      var s = (ai.payload && ai.payload.settings) || ai.settings || null;
      if (s && hasContent(s)) applySettings(s, "actionInfo");
      else healFromCache("actionInfo was empty");
    } else {
      healFromCache("no actionInfo");
    }
    openAppSocket();
  };

  // Name the slot this inspector belongs to. Two buttons of the same action
  // are otherwise indistinguishable, which is how edits end up on the wrong
  // button while a different one is being pressed.
  function showSlot(ai) {
    var el = document.getElementById("whichButton");
    if (!el) return;
    var c = (ai.payload && ai.payload.coordinates) || ai.coordinates || null;
    var where = c ? (app.controller + " col " + c.column + ", row " + c.row) : app.controller;
    el.textContent = "editing: " + where + "  (id " + (app.context || "?").slice(0, 8) + ")";
  }

  // Settings can arrive wrapped one level deep ("settings":{"settings":{…}}),
  // which used to look like "no settings at all" to this page - the UI then
  // went blank and the next save wiped the button.
  function unwrap(s) {
    var guard = 0;
    while (s && typeof s.settings === "object" && s.settings !== null && guard++ < 4) s = s.settings;
    return s;
  }

  // A settings object counts as real only if it carries something actionable.
  // Stream Deck hands back an empty object for a fresh button, and if we treat
  // that as "the settings are empty" the next save wipes the button.
  function hasContent(s) {
    s = unwrap(s);
    if (!s) return false;
    return !!(s.keys || s.sequence || s.mode || s.cooldownMs || s.Title);
  }

  function healFromCache(why) {
    var s = parseMaybe(localStorage.getItem(cacheKey()));
    if (s && hasContent(s)) {
      applySettings(s, "local cache");
      note("settings were empty from Stream Deck (" + why + "); restored " +
           (s.keys || s.sequence) + " from local cache and pushed it back");
      persist();
    } else {
      note("no settings yet for this button (" + why + ")");
    }
  }

  function cacheKey() { return "keyinject:" + (app.context || "default"); }

  function openAppSocket() {
    if (!app.port) return;
    var sock = new WebSocket("ws://127.0.0.1:" + app.port);
    app.sock = sock;
    sock.onopen = function () {
      app.up = true;
      sock.send(JSON.stringify({ event: app.registerEvent, uuid: app.uuid }));
      note("app socket up ctx=" + app.context + " actionInfo=" + rawActionInfo.slice(0, 200));
      // Ask Stream Deck for the current settings of this action instance;
      // the injected actionInfo can be stale/empty on reopen.
      sock.send(JSON.stringify({
        event: "getSettings",
        action: app.action,
        context: app.uuid,
        device: app.device,
        payload: {}
      }));
    };
    sock.onmessage = function (ev) {
      var m = parseMaybe(ev.data);
      if (!m) return;
      var e = m.event || "";
      if (e) note("app event: " + e);
      if (e === "willAppear" || e === "didReceiveSettings") {
        var s = (m.payload && m.payload.settings) || null;
        if (hasContent(s)) applySettings(s, e);
        else healFromCache(e + " came back empty");
      }
    };
    sock.onclose = function () {
      app.up = false;
      app.sock = null;
      setTimeout(openAppSocket, 800);
    };
    sock.onerror = function () { };
  }

  function appSend(obj) {
    if (app.up && app.sock && app.sock.readyState === 1) {
      app.sock.send(JSON.stringify(obj));
      return true;
    }
    return false;
  }

  // Persist settings to Stream Deck (per button). Also cached locally per
  // context as a fallback in case the app hands us empty settings later.
  function persist() {
    var keysVal = keysEl.value.trim();
    var seqVal = seqEl.value.trim();

    // Wipe guard: an empty field we never typed into means the settings load
    // failed, not that the user cleared the button. Blank settings arriving
    // from Stream Deck used to be saved back over a configured button.
    if (!keysVal && !uiEdited.keys && currentSettings.keys) {
      keysVal = currentSettings.keys;
      keysEl.value = keysVal;
      note("kept key \"" + keysVal + "\" (empty settings from Stream Deck, not a clear)");
    }
    if (!seqVal && !uiEdited.sequence && currentSettings.sequence) {
      seqVal = currentSettings.sequence;
      seqEl.value = seqVal;
      note("kept sequence (empty settings from Stream Deck, not a clear)");
    }

    currentSettings = {
      mode: modeEl.value,
      keys: keysVal,
      sequence: seqVal,
      releaseMs: parseInt(releaseEl.value, 10) || 10000,
      cooldownMs: Math.round((parseFloat(cooldownEl.value) || 0) * 1000),
      Title: titleEl.value.trim()
    };
    // NB: PI commands must carry the *property inspector's* own uuid as
    // "context"; using the action context is rejected by Stream Deck
    // ("setSettings from the wrong context").
    var ok = appSend({
      event: "setSettings",
      action: app.action,
      context: app.uuid,
      device: app.device,
      payload: { settings: currentSettings }
    });
    try {
      localStorage.setItem(cacheKey(), JSON.stringify(currentSettings));
    } catch (e) { /* ignore */ }
    // Tell the bridge too. It keeps its own per-button copy, so if Stream Deck
    // later hands the plugin an empty settings object the button still works.
    bridgeSend({
      type: "settings",
      context: app.context,
      mode: currentSettings.mode,
      keys: currentSettings.keys,
      sequence: currentSettings.sequence,
      releaseMs: String(currentSettings.releaseMs),
      cooldownMs: String(currentSettings.cooldownMs)
    });
    note("settings saved (" + (ok ? "sent to app" : "local cache only") + ") " +
         currentSettings.mode + " " + currentSettings.keys);
    validateKeys();
    updateTitle();
  }

  // Token checking is the bridge's job (it owns the key map); we only display
  // the answer, so the PI cannot drift out of sync with what will be sent.
  function validateKeys() {
    var v = keysEl.value.trim();
    var hint = document.getElementById("keysHint");
    if (!v) {
      if (hint) {
        hint.innerHTML = modeEl.value === "sequence"
          ? "Sequence mode uses the Sequence field; the Keys field is not used."
          : "<b>No key set.</b> Pressing the button flashes it red instead of typing.";
      }
      return;
    }
    bridgeSend({ type: "validate", keys: v });
  }

  function showValidation(bad) {
    var hint = document.getElementById("keysHint");
    if (!hint) return;
    if (bad) {
      hint.innerHTML = "<b>Unknown key token: " + bad + "</b> — this is not sent; " +
        "the button flashes red. Use a single key, or a combo like <code>ctrl+shift+a</code>.";
    } else {
      hint.innerHTML = "Single key, or a combo like <code>ctrl+shift+a</code>. Last key is the one pressed with the modifiers.";
    }
  }

  function restoreLocal() {
    var s = parseMaybe(localStorage.getItem(cacheKey()));
    if (s && hasContent(s)) applySettings(s, "local cache");
  }

  function updateTitle() {
    var t = (titleEl.value || "").trim() || autoTitle();
    if (!t) return;
    // Stream Deck rejects setTitle sent by a property inspector, so ask the
    // bridge (the plugin) to send it on our behalf over its app session.
    bridgeSend({
      type: "title",
      title: t,
      context: app.context,
      device: app.device,
      action: app.action
    });
  }

  // ------------------------------------------------------------------ //
  // Bridge connection (test pad, release-all, status pill, breadcrumbs)
  // ------------------------------------------------------------------ //
  var bridgeSock = null;
  var bridgeUp = false;
  var pending = [];

  function bridgeSend(obj) {
    if (bridgeSock && bridgeSock.readyState === 1) {
      bridgeSock.send(JSON.stringify(obj));
    } else {
      pending.push(obj);
      if (pending.length > 20) pending.length = 0;
    }
  }

  function note(text) {
    try { bridgeSend({ type: "pi", note: String(text) }); } catch (e) { }
  }

  function connectBridge() {
    bridgeSock = new WebSocket(BRIDGE_URL);
    bridgeSock.onopen = function () {
      bridgeUp = true;
      setStatus(true);
      bridgeSend({ type: "hello" });
      var q = pending.slice(); pending.length = 0;
      q.forEach(function (m) { if (m.type !== "hello") bridgeSend(m); });
      note("PI opened, ctx=" + app.context);
    };
    bridgeSock.onclose = function () {
      bridgeUp = false;
      setStatus(false);
      setTimeout(connectBridge, 800);
    };
    bridgeSock.onmessage = function (ev) {
      var msg = parseMaybe(ev.data);
      if (!msg) return;
      if (msg.type === "validated") showValidation(msg.ok ? "" : (msg.bad || msg.keys || "?"));
      if (msg.type === "ack" && msg.ok === false) {
        console.warn("bridge error:", msg.error);
      }
    };
    bridgeSock.onerror = function () { };
  }

  // ------------------------------------------------------------------ //
  // UI
  // ------------------------------------------------------------------ //
  var $ = function (id) { return document.getElementById(id); };
  var modeEl = $("mode"), keysEl = $("keys"), seqEl = $("seq"),
      releaseEl = $("releaseMs"), titleEl = $("title"),
      cooldownEl = $("cooldown"),
      statusEl = $("status"), captureBtn = $("capture");

  var currentSettings = { mode: "tap", keys: "", sequence: "", releaseMs: 10000,
                          cooldownMs: 0, Title: "" };
  var captureArmed = false;
  // Tracks whether the user actually typed in a field. Settings that arrive
  // empty must not be mistaken for the user clearing the field.
  var uiEdited = { keys: false, sequence: false };

  function setStatus(up) {
    statusEl.textContent = up ? "bridge: connected" : "bridge: offline…";
    statusEl.className = "pill " + (up ? "ok" : "off");
  }

  function applySettings(s, source) {
    s = unwrap(s);
    if (!s) return;
    loadIntoUi(s);
    currentSettings = {
      mode: s.mode || "tap",
      keys: typeof s.keys === "string" ? s.keys : "",
      sequence: typeof s.sequence === "string" ? s.sequence : "",
      releaseMs: parseInt(s.releaseMs, 10) || 10000,
      cooldownMs: parseInt(s.cooldownMs, 10) || 0,
      Title: typeof s.Title === "string" ? s.Title : ""
    };
    note("settings applied from " + (source || "?") + " mode=" + currentSettings.mode +
         " keys=" + currentSettings.keys);
    // A fresh load is not a user edit.
    uiEdited.keys = false;
    uiEdited.sequence = false;
    validateKeys();
  }

  function loadIntoUi(s) {
    if (!s) return;
    if (s.mode) modeEl.value = s.mode;
    if (typeof s.keys === "string") keysEl.value = s.keys;
    if (typeof s.sequence === "string") seqEl.value = s.sequence;
    if (s.releaseMs) releaseEl.value = s.releaseMs;
    if (typeof s.cooldownMs === "number") cooldownEl.value = s.cooldownMs / 1000;
    if (typeof s.Title === "string") titleEl.value = s.Title;
    syncVisibility();
  }

  function syncVisibility() {
    var m = modeEl.value;
    $("keysRow").classList.toggle("hidden", m === "sequence");
    $("seqRow").classList.toggle("hidden", m !== "sequence");
    $("releaseRow").classList.toggle("hidden", m !== "hold");
    $("test").classList.toggle("hidden", m === "sequence");
  }

  function summary() {
    if (modeEl.value === "sequence") {
      var tokens = seqEl.value.split(",").map(function (t) { return t.trim(); })
        .filter(function (t) { return t && isNaN(parseInt(t, 10)); });
      return tokens.slice(0, 3).join(" ");
    }
    return keysEl.value.trim();
  }

  function autoTitle() {
    var m = modeEl.value;
    var icon = m === "hold" ? "⏺ " : m === "sequence" ? "≡ " : "▶ ";
    var s = summary();
    if (!s) return "";
    return icon + s.toUpperCase().slice(0, 20);
  }

  // Key capture: click Capture, then press any key (modifiers become a combo).
  captureBtn.addEventListener("click", function () {
    captureArmed = !captureArmed;
    captureBtn.classList.toggle("capturing", captureArmed);
    captureBtn.textContent = captureArmed ? "Press a key…" : "Capture";
  });

  document.addEventListener("keydown", function (ev) {
    if (!captureArmed) return;
    ev.preventDefault();
    ev.stopPropagation();
    if (ev.key === "Escape") { captureArmed = false; captureBtn.click(); return; }
    var mods = [];
    if (ev.ctrlKey) mods.push("Ctrl");
    if (ev.altKey) mods.push("Alt");
    if (ev.shiftKey) mods.push("Shift");
    if (ev.metaKey) mods.push("Win");
    var base = tokenFromEvent(ev);
    if (!base) return;
    var all = mods.concat([base]);
    keysEl.value = all.join("+");
    uiEdited.keys = true;
    captureArmed = false;
    captureBtn.classList.remove("capturing");
    captureBtn.textContent = "Capture";
    persist();
  }, true);

  var SPECIAL = {
    " ": "Space", Escape: "Esc", Delete: "Delete", Insert: "Insert",
    Home: "Home", End: "End", PageUp: "PageUp", PageDown: "PageDown",
    ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
    Enter: "Enter", Tab: "Tab", Backspace: "Backspace",
    Control: "Ctrl", Alt: "Alt", Shift: "Shift", Meta: "Win",
    CapsLock: "CapsLock", PrintScreen: "PrtSc", ScrollLock: "ScrollLock",
    NumLock: "NumLock", ContextMenu: "Apps"
  };

  function tokenFromEvent(ev) {
    if (ev.key in SPECIAL) return SPECIAL[ev.key];
    if (/^F\d{1,2}$/.test(ev.key)) return ev.key;
    if (ev.key.length === 1 && (ev.key === ev.key.toUpperCase())) {
      // letters/digits/symbols as-is (letters upper-case, matched by bridge)
      return ev.key.toUpperCase();
    }
    if (ev.key.length === 1) return ev.key;
    return null;
  }

  // Test pad: press and hold the button in the PI to hold the key down.
  // (Real deck buttons are handled by the bridge directly, without the PI.)
  var testDown = false;
  $("test").addEventListener("pointerdown", function (ev) {
    ev.preventDefault();
    if (!currentSettings.keys) return;
    testDown = true;
    bridgeSend({ type: "key", event: "down", keys: currentSettings.keys });
  });
  $("test").addEventListener("pointerup", function () {
    if (!testDown) return;
    testDown = false;
    bridgeSend({ type: "key", event: "up", keys: currentSettings.keys });
  });
  $("test").addEventListener("pointerleave", function () {
    if (!testDown) return;
    testDown = false;
    bridgeSend({ type: "key", event: "up", keys: currentSettings.keys });
  });

  $("releaseAll").addEventListener("click", function () {
    bridgeSend({ type: "releaseAll" });
  });

  // Preview: run one cooldown cycle on the real key, without any keystrokes,
  // so the animation can be checked outside the game.
  $("cooldownTest").addEventListener("click", function () {
    var secs = Math.round(parseFloat(cooldownEl.value) || 0) || 5;
    bridgeSend({
      type: "cooldown",
      seconds: String(secs),
      context: app.context,
      device: app.device,
      action: app.action
    });
    note("cooldown preview: " + secs + "s");
  });

  [modeEl, keysEl, seqEl, releaseEl, cooldownEl, titleEl].forEach(function (el) {
    el.addEventListener("change", persist);
  });
  keysEl.addEventListener("input", function () {
    uiEdited.keys = true;
    updateTitle();
    validateKeys();
  });
  seqEl.addEventListener("input", function () {
    uiEdited.sequence = true;
    updateTitle();
  });
  modeEl.addEventListener("change", function () { syncVisibility(); persist(); });

  syncVisibility();
  connectBridge();
})();
