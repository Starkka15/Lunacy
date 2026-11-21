// W10MLuna's shell: the Luna shell in the WebView every webOS window lives in (SHELL.md, design
// B). Each window - a card, a dashboard, a popup alert, an app's hidden root - is a frame of
// this page, on this page's origin, so an app's windows script each other as they did on webOS
// (window.open returns the window; Enyo's windowParams and enyo.application are shared by
// reference). AndroidLuna's shell/ (ShellActivity, CardLayer, Launcher, QuickLaunch,
// NotificationArea) is the reference for what each part does; ES5, like Lunacy's page scripts.
(function () {
	"use strict";
	var H = window.LunaHost;
	if (!H) { document.body.textContent = "Lunacy: no host bridge."; return; }

	var info = JSON.parse(H.shellInfo());
	var apps = {};
	// The apps, and the launch points apps have added (ApplicationManager's addLaunchPoint), each
	// a launcher entry that launches its app with its params.
	// The apps hidden with Software Manager (HiddenApps.cs): still installed and still opened by
	// other apps, but left off the launcher's pages and the dock.
	var hiddenApps = {};
	function takeApps(list, points, hidden) {
		apps = {};
		hiddenApps = {};
		(hidden || []).forEach(function (id) { hiddenApps[id] = true; });
		list.forEach(function (a) { if (hiddenApps[a.id]) { a.visible = false; } apps[a.id] = a; });
		(points || []).forEach(function (p) {
			apps[p.id] = { id: p.id, title: p.title, icon: p.icon, launchPoint: true, appId: p.appId, params: p.params, removable: p.removable, visible: true };
		});
	}
	takeApps(info.apps, info.launchPoints, info.hidden);
	var zoomApps = {};
	(info.zoomApps || []).forEach(function (id) { zoomApps[id] = true; });

	function log(msg) { try { H.shellLog(String(msg)); } catch (e) {} }
	function $(id) { return document.getElementById(id); }
	function el(tag, cls, parent) {
		var e = document.createElement(tag);
		if (cls) { e.className = cls; }
		if (parent) { parent.appendChild(e); }
		return e;
	}
	function stored(key, fallback) {
		try { var v = localStorage.getItem("lunacy-shell:" + key); return v === null ? fallback : JSON.parse(v); } catch (e) { return fallback; }
	}
	function store(key, value) { try { localStorage.setItem("lunacy-shell:" + key, JSON.stringify(value)); } catch (e) {} }

	var body = document.body;
	var cardsEl = $("cards");
	// The wallpaper preference (SystemService.cs), drawn by the host once the page is up; the
	// last one shown until then.
	function setWallpaper(url, fallback) {
		var bg = "url(\"" + url + "\"), url(\"" + fallback + "\")";
		$("wallpaper").style.backgroundImage = bg;
		store("wallpaper", bg);
	}
	$("wallpaper").style.backgroundImage = stored("wallpaper", "url(\"/__lunacy/luna/wallpapers/22.jpg\")");

	// MARK: volumes (AudioService.cs)

	// A UWP app can't set Windows' volume: media scales every app's audio and video, system and
	// ringtone the shell's own sounds.
	var volumes = { media: 0.7, system: 0.7, ringtone: 0.8 };
	function mediaVolume(doc) {
		try {
			var els = doc.querySelectorAll("audio, video");
			for (var i = 0; i < els.length; i++) { els[i].volume = volumes.media; }
			var frames = doc.querySelectorAll("iframe");
			for (var j = 0; j < frames.length; j++) { try { mediaVolume(frames[j].contentDocument); } catch (e) {} }
		} catch (e) {}
	}
	// A frame's media takes the volume as it starts playing (play doesn't bubble: captured).
	function watchMedia(frame) {
		try {
			var doc = frame.contentDocument;
			if (!doc || doc.__lunacyVolume) { return; }
			doc.__lunacyVolume = true;
			doc.addEventListener("play", function (e) { try { e.target.volume = volumes.media; } catch (x) {} }, true);
			doc.addEventListener("loadedmetadata", function (e) { try { e.target.volume = volumes.media; } catch (x) {} }, true);
			mediaVolume(doc);
		} catch (e) {}
	}
	function setVolumes(v) {
		volumes = v;
		for (var id in windows) { if (windows[id].frame) { watchMedia(windows[id].frame); try { mediaVolume(windows[id].frame.contentDocument); } catch (e) {} } }
	}
	// The shell's own sounds: feedback at the system volume, alerts and ringtones at theirs.
	function playSound(url, volume) {
		try { var a = new Audio(url); a.volume = Math.max(0, Math.min(1, volume)); a.play(); } catch (e) {}
	}

	// MARK: windows

	var windows = {};      // id -> window record
	var cards = [];        // card ids, left to right in the card view
	var nextId = 1;
	var pendingAttributes = null;   // bridge.js's nextWindow, for the window.open that follows
	var maximized = null;  // the card on screen, or null for the card view
	var focusIndex = 0;    // the card the card view is centred on

	function windowOf(w) {
		// A page's own frames belong to its window: walk up to this page's child.
		try { while (w && w.parent && w.parent !== window) { w = w.parent; } } catch (e) { return 0; }
		for (var id in windows) {
			if (windows[id].frame.contentWindow === w) { return Number(id); }
		}
		return 0;
	}

	function isZoomed(appId) { return info.phone && info.layoutWidth > 0 && zoomApps[appId]; }
	// Device Info's switches (Lunacy's system/setFixedViewport and system/setLayout): the
	// windows open now take the change at once.
	function setZoom(appId, on) { zoomApps[appId] = Boolean(on); layoutAll(); }
	function setLayoutWidth(w) { info.layoutWidth = Number(w) || 0; layoutAll(); }

	// The window's frame laid out at its box's size, or for a TouchPad app on a phone wider and
	// scaled down to fit (Phone Zoom, AndroidLuna's fixed viewport: the shorter side
	// info.layoutWidth px).
	function layoutFrame(win, w, h) {
		var f = 1;
		if (win.type === "card" && isZoomed(win.appId)) { f = Math.min(1, Math.min(w, h) / info.layoutWidth); }
		var fw = Math.round(w / f), fh = Math.round(h / f);
		// scale: device px per page px, as AppWindow.kt's cardSize reports it.
		win.size = { width: fw, height: fh, scale: (window.devicePixelRatio || 1) * f };
		var s = win.frame.style;
		if (s.width !== fw + "px" || s.height !== fh + "px") {
			s.width = fw + "px"; s.height = fh + "px";
		}
		s.transform = f === 1 ? "" : "scale(" + f + ")";
	}

	function makeWindow(appId, type, url, opener, name, attributes) {
		var id = nextId++;
		var win = { id: id, appId: appId, type: type, opener: opener || null, name: name || "", attributes: attributes || {},
			ready: false, active: false, told: null, size: { width: 0, height: 0, scale: 1 } };
		var box;
		if (type === "card") { box = el("div", "card", cardsEl); el("div", "shield", box); }
		else if (type === "dashboard") { box = el("div", "dashboard", $("dashboards")); }
		else if (type === "popupalert") { box = el("div", "popup", $("popups")); }
		else if (type === "dock") { box = el("div", "dockwin", $("exh-windows")); }
		else { box = $("hidden"); }
		win.box = box;
		var frame = document.createElement("iframe");
		// The window's own name, as window.open gave it (a browser names the new window by its
		// target): Mojo keys a stage's callback by it, and a card named for the shell left the
		// app's scene never pushed (SimpleChat's white card). A launched window has none.
		frame.name = name || "";
		frame.src = url;
		win.frame = frame;
		windows[id] = win;
		H.windowOpened(id, appId, type);
		box.insertBefore(frame, box.firstChild);
		// Values set on the new window before its page loads stay with it (Spikes/shell-frames, T3).
		try {
			var cw = frame.contentWindow;
			cw.__lunacyWindow = id;
			if (opener) { cw.opener = opener; }
		} catch (e) { log("window " + id + " not reachable at creation: " + e.message); }
		frame.addEventListener("load", function () { watchMedia(frame); if (!win.ready) { stageReady(id); } });
		if (type === "card") {
			box.addEventListener("pointerdown", function (e) { cardPointer(win, e); });
		}
		layoutAll();
		log("opened " + type + " " + id + " " + appId + " " + url);
		return win;
	}

	function run(id, js) {
		var win = windows[id];
		if (!win) { return; }
		try { win.frame.contentWindow.eval(js); } catch (e) { log("window " + id + " script failed: " + e.message); }
	}

	// Focus, as LunaSysMgr gave it: PalmSystem.isActivated, then Mojo.stageActivated or
	// stageDeactivated, once the page has said it is ready (AppWindow.kt, deliverActive).
	function deliverActive(win) {
		if (!win.ready || win.told === win.active) { return; }
		win.told = win.active;
		var a = win.active;
		run(win.id, "if(window.PalmSystem){PalmSystem.isActivated=" + a + "}" +
			"if(window.Mojo&&Mojo." + (a ? "stageActivated" : "stageDeactivated") + "){Mojo." + (a ? "stageActivated" : "stageDeactivated") + "()}");
	}
	function setActive(win, a) { win.active = a; deliverActive(win); }
	function stageReady(id) {
		var win = windows[id];
		if (!win || win.ready) { return; }
		win.ready = true;
		if (win.active) { deliverActive(win); } else { win.told = false; }
	}

	// Takes a closed window's node off the page once its document is gone. Removing a frame
	// whose page still runs leaves that page's mutation observers firing on a dead window, and
	// EdgeHTML throws "Object expected" from the first call they make (engine.js's layout pass,
	// at every card close). So the node is hidden at once, its frame goes to about:blank, which
	// unloads the page and its observers with it, and the node goes when that has loaded.
	function detach(node, frame) {
		if (!node || !node.parentNode) { return; }
		node.style.display = "none";
		var done = false;
		function remove() { if (done) { return; } done = true; if (node.parentNode) { node.parentNode.removeChild(node); } }
		try { frame.addEventListener("load", remove); frame.src = "about:blank"; } catch (e) { remove(); return; }
		setTimeout(remove, 2000);
	}

	function closeWindow(id, quiet) {
		var win = windows[id];
		if (!win) { return; }
		delete windows[id];
		try { var cw = win.frame.contentWindow; if (cw && cw.__lunacyUnload) { cw.__lunacyUnload(); } } catch (e) {}
		if (win.type === "card") {
			var i = cards.indexOf(id);
			if (i >= 0) { cards.splice(i, 1); }
			if (maximized === id) { maximized = null; }
			detach(win.box, win.frame);
			if (focusIndex >= cards.length) { focusIndex = Math.max(0, cards.length - 1); }
		} else if (win.type === "hidden" || win.type === "service") {
			detach(win.frame, win.frame);
		} else {
			detach(win.box, win.frame);
		}
		H.windowClosed(id);
		log("closed " + win.type + " " + id + " " + win.appId);
		if (win.type === "service") { return; }
		// An app whose last card or dashboard closes is closed, its hidden root too: on webOS an
		// app lived as long as one of its windows did (a noWindow app's root keeps it alive only
		// until its windows are gone if it has none to show).
		if (!quiet) {
			var left = appWindows(win.appId);
			var shown = left.filter(function (w) { return w.type !== "hidden"; });
			if (!shown.length && left.length && win.type !== "hidden") {
				var root = left[0];
				if (!apps[win.appId] || !apps[win.appId].noWindow) { left.forEach(function (w) { closeWindow(w.id, true); }); }
				else { log("app " + win.appId + " keeps its hidden root " + root.id); }
			}
		}
		updateMode();
	}

	function appWindows(appId) {
		var r = [];
		for (var id in windows) { if (windows[id].appId === appId && windows[id].type !== "service") { r.push(windows[id]); } }
		r.sort(function (a, b) { return a.id - b.id; });
		return r;
	}

	// window.open, as webOS answered it: a card, a dashboard or a popup alert by the
	// "attributes={window:...}" bridge.js hands over first (nextWindow), returned at once.
	function openWindow(openerId, openerWin, url, name, features) {
		var opener = windows[openerId];
		if (!opener) { log("window.open from an unknown window"); return null; }
		var attrs = pendingAttributes;
		pendingAttributes = null;
		if (!attrs) {
			var m = /attributes=(\{.*\})/.exec(features || "");
			attrs = {};
			if (m) { try { attrs = JSON.parse(m[1]); } catch (e) {} }
		}
		// A name already open in the app is that window, as a browser answers window.open.
		if (name) {
			var same = appWindows(opener.appId).filter(function (w) { return w.name === name; })[0];
			if (same) { return same.frame.contentWindow; }
		}
		var href = url || "about:blank";
		// A relative URL is the opener's; one with a scheme stays as it is (EdgeHTML resolved
		// Email's ms-local-stream: URL against the app's folder, doubling it).
		if (!/^[a-z][a-z0-9+.\-]*:/i.test(href)) {
			try { var a = openerWin.document.createElement("a"); a.href = href; href = a.href; } catch (e) {}
		}
		var kind = attrs.window === "dashboard" ? "dashboard"
			: (attrs.window === "popupalert" || attrs.window === "emergency") ? "popupalert"
			// An app's own Exhibition view: the dock's, never a card.
			: attrs.window === "dockMode" ? "dock" : "card";
		var win = makeWindow(opener.appId, kind, href, openerWin, name, attrs);
		if (kind === "card") {
			// A card an app opens joins its opener's group: next to it (CardLayer).
			var at = cards.indexOf(openerId);
			cards.splice(at >= 0 ? at + 1 : cards.length, 0, win.id);
			maximize(win.id);
		} else if (kind === "dashboard") {
			updateNotifications();
		} else if (kind === "dock") {
			dockWindowOpened(win);
		}
		layoutAll();
		return win.frame.contentWindow;
	}

	// MARK: launching

	// `dock`: started for Exhibition (Placement.DOCK): its first window is a dock window, and
	// a relaunch leaves the card view alone.
	function launch(appId, params, launchedBy, dock) {
		var app = apps[appId];
		if (!app) { log("launch: no app " + appId); return; }
		if (app.launchPoint) { launch(app.appId, app.params || "", launchedBy, dock); return; }
		// A shortcut to one of the platform's settings pages (Wi-Fi, Bluetooth, date and time,
		// language): no window, no pretending Lunacy owns the setting (Android's openedInstead).
		if (app.lunacyAndroidSettings) { H.shellEvent("settings", appId); closeLauncher(); return; }
		var mine = appWindows(appId);
		if (mine.length && dock) {
			var docked = mine.filter(function (w) { return w.type === "dock"; })[0];
			if (docked) { showDockFace(docked); return; }
			try { mine[0].frame.contentWindow.__lunacyRelaunch && mine[0].frame.contentWindow.__lunacyRelaunch(params || ""); } catch (e) { log("relaunch failed: " + e.message); }
			return;
		}
		if (dock) {
			var dwin = makeWindow(appId, app.noWindow ? "hidden" : "dock", app.main + (params ? "?launchParams=" + encodeURIComponent(params) : ""), null, "", {});
			if (dwin.type === "dock") { dockWindowOpened(dwin); }
			return;
		}
		if (mine.length) {
			// Running: the root window hears the new params (Mojo.relaunch, which Enyo
			// defines), and its card comes forward.
			var root = mine[0];
			var card = mine.filter(function (w) { return w.type === "card"; })[0];
			if (card) { maximize(card.id); }
			try { root.frame.contentWindow.__lunacyRelaunch && root.frame.contentWindow.__lunacyRelaunch(params || ""); } catch (e) { log("relaunch failed: " + e.message); }
			return;
		}
		var url = app.main + (params ? "?launchParams=" + encodeURIComponent(params) : "");
		var win = makeWindow(appId, app.noWindow ? "hidden" : "card", url, null, "", {});
		if (win.type === "card") {
			// A new app's card starts a group right of the one that is up.
			var at = maximized ? cards.indexOf(maximized) + 1 : (cards.length ? focusIndex + 1 : 0);
			cards.splice(at, 0, win.id);
			maximize(win.id);
		}
		closeLauncher();
	}

	// MARK: the card view and the maximized card

	function stageSize() { return { w: window.innerWidth, h: window.innerHeight }; }
	var CARD_SCALE = 0.6, CARD_GAP = 16;
	var GESTURE = 28;   // the gesture area's height (shell.css #edge)

	function layoutAll() {
		var s = stageSize();
		var notes = $("notifications").offsetHeight || 0;
		var cardH = s.h - GESTURE - (maximized ? notesHeightWhenMax() : 0);
		var step = s.w * CARD_SCALE + CARD_GAP;
		layoutDock();
		cards.forEach(function (id, i) {
			var win = windows[id];
			if (!win) { return; }
			var b = win.box;
			b.style.width = s.w + "px"; b.style.height = cardH + "px";
			layoutFrame(win, s.w, cardH);
			b.classList.toggle("max", maximized === id);
			b.classList.toggle("inactive", i !== focusIndex);
			if (b.classList.contains("dragging")) { return; }
			if (maximized === id) {
				b.style.transform = "translate(0px,0px) scale(1)";
				b.style.zIndex = 10;
			} else {
				var x = s.w / 2 - (s.w * CARD_SCALE) / 2 + (i - focusIndex) * step + dragX;
				var y = (s.h - 95) / 2 - (cardH * CARD_SCALE) / 2;
				b.style.transform = "translate(" + x + "px," + y + "px) scale(" + CARD_SCALE + ")";
				b.style.zIndex = 1;
			}
		});
		for (var id in windows) {
			var w = windows[id];
			if (w.type === "dashboard") {
				var dh = w.attributes.height || 52;
				w.box.style.height = dh + "px";
				layoutFrame(w, s.w, dh);
			} else if (w.type === "popupalert") {
				var ph = w.attributes.height || 160;
				w.box.style.height = ph + "px";
				layoutFrame(w, s.w, ph);
			} else if (w.type === "hidden") {
				layoutFrame(w, s.w, s.h);
			}
		}
	}
	function notesHeightWhenMax() { return $("notifications").classList.contains("has") ? 29 : 0; }

	function maximize(id) {
		var win = windows[id];
		if (!win || win.type !== "card") { return; }
		// A card arriving ends Exhibition (SystemUiController::setCardWindowMaximized).
		if (exhibitionOn) { setExhibition(false); }
		var old = maximized && windows[maximized];
		maximized = id;
		focusIndex = Math.max(0, cards.indexOf(id));
		if (old && old !== win) { setActive(old, false); }
		setActive(win, true);
		updateMode();
	}

	function minimize() {
		if (!maximized) { return; }
		var win = windows[maximized];
		maximized = null;
		if (win) { setActive(win, false); }
		updateMode();
	}

	function updateMode() {
		body.classList.toggle("maximized", Boolean(maximized));
		body.classList.toggle("cardview", !maximized);
		updateNotifications();
		layoutAll();
	}

	// A card in the card view: tap to bring it up, drag sideways to move through the cards,
	// throw it up (or pull it off the bottom) to close it (CardLayer).
	var dragX = 0;
	function cardPointer(win, e) {
		if (maximized || launcherOpen) { return; }
		var b = win.box, x0 = e.clientX, y0 = e.clientY, t0 = Date.now(), axis = null, dy = 0;
		var s = stageSize(), step = s.w * CARD_SCALE + CARD_GAP;
		function move(ev) {
			var dx = ev.clientX - x0;
			dy = ev.clientY - y0;
			if (!axis && Math.abs(dx) + Math.abs(dy) > 12) { axis = Math.abs(dy) > Math.abs(dx) ? "y" : "x"; }
			if (axis === "x") { dragX = dx; cardsEl.classList.add("dragging"); setDragging(true); layoutAll(); }
			if (axis === "y") {
				b.classList.add("dragging");
				var i = cards.indexOf(win.id);
				var x = s.w / 2 - (s.w * CARD_SCALE) / 2 + (i - focusIndex) * step;
				var y = (s.h - 95) / 2 - (b.offsetHeight * CARD_SCALE) / 2 + dy;
				b.style.transform = "translate(" + x + "px," + y + "px) scale(" + CARD_SCALE + ")";
			}
		}
		function up(ev) {
			window.removeEventListener("pointermove", move);
			window.removeEventListener("pointerup", up);
			window.removeEventListener("pointercancel", up);
			var dt = Math.max(1, Date.now() - t0);
			if (!axis) { focusIndex = cards.indexOf(win.id); maximize(win.id); return; }
			if (axis === "x") {
				var moved = Math.round(-dragX / step);
				if (moved === 0 && Math.abs(dragX) / dt > 0.5) { moved = dragX < 0 ? 1 : -1; }
				focusIndex = Math.max(0, Math.min(cards.length - 1, focusIndex + moved));
				dragX = 0;
				setDragging(false);
				layoutAll();
				return;
			}
			b.classList.remove("dragging");
			var fast = Math.abs(dy) / dt > 0.6;
			if ((dy < -s.h * 0.25) || (dy < -40 && fast) || dy > s.h * 0.3) {
				b.classList.add("closing");
				var i = cards.indexOf(win.id);
				var x = s.w / 2 - (s.w * CARD_SCALE) / 2 + (i - focusIndex) * step;
				b.style.transform = "translate(" + x + "px," + (dy < 0 ? -s.h : s.h) + "px) scale(" + CARD_SCALE + ")";
				setTimeout(function () { closeWindow(win.id); }, 300);
			} else {
				layoutAll();
			}
		}
		window.addEventListener("pointermove", move);
		window.addEventListener("pointerup", up);
		window.addEventListener("pointercancel", up);
	}
	function setDragging(on) {
		cards.forEach(function (id) { var w = windows[id]; if (w) { w.box.classList.toggle("dragging", on); } });
	}

	// The swipe up from the bottom edge: the card that is up goes to the card view.
	(function () {
		var edge = $("edge");
		edge.addEventListener("pointerdown", function (e) {
			var y0 = e.clientY, done = false;
			edge.classList.add("lit");
			try { edge.setPointerCapture(e.pointerId); } catch (x) {}
			function move(ev) { if (!done && y0 - ev.clientY > 20) { done = true; log("gesture: card view"); minimize(); } }
			function up() {
				edge.classList.remove("lit");
				window.removeEventListener("pointermove", move);
				window.removeEventListener("pointerup", up);
				window.removeEventListener("pointercancel", up);
			}
			window.addEventListener("pointermove", move);
			window.addEventListener("pointerup", up);
			window.addEventListener("pointercancel", up);
		});
	})();

	// MARK: dashboards and popup alerts

	function updateNotifications() {
		var any = false, bar = $("notification-bar");
		bar.innerHTML = "";
		for (var id in windows) {
			var w = windows[id];
			if (w.type !== "dashboard") { continue; }
			any = true;
			var img = el("img", "", bar);
			img.src = w.attributes.icon || (apps[w.appId] && apps[w.appId].icon) || "/__lunacy/luna/images/default-app-icon.png";
		}
		$("notifications").classList.toggle("has", any);
		if (!any) { $("notifications").classList.remove("open"); }
	}
	$("notification-bar").addEventListener("click", function () {
		var n = $("notifications"), open = !n.classList.contains("open");
		n.classList.toggle("open", open);
		for (var id in windows) { if (windows[id].type === "dashboard") { setActive(windows[id], open); } }
		layoutAll();
	});

	// MARK: the launcher

	var launcherOpen = false;
	var pageIndex = stored("launcherPage", 0);
	var pageDefs = [
		{ designator: "apps", name: "apps" },
		{ designator: "downloads", name: "downloads" },
		{ designator: "prefs", name: "settings" }
	];
	var defaultLayout = {
		apps: ["com.palm.app.browser", "com.palm.app.email", "com.palm.app.calendar", "com.palm.app.messaging",
			"com.palm.app.notes", "com.palm.app.contacts", "com.palm.app.musicplayer", "com.palm.app.photos",
			"com.palm.app.clock", "com.palm.calculator"],
		downloads: ["com.palm.app.enyo-findapps"],
		prefs: ["com.palm.app.accounts", "com.palm.app.bluetooth", "com.palm.app.dateandtime", "com.palm.app.deviceinfo",
			"com.palm.app.exhibitionpreferences", "com.palm.app.help", "com.palm.app.searchpreferences", "com.palm.app.location",
			"com.palm.app.languagepicker", "com.palm.app.screenlock", "com.palm.app.swmanager", "com.palm.app.soundsandalerts",
			"com.palm.app.updates", "com.palm.app.wifi"]
	};

	// Where each app sits: the default layout, then any other app on the downloads page, as
	// webOS put an installed app there unless its keywords said otherwise.
	function pageItems() {
		var placed = {}, pages = {};
		pageDefs.forEach(function (p) {
			pages[p.designator] = (defaultLayout[p.designator] || []).filter(function (id) {
				if (apps[id] && apps[id].visible !== false && !placed[id]) { placed[id] = true; return true; }
				return false;
			});
		});
		Object.keys(apps).sort(function (a, b) { return apps[a].title.localeCompare(apps[b].title); }).forEach(function (id) {
			if (placed[id] || apps[id].visible === false) { return; }
			var kw = (apps[id].keywords || []).join(" ").toLowerCase();
			(/\bsettings?\b|preferences/.test(kw) ? pages.prefs : pages.downloads).push(id);
		});
		return pages;
	}

	function buildLauncher() {
		var tabsRow = $("tabs-row"), pagesRow = $("pages-row"), pages = pageItems();
		tabsRow.innerHTML = ""; pagesRow.innerHTML = "";
		var w = stageSize().w;
		pageDefs.forEach(function (p, i) {
			var tab = el("div", "tab", tabsRow);
			tab.textContent = p.name;
			tab.addEventListener("click", function () { showPage(i); });
			var page = el("div", "page", pagesRow);
			page.style.width = w + "px";
			var grid = el("div", "page-grid", page);
			// As many 84 px cells as fit between 6 px margins, the rest shared as gaps.
			var cols = Math.max(1, Math.floor((w - 12) / 84));
			var gap = (w - 12 - cols * 84) / cols;
			grid.style.paddingLeft = (6 + gap / 2) + "px";
			pages[p.designator].forEach(function (id) {
				var a = apps[id];
				var cell = el("div", "icon", grid);
				cell.style.margin = "0 " + gap / 2 + "px 9px";
				el("div", "glow", cell);
				var img = el("img", "", cell);
				img.src = a.icon;
				img.onerror = function () { img.onerror = null; img.src = "/__lunacy/luna/images/default-app-icon.png"; };
				var label = el("div", "label", cell);
				label.textContent = a.title;
				// A long press on an installed app or an added launch point offers Delete.
				if (a.removable) {
					var pressTimer = null, pressed = false;
					cell.addEventListener("pointerdown", function () { pressed = false; pressTimer = setTimeout(function () { pressed = true; askDelete(id); }, 700); });
					["pointerup", "pointercancel", "pointerleave", "pointermove"].forEach(function (ev) {
						cell.addEventListener(ev, function (e) { if (ev === "pointermove" && Math.abs(e.movementX || 0) + Math.abs(e.movementY || 0) < 4) { return; } clearTimeout(pressTimer); });
					});
					cell.addEventListener("click", function (e) { if (pressed) { e.stopImmediatePropagation(); pressed = false; } }, true);
				}
				cell.addEventListener("click", function () {
					cell.classList.add("touched");
					setTimeout(function () { cell.classList.remove("touched"); }, 3000);
					launch(id, "", "com.palm.launcher");
				});
			});
		});
		showPage(Math.min(pageIndex, pageDefs.length - 1), true);
	}

	function showPage(i, instant) {
		pageIndex = i;
		store("launcherPage", i);
		var w = stageSize().w, row = $("pages-row");
		if (instant) { row.classList.add("dragging"); }
		row.style.transform = "translateX(" + (-i * w) + "px)";
		if (instant) { setTimeout(function () { row.classList.remove("dragging"); }, 0); }
		var tabs = $("tabs-row").children;
		for (var k = 0; k < tabs.length; k++) { tabs[k].classList.toggle("selected", k === i); }
		var tw = 150, x = Math.min(0, Math.max(w - tabs.length * tw, w / 2 - (i + 0.5) * tw));
		$("tabs-row").style.transform = "translateX(" + x + "px)";
	}

	// A sideways drag on the pages turns them, snapping to a page (Launcher.kt).
	(function () {
		var pagesEl = $("pages");
		pagesEl.addEventListener("pointerdown", function (e) {
			var x0 = e.clientX, y0 = e.clientY, t0 = Date.now(), axis = null, row = $("pages-row"), w = stageSize().w;
			function move(ev) {
				var dx = ev.clientX - x0, dy = ev.clientY - y0;
				if (!axis && Math.abs(dx) + Math.abs(dy) > 10) { axis = Math.abs(dx) > Math.abs(dy) ? "x" : "y"; }
				if (axis === "x") {
					row.classList.add("dragging");
					row.style.transform = "translateX(" + (-pageIndex * w + dx) + "px)";
				}
			}
			function up(ev) {
				window.removeEventListener("pointermove", move);
				window.removeEventListener("pointerup", up);
				window.removeEventListener("pointercancel", up);
				row.classList.remove("dragging");
				if (axis !== "x") { return; }
				var dx = ev.clientX - x0, v = dx / Math.max(1, Date.now() - t0), to = pageIndex;
				if (dx < -w / 3 || v < -0.5) { to++; } else if (dx > w / 3 || v > 0.5) { to--; }
				showPage(Math.max(0, Math.min(pageDefs.length - 1, to)));
			}
			window.addEventListener("pointermove", move);
			window.addEventListener("pointerup", up);
			window.addEventListener("pointercancel", up);
		});
	})();

	// The launcher's Delete, as webOS asked it: the app and its data, or the shortcut.
	function askDelete(id) {
		var a = apps[id];
		if (!a) { return; }
		var old = $("delete-ask");
		if (old) { old.parentNode.removeChild(old); }
		var box = el("div", "", body);
		box.id = "delete-ask";
		var text = el("div", "delete-text", box);
		text.textContent = a.launchPoint ? "Delete the shortcut \u201c" + a.title + "\u201d?" : "Delete " + a.title + "? Its data goes with it.";
		var row = el("div", "delete-row", box);
		var yes = el("div", "delete-yes", row); yes.textContent = "Delete";
		var no = el("div", "delete-no", row); no.textContent = "Cancel";
		yes.addEventListener("click", function () { box.parentNode.removeChild(box); H.shellEvent("remove", id); });
		no.addEventListener("click", function () { box.parentNode.removeChild(box); });
	}

	function openLauncher() { launcherOpen = true; body.classList.add("launcher"); }
	function closeLauncher() { launcherOpen = false; body.classList.remove("launcher"); }

	// MARK: the dock (QuickLaunch.kt, phone): four apps, the launcher button the fifth slot.

	function buildDock() {
		var items = $("dock-items");
		items.innerHTML = "";
		var ids = stored("dock", ["com.palm.app.browser", "com.palm.app.email", "com.palm.app.calendar", "com.palm.app.enyo-findapps"])
			.filter(function (id) { return apps[id] && !hiddenApps[id]; }).slice(0, 4);
		var slotW = stageSize().w / (ids.length + 1);
		var size = Math.min(59, slotW * 0.78);
		ids.forEach(function (id) {
			var slot = el("div", "dock-slot", items);
			var img = el("img", "", slot);
			img.src = apps[id].icon;
			img.style.width = size + "px"; img.style.height = size + "px";
			slot.addEventListener("click", function () { launch(id, "", "com.palm.launcher"); });
		});
		var slot = el("div", "dock-slot", items);
		var btn = el("div", "dock-launcher", slot);
		btn.style.width = size + "px"; btn.style.height = size + "px";
		btn.style.backgroundSize = size + "px " + (size * 2) + "px";
		slot.addEventListener("click", function () { if (launcherOpen) { closeLauncher(); } else { openLauncher(); } });
	}

	// MARK: Back, in the Pre3's order: what the shell has open over the cards closes first;
	// a card that is up gets webOS's back gesture (Escape); in the card view the launcher
	// opens or closes.

	function back() {
		if (exhibitionOn) { if ($("exh-menu").classList.contains("open")) { closeFaceMenu(); } else { setExhibition(false); } return true; }
		var n = $("notifications");
		if (n.classList.contains("open")) { n.classList.remove("open"); layoutAll(); return; }
		if (launcherOpen) { closeLauncher(); return; }
		if (maximized) {
			run(maximized, "(function(){function k(t){var e=document.createEvent('Events');e.initEvent(t,true,true);e.keyCode=27;e.which=27;(document.activeElement||document).dispatchEvent(e);}k('keydown');k('keyup');})()");
			return;
		}
		openLauncher();
	}

	window.addEventListener("resize", function () { layoutAll(); buildDock(); showPage(pageIndex, true); });

	// MARK: Exhibition (AndroidLuna's ExhibitionController)

	var exhibitionOn = false;
	var exhibitionApp = null;   // the app on show, or null for the Time face
	var dockShown = null;       // the dock window on show, or null for the Time face
	var faces = [];             // [{appId, title, icon}]: the apps switched on in Exhibition
	var clockTimer = null;

	// An Exhibition window at the TouchPad's dock size: its 1024 x 768 screen less the 28 px
	// status bar, upright or on its side as the screen is, as large as fits, centred, black
	// around it (Docs/luna-deltas.md B16). Its apps were written for exactly that window.
	function layoutDock() {
		var area = $("exh-windows");
		var aw = area.offsetWidth || window.innerWidth, ah = area.offsetHeight || (window.innerHeight - 28);
		var dw = aw > ah ? 1024 : 768, dh = (aw > ah ? 768 : 1024) - 28;
		var s = Math.min(aw / dw, ah / dh);
		for (var id in windows) {
			var w = windows[id];
			if (w.type !== "dock") { continue; }
			var b = w.box.style;
			b.width = Math.round(dw * s) + "px"; b.height = Math.round(dh * s) + "px";
			b.left = Math.round((aw - dw * s) / 2) + "px"; b.top = Math.round((ah - dh * s) / 2) + "px";
			w.size = { width: dw, height: dh, scale: (window.devicePixelRatio || 1) * s };
			var fs = w.frame.style;
			fs.width = dw + "px"; fs.height = dh + "px"; fs.transform = "scale(" + s + ")";
		}
	}

	function dockWindowOpened(win) {
		// An app's second dock window replaces its first, as LunaSysMgr closed the one running.
		appWindows(win.appId).forEach(function (w) { if (w.type === "dock" && w !== win) { closeWindow(w.id, true); } });
		win.box.style.opacity = 0;
		layoutDock();
		if (exhibitionOn && win.appId === exhibitionApp) { showDockFace(win); }
	}

	// Shows a dock window, or the Time face (null), in place of what was showing; the rest stay
	// running out of sight, and only the one on show has the focus.
	function showDockFace(win) {
		dockShown = win;
		for (var id in windows) {
			var w = windows[id];
			if (w.type !== "dock") { continue; }
			w.box.style.opacity = w === win ? 1 : 0;
			w.box.style.pointerEvents = w === win ? "" : "none";
			setActive(w, w === win);
		}
		$("exh-time").style.opacity = win ? 0 : 1;
		$("exh-time").style.pointerEvents = win ? "none" : "";
	}

	function tick() {
		var d = new Date();
		var h = d.getHours(), m = d.getMinutes();
		var h24 = info.hour24;
		$("exh-clock").textContent = (h24 ? h : ((h + 11) % 12 + 1)) + ":" + (m < 10 ? "0" : "") + m;
		$("exh-date").textContent = d.toLocaleDateString(undefined, { weekday: "long", month: "long", day: "numeric" });
	}

	function faceTitle(appId) {
		if (!appId) { return "Time"; }
		return apps[appId] ? apps[appId].title : appId;
	}

	function exhibit(appId) {
		$("exh-title").textContent = faceTitle(appId);
		var docked = appWindows(appId).filter(function (w) { return w.type === "dock"; })[0];
		if (docked) { showDockFace(docked); return; }
		// The window type by name and dockMode, as DockModeWindowManager::launchApp built them:
		// apps test both.
		launch(appId, JSON.stringify({ windowType: "dockModeWindow", dockMode: true }), "", true);
	}

	// Exhibition on (display/control/setState {"state":"dock"}, Palm's Exhibition app) or off.
	// Entering again while on takes effect; only leaving is idempotent. `list`: the faces.
	function setExhibition(on, list) {
		if (!on && !exhibitionOn) { return; }
		exhibitionOn = on;
		if (on) {
			if (list) { faces = list; }
			closeLauncher();
			if (maximized && windows[maximized]) { setActive(windows[maximized], false); }
			// The face it was last left on, if still switched on; otherwise the Time face.
			var last = stored("exhibitionDefault", null);
			exhibitionApp = faces.some(function (x) { return x.appId === last; }) ? last : null;
			body.classList.add("exhibiting");
			tick();
			if (!clockTimer) { clockTimer = setInterval(tick, 1000); }
			if (exhibitionApp) { showDockFace(null); exhibit(exhibitionApp); }
			else { showDockFace(null); $("exh-title").textContent = "Time"; }
		} else {
			closeFaceMenu();
			// What was showing is where the next Exhibition opens; webOS closed what it had put
			// in the dock (DockModeCloseAppsOnExit).
			store("exhibitionDefault", exhibitionApp);
			exhibitionApp = null;
			dockShown = null;
			for (var id in windows) { if (windows[id].type === "dock") { closeWindow(Number(id)); } }
			body.classList.remove("exhibiting");
			if (clockTimer) { clearInterval(clockTimer); clockTimer = null; }
			if (maximized && windows[maximized]) { setActive(windows[maximized], true); }
			H.shellEvent("exhibition", "off");
		}
		layoutAll();
	}

	function closeFaceMenu() {
		$("exh-menu").classList.remove("open");
		$("exh-title").textContent = faceTitle(exhibitionApp);
	}

	// The title drops down the faces: Time, and every app switched on in Exhibition.
	function toggleFaceMenu() {
		var menu = $("exh-menu");
		if (menu.classList.contains("open")) { closeFaceMenu(); return; }
		menu.innerHTML = "";
		var list = [{ appId: null, title: "Time", icon: "/__lunacy/luna/images/dockmode/time-icon-48x48.png" }].concat(faces);
		list.forEach(function (face) {
			var item = el("div", "exh-item" + (face.appId === exhibitionApp ? " on" : ""), menu);
			var img = el("img", "", item); img.src = face.icon;
			el("span", "", item).textContent = face.title;
			item.addEventListener("click", function () { chooseFace(face.appId); });
		});
		menu.classList.add("open");
		$("exh-title").textContent = "Choose an App";
	}

	function chooseFace(appId) {
		closeFaceMenu();
		if (appId === exhibitionApp) { return; }
		exhibitionApp = appId;
		if (!appId) { showDockFace(null); $("exh-title").textContent = "Time"; }
		else { exhibit(appId); }
	}

	$("exh-title").addEventListener("click", toggleFaceMenu);
	$("exh-done").addEventListener("click", function () { setExhibition(false); });
	// The swipe up from the bottom edge ends the mode, as the home button does.
	(function () {
		var y0 = null;
		var edge = $("exh-edge");
		edge.addEventListener("pointerdown", function (e) { y0 = e.clientY; try { edge.setPointerCapture(e.pointerId); } catch (x) {} });
		edge.addEventListener("pointermove", function (e) { if (y0 !== null && y0 - e.clientY > 40) { y0 = null; setExhibition(false); } });
		edge.addEventListener("pointerup", function () { y0 = null; });
	})();

	// MARK: what windows and the host call

	window.__lunacyShell = {
		windowOf: windowOf,
		setZoom: setZoom,
		// A JS service package's frame (JsServices.cs): hidden, never one of an app's windows.
		// A service frame is its bus name, as its process was on the bus (the window's id names its
		// WebSQL folder, where a colon is no file name); appWindows leaves it out of the apps'.
		startService: function (name, url) { return makeWindow(name, "service", url, null, "", {}).id; },
		stopService: function (id) { closeWindow(id, true); },
		// applicationManager/updateLaunchPointIcon: the launcher and dock draw the new icon.
		setIcon: function (appId, url) {
			if (!apps[appId]) { return; }
			apps[appId].icon = url;
			buildLauncher();
			buildDock();
		},
		setLayoutWidth: setLayoutWidth,
		open: openWindow,
		close: function (id) { closeWindow(id); },
		nextWindow: function (attrs) { try { pendingAttributes = JSON.parse(attrs || "{}"); } catch (e) { pendingAttributes = {}; } },
		stageReady: stageReady,
		activate: function (id) { if (windows[id] && windows[id].type === "card") { maximize(id); } },
		cardSize: function (id) { var w = windows[id]; return JSON.stringify(w ? w.size : { width: stageSize().w, height: stageSize().h, scale: 1 }); },
		screenSize: function () { var s = stageSize(); return JSON.stringify({ width: s.w, height: s.h }); },
		run: run,
		launch: function (appId, params, by) { launch(appId, params, by); },
		setWallpaper: setWallpaper,
		setApps: function (list, points, hidden) { takeApps(JSON.parse(list), JSON.parse(points || "[]"), JSON.parse(hidden || "[]")); buildLauncher(); buildDock(); },
		closeApp: function (appId) { appWindows(appId).forEach(function (w) { closeWindow(w.id, true); }); updateMode(); },
		setExhibition: function (on, list) { setExhibition(Boolean(on), list ? JSON.parse(list) : null); },
		exhibiting: function () { return exhibitionOn; },
		setVolumes: setVolumes,
		playSound: function (url, volume) { playSound(url, volume); },
		back: back,
		log: log
	};

	buildLauncher();
	buildDock();
	updateMode();
	log("shell up: " + Object.keys(apps).length + " apps, " + stageSize().w + "x" + stageSize().h + (info.phone ? ", phone" : ""));
})();
