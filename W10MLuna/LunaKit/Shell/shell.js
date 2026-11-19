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
	info.apps.forEach(function (a) { apps[a.id] = a; });
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
	$("wallpaper").style.backgroundImage = "url(/__lunacy/luna/wallpapers/" + stored("wallpaper", "01.jpg") + ")";

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
		else { box = $("hidden"); }
		win.box = box;
		var frame = document.createElement("iframe");
		frame.name = "lunacy-window-" + id;
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
		frame.addEventListener("load", function () { if (!win.ready) { stageReady(id); } });
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

	function closeWindow(id, quiet) {
		var win = windows[id];
		if (!win) { return; }
		delete windows[id];
		try { var cw = win.frame.contentWindow; if (cw && cw.__lunacyUnload) { cw.__lunacyUnload(); } } catch (e) {}
		if (win.type === "card") {
			var i = cards.indexOf(id);
			if (i >= 0) { cards.splice(i, 1); }
			if (maximized === id) { maximized = null; }
			win.box.parentNode.removeChild(win.box);
			if (focusIndex >= cards.length) { focusIndex = Math.max(0, cards.length - 1); }
		} else if (win.type === "hidden") {
			win.frame.parentNode.removeChild(win.frame);
		} else {
			win.box.parentNode.removeChild(win.box);
		}
		H.windowClosed(id);
		log("closed " + win.type + " " + id + " " + win.appId);
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
		for (var id in windows) { if (windows[id].appId === appId) { r.push(windows[id]); } }
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
			: (attrs.window === "popupalert" || attrs.window === "emergency") ? "popupalert" : "card";
		var win = makeWindow(opener.appId, kind, href, openerWin, name, attrs);
		if (kind === "card") {
			// A card an app opens joins its opener's group: next to it (CardLayer).
			var at = cards.indexOf(openerId);
			cards.splice(at >= 0 ? at + 1 : cards.length, 0, win.id);
			maximize(win.id);
		} else if (kind === "dashboard") {
			updateNotifications();
		}
		layoutAll();
		return win.frame.contentWindow;
	}

	// MARK: launching

	function launch(appId, params, launchedBy) {
		var app = apps[appId];
		if (!app) { log("launch: no app " + appId); return; }
		var mine = appWindows(appId);
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

	function openLauncher() { launcherOpen = true; body.classList.add("launcher"); }
	function closeLauncher() { launcherOpen = false; body.classList.remove("launcher"); }

	// MARK: the dock (QuickLaunch.kt, phone): four apps, the launcher button the fifth slot.

	function buildDock() {
		var items = $("dock-items");
		items.innerHTML = "";
		var ids = stored("dock", ["com.palm.app.browser", "com.palm.app.email", "com.palm.app.calendar", "com.palm.app.enyo-findapps"])
			.filter(function (id) { return apps[id]; }).slice(0, 4);
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

	// MARK: what windows and the host call

	window.__lunacyShell = {
		windowOf: windowOf,
		setZoom: setZoom,
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
		back: back,
		log: log
	};

	buildLauncher();
	buildDock();
	updateMode();
	log("shell up: " + Object.keys(apps).length + " apps, " + stageSize().w + "x" + stageSize().h + (info.phone ? ", phone" : ""));
})();
