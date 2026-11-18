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
// The object is added to the top document only; a same-origin frame uses the top's. This
// script is served first in every page (AppServer.cs) with the values that don't change while
// the page lives (__LUNACY_STATIC__), since a UWP WebView has no document-start user scripts.
// Anything W10M has no counterpart for answers as "not here", never a fake success.
(function () {
	if (window.LunacyNative) { return; }
	var H = window.LunaHost;
	if (!H) { try { H = window.top.LunaHost; } catch (e) {} }
	if (!H) { return; }
	var S = __LUNACY_STATIC__;

	// WinRT turns undefined into the string "undefined" and NaN into 0; say what is meant.
	function s(v) { return v === undefined || v === null ? "" : String(v); }
	function n(v) { v = Number(v); return isFinite(v) ? Math.round(v) : 0; }

	var N = {
		// The bus (bridge.js). Replies come back through PalmServiceBridge.__reply.
		call: function (token, url, params) { H.busCall(n(token), s(url), s(params)); },
		cancel: function (token) { H.busCancel(n(token)); },
		log: function (msg) { H.log(s(msg)); },

		// The network shim (net.js).
		netSend: function (id, request) { H.netSend(n(id), s(request)); },
		netResult: function (id) { return H.netResult(n(id)); },
		netSendSync: function (request) { return H.netSendSync(s(request)); },
		netAbort: function (id) { H.netAbort(n(id)); },

		// WebSQL (websql.js).
		sqlOpen: function (name, version, host, creation) { return H.sqlOpen(s(name), s(version), s(host), !!creation); },
		sqlRequest: function (id, request) { H.sqlRequest(n(id), s(request)); },
		sqlResult: function (id) { return H.sqlResult(n(id)); },

		// What the page is told about where it runs.
		appId: function () { return S.appId; },
		appOrigin: function (appId) { return H.appOrigin(s(appId)); },
		deviceInfo: function () { return S.deviceInfo; },
		localeInfo: function () { return S.localeInfo; },
		engineMajor: function () { return -1; },  // not Chromium: none of Lunacy's Chromium-version fixes apply
		processId: function () { return S.processId; },
		activityId: function () { return -1; },
		mediaBase: function () { return ""; },
		localNetworkBlocked: function () { return false; },
		screenSize: function () { return H.screenSize(); },
		cardSize: function () { return H.cardSize(); },
		screenOrientation: function () { return H.screenOrientation(); },
		windowOrientation: function () { return H.screenOrientation(); },

		// The window and its stage.
		stageReady: function () { H.notify("stageReady", ""); },
		pageDrawn: function () { H.notify("pageDrawn", ""); },
		activate: function () { H.notify("activate", ""); },
		fullScreen: function (on) { H.fullScreen(!!on); },
		requestOrientation: function (o) { H.requestOrientation(s(o)); },
		statusBarColor: function (rgb) { H.notify("statusBarColor", s(rgb)); },
		blockScreenTimeout: function (block) { H.blockScreenTimeout(!!block); },
		nextWindow: function (attributes) { H.notify("nextWindow", s(attributes)); },

		// Text input and the clipboard.
		keyboard: function (show) { H.notify("keyboard", show ? "1" : ""); },
		keyboardResizes: function (resize) { H.notify("keyboardResizes", resize ? "1" : ""); },
		setClipboard: function (text) { H.setClipboard(s(text)); },
		copiedToClipboard: function () {},
		clipboardHasText: function () { return H.clipboardHasText(); },
		paste: function () { H.paste(); },
		cookieWritten: function () {},

		// Banners and sounds: no Luna shell around a W10MLuna card yet (README, "Still to do").
		addBanner: function (message, params, icon, soundClass, soundFile, duration) {
			return H.addBanner(s(message), s(params), s(icon), s(soundClass), s(soundFile), n(duration));
		},
		removeBanner: function (id) { H.notify("removeBanner", s(id)); },
		clearBanners: function () { H.notify("clearBanners", ""); },
		playSound: function (soundClass, soundFile, duration) { H.notify("playSound", s(soundClass) + " " + s(soundFile)); },

		// No PDK plug-ins (ARM Linux ELF) and no BrowserAdapter views on W10M: bridge.js and
		// Enyo report these.
		pluginStart: function () { return 0; },
		pluginCall: function () { return ""; },
		pluginStop: function () {}
	};
	window.LunacyNative = N;

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
				H.console(level, parts.join(" "));
			} catch (e) {}
			if (orig) { return orig.apply(console, arguments); }
		};
	});
	window.addEventListener("error", function (e) {
		try { H.console("error", "uncaught: " + e.message + " at " + e.filename + ":" + e.lineno); } catch (x) {}
	});
})();
