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

	// The device's user agent, as Android sets its WebView's (AppWindow.kt): webOS apps tell
	// webOS from elsewhere by it (hpwOS/3, webOS/2), App Catalog among them. A UWP WebView's
	// navigator.userAgent can't be set from the host, so it is defined here, on the navigator
	// and, where EdgeHTML won't take that, on its prototype in this frame.
	(function (ua) {
		if (!ua) { return; }
		var get = { get: function () { return ua; }, configurable: true };
		var appVersion = { get: function () { return ua.replace(/^Mozilla\//, ""); }, configurable: true };
		try { Object.defineProperty(navigator, "userAgent", get); Object.defineProperty(navigator, "appVersion", appVersion); } catch (e) {}
		if (navigator.userAgent !== ua) {
			try { Object.defineProperty(Navigator.prototype, "userAgent", get); Object.defineProperty(Navigator.prototype, "appVersion", appVersion); } catch (e) {}
		}
		if (navigator.userAgent !== ua) { try { H.log(W, "w10m-native: navigator.userAgent can't be set"); } catch (e) {} }
	})(S.userAgent);
	if (root) { H.pageStart(W); }
	// A page inside a window's page (Enyo's CrossAppUI: an account wizard in Email or Calendar)
	// has no window of its own, so the shell's log says nothing of it unless it does.
	else {
		try { H.log(W, "frame " + S.appId + " starting " + location.pathname); } catch (e) {}
		window.addEventListener("load", function () {
			try { H.log(W, "frame " + S.appId + " loaded, " + (document.body ? document.body.innerHTML.length : -1) + " chars of body"); } catch (e) {}
		});
	}

	// WinRT turns undefined into the string "undefined" and NaN into 0; say what is meant.
	function s(v) { return v === undefined || v === null ? "" : String(v); }
	function n(v) { v = Number(v); return isFinite(v) ? Math.round(v) : 0; }

	var N = {
		// The bus (bridge.js). Replies come back through PalmServiceBridge.__reply.
		// Whether the call stays open (Bus.StaysOpen), as Android's call answers: bridge.js keeps
		// a subscription's callback only then, so its later replies reach the page.
		call: function (token, url, params) { return H.busCall(W, n(token), s(url), s(params)) === true; },
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

	// A card's frame is removed when it closes, and its page's last mutation observers still
	// fire: EdgeHTML then throws "Object expected" from requestAnimationFrame on the detached
	// window (engine.js's layout pass, at every card close). A frame that is gone asks for
	// nothing; any other failure is still thrown.
	var raf0 = window.requestAnimationFrame;
	if (raf0) {
		window.requestAnimationFrame = function (f) {
			try { return raf0.call(window, f); }
			catch (e) { var gone = true; try { gone = !window.frameElement || !window.frameElement.parentNode; } catch (x) {} if (gone) { return 0; } throw e; }
		};
	}

	// A file:// URL that reaches a frame or an image without passing media.js's src hooks (in
	// markup, or where EdgeHTML's own src accessor won't take the hook) names nothing on
	// ms-local-stream, and the frame stays blank: Calendar's Windows account page, which Enyo's
	// CrossAppUI loads from the application manager's file:// base path. The element is pointed
	// at the same file on Lunacy's origin (media.js's __lunacyPageUrl), and the shell's log says so.
	if (window.MutationObserver) {
		var fixSrc = function (el) {
			var tag = el.tagName;
			if (tag !== "IFRAME" && tag !== "IMG") { return; }
			var u = el.getAttribute("src");
			if (!u || u.slice(0, 8).toLowerCase() !== "file:///" || !window.__lunacyPageUrl) { return; }
			var m = window.__lunacyPageUrl(u);
			if (m === u) { return; }
			H.log(W, "file URL in " + tag.toLowerCase() + " mapped: " + u.split("?")[0]);
			el.setAttribute("src", m);
			if (el.getAttribute("src") !== m) { el.src = m; }
		};
		var scan = function (node) {
			if (!node || node.nodeType !== 1) { return; }
			fixSrc(node);
			if (node.getElementsByTagName) {
				var list = node.querySelectorAll("iframe[src^='file:'], img[src^='file:']");
				for (var i = 0; i < list.length; i++) { fixSrc(list[i]); }
			}
		};
		new MutationObserver(function (records) {
			for (var i = 0; i < records.length; i++) {
				var r = records[i];
				if (r.type === "attributes") { fixSrc(r.target); continue; }
				for (var j = 0; j < r.addedNodes.length; j++) { scan(r.addedNodes[j]); }
			}
		}).observe(document, { childList: true, subtree: true, attributes: true, attributeFilter: ["src"] });
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
