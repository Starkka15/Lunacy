// W10MLuna: window.LunacyNative on Windows 10 Mobile's WebView (EdgeHTML). ES5, like the rest
// of Lunacy's page scripts.
//
// Lunacy's page scripts (bridge.js, net.js, websql.js, layout.js, textedit.js) talk to the host
// through one object, which Android provides with addJavascriptInterface: every method a
// synchronous call into Kotlin. UWP's WebView has the same kind of channel: a WinRT object
// marked [AllowForWeb] and added with AddWebAllowedObject is called synchronously and answers
// at once (LunaKit's Bridge.cs, which follows AppWindow.kt's interface method for method).
// So unlike iOSLuna there is no prompt() and no push-before-done: netResult and sqlResult read
// the host's result map, as on Android.
//
// Every webOS window is a frame of the shell's page, on its origin (SHELL.md, design B): the
// object lives on the top document, and each call names this window, which the shell knows
// by its frame. The window's own parts of webOS's window model come from the shell too:
// window.open makes a frame and returns it at once, window.close closes this one, and each
// app's localStorage is its own. This script is served first in every page (AppServer.cs) with
// the values that don't change while the page lives (__LUNACY_STATIC__), since a UWP WebView has
// no document-start user scripts. Anything W10M has no counterpart for answers as "not here",
// never a fake success.
(function () {
	if (window.LunacyNative) { return; }
	var T = null, H = null, Shell = null;
	try { T = window.top; H = T.LunaHost; Shell = T.__lunacyShell; } catch (e) {}
	if (!H || !Shell || T === window) { return; }
	var S = __LUNACY_STATIC__;
	var W = Shell.windowOf(window);
	var root = window.parent === T;   // this window's own page, not a frame inside it
	if (root) { H.pageStart(W); }

	// WinRT turns undefined into the string "undefined" and NaN into 0; say what is meant.
	function s(v) { return v === undefined || v === null ? "" : String(v); }
	function n(v) { v = Number(v); return isFinite(v) ? Math.round(v) : 0; }

	var N = {
		// The bus (bridge.js). Replies come back through PalmServiceBridge.__reply.
		call: function (token, url, params) { H.busCall(W, n(token), s(url), s(params)); },
		cancel: function (token) { H.busCancel(W, n(token)); },
		log: function (msg) { H.log(W, s(msg)); },

		// The network shim (net.js).
		netSend: function (id, request) { H.netSend(W, n(id), s(request)); },
		netResult: function (id) { return H.netResult(W, n(id)); },
		netSendSync: function (request) { return H.netSendSync(W, s(request)); },
		netAbort: function (id) { H.netAbort(W, n(id)); },

		// WebSQL (websql.js).
		sqlOpen: function (name, version, host, creation) { return H.sqlOpen(W, s(name), s(version), s(host), !!creation); },
		sqlRequest: function (id, request) { H.sqlRequest(W, n(id), s(request)); },
		sqlResult: function (id) { return H.sqlResult(W, n(id)); },

		// What the page is told about where it runs.
		appId: function () { return S.appId; },
		appOrigin: function () { return H.origin(); },
		deviceInfo: function () { return S.deviceInfo; },
		localeInfo: function () { return S.localeInfo; },
		engineMajor: function () { return -1; },  // not Chromium: none of Lunacy's Chromium-version fixes apply
		processId: function () { return S.processId; },
		activityId: function () { return -1; },
		mediaBase: function () { return ""; },
		localNetworkBlocked: function () { return false; },
		screenSize: function () { return Shell.screenSize(); },
		cardSize: function () { return Shell.cardSize(W); },
		screenOrientation: function () { return H.screenOrientation(); },
		windowOrientation: function () { return H.screenOrientation(); },

		// The window and its stage.
		stageReady: function () { Shell.stageReady(W); },
		pageDrawn: function () { Shell.stageReady(W); },
		activate: function () { Shell.activate(W); },
		fullScreen: function (on) { H.fullScreen(W, !!on); },
		requestOrientation: function (o) { H.requestOrientation(W, s(o)); },
		statusBarColor: function (rgb) { H.notify(W, "statusBarColor", s(rgb)); },
		blockScreenTimeout: function (block) { H.blockScreenTimeout(W, !!block); },
		nextWindow: function (attributes) { Shell.nextWindow(s(attributes)); },

		// Text input and the clipboard.
		keyboard: function (show) { H.notify(W, "keyboard", show ? "1" : ""); },
		keyboardResizes: function (resize) { H.notify(W, "keyboardResizes", resize ? "1" : ""); },
		setClipboard: function (text) { H.setClipboard(W, s(text)); },
		copiedToClipboard: function () {},
		clipboardHasText: function () { return H.clipboardHasText(W); },
		paste: function () { H.paste(W); },
		cookieWritten: function () {},

		// Banners and sounds.
		addBanner: function (message, params, icon, soundClass, soundFile, duration) {
			return H.addBanner(W, s(message), s(params), s(icon), s(soundClass), s(soundFile), n(duration));
		},
		removeBanner: function (id) { H.notify(W, "removeBanner", s(id)); },
		clearBanners: function () { H.notify(W, "clearBanners", ""); },
		playSound: function (soundClass, soundFile) { H.notify(W, "playSound", s(soundClass) + " " + s(soundFile)); },

		// PDK plug-ins aren't on W10M yet (SHELL.md, step 5): bridge.js and Enyo report these.
		pluginStart: function () { return 0; },
		pluginCall: function () { return ""; },
		pluginStop: function () {}
	};
	window.LunacyNative = N;

	// window.open and window.close are the shell's: a webOS window is a frame of its page, made
	// and handed back at once, as webOS handed an app the window it opened. bridge.js wraps this
	// open to pass the window's attributes first (N.nextWindow).
	window.open = function (url, name, features) { return Shell.open(W, window, s(url), s(name), s(features)); };
	if (root) { window.close = function () { Shell.close(W); }; }

	// window.opener, as the shell recorded it: the shell sets it on the frame before its page
	// loads, but EdgeHTML doesn't keep it through the page's navigation, and Enyo's
	// getRootWindow then fell through to top, the shell's page: a card of Email's lost its
	// app's enyo.application (accounts, prefs) and every window its manager.
	if (root) {
		var op = null;
		try { op = Shell.openerOf(W); } catch (e) {}
		if (op && window.opener !== op) {
			H.log(W, "opener restored (the page had " + (window.opener ? "another" : "none") + ")");
			try { Object.defineProperty(window, "opener", { value: op, writable: true, configurable: true }); }
			catch (e) { try { window.opener = op; } catch (e2) { H.log(W, "opener not set: " + e2.message); } }
		}
	}

	// On webOS a card was its own top window; here top is the shell's page. Enyo finds its app's
	// root window as opener || rootWindow || top, and when the root goes it hands the role on by
	// setting the new root's rootWindow to null: so an empty rootWindow means this window, never
	// the shell.
	if (root) {
		var rootWin = null;
		try {
			Object.defineProperty(window, "rootWindow", {
				get: function () { return rootWin || window; },
				set: function (v) { rootWin = v || null; },
				configurable: true
			});
		} catch (e) { H.log(W, "rootWindow not set: " + e.message); }
	}

	// Each app's own localStorage and sessionStorage, as each webOS app had: every app shares
	// this one origin here, so an app's keys are kept under its id. Property access works too
	// (localStorage.foo = "x"), as on a real Storage.
	function scoped(store, prefix) {
		if (!store || typeof Proxy !== "function") { return store; }
		function keys() {
			var r = [];
			for (var i = 0; i < store.length; i++) { var k = store.key(i); if (k && k.indexOf(prefix) === 0) { r.push(k.slice(prefix.length)); } }
			return r;
		}
		var api = {
			getItem: function (k) { return store.getItem(prefix + k); },
			setItem: function (k, v) { store.setItem(prefix + k, String(v)); },
			removeItem: function (k) { store.removeItem(prefix + k); },
			clear: function () { keys().forEach(function (k) { store.removeItem(prefix + k); }); },
			key: function (i) { var k = keys(); return i >= 0 && i < k.length ? k[i] : null; }
		};
		return new Proxy(api, {
			get: function (t, k) {
				if (k === "length") { return keys().length; }
				if (Object.prototype.hasOwnProperty.call(t, k)) { return t[k]; }
				if (typeof k !== "string") { return undefined; }
				var v = store.getItem(prefix + k);
				return v === null ? undefined : v;
			},
			set: function (t, k, v) { store.setItem(prefix + k, String(v)); return true; },
			deleteProperty: function (t, k) { store.removeItem(prefix + k); return true; },
			has: function (t, k) { return k === "length" || Object.prototype.hasOwnProperty.call(t, k) || store.getItem(prefix + k) !== null; },
			ownKeys: function () { return keys(); },
			getOwnPropertyDescriptor: function (t, k) {
				var v = store.getItem(prefix + k);
				return v === null ? undefined : { value: v, writable: true, enumerable: true, configurable: true };
			}
		});
	}
	try {
		var prefix = "app:" + S.appId + ":";
		var local = scoped(window.localStorage, prefix), session = scoped(window.sessionStorage, prefix);
		Object.defineProperty(window, "localStorage", { value: local, configurable: true });
		Object.defineProperty(window, "sessionStorage", { value: session, configurable: true });
	} catch (e) { H.log(W, "storage not scoped: " + e.message); }

	// The page's console in the host's log, as AndroidLuna's WebChromeClient.onConsoleMessage
	// puts it in Android's; a UWP WebView has no such hook. The console itself is unchanged.
	["log", "info", "warn", "error"].forEach(function (level) {
		var orig = console[level];
		console[level] = function () {
			try {
				var parts = [];
				for (var i = 0; i < arguments.length; i++) {
					var x = arguments[i];
					parts.push(typeof x === "string" ? x : (x && x.stack) || (function () { try { return JSON.stringify(x); } catch (e) { return String(x); } })());
				}
				H.console(W, level, parts.join(" "));
			} catch (e) {}
			if (orig) { return orig.apply(console, arguments); }
		};
	});
	window.addEventListener("error", function (e) {
		try { H.console(W, "error", "uncaught: " + e.message + " at " + e.filename + ":" + e.lineno); } catch (x) {}
	});
})();
