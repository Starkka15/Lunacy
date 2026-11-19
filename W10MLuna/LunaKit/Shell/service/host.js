// Lunacy's JS service host, for W10M: AndroidLuna's lunacy/services/host.js, in a frame. Runs
// one webOS JS service package as run-js-service and jsservicelauncher/bootstrap-node.js did,
// on node.js (this folder). The bus is JsServices.cs, one JSON message at a time through the
// bridge: request, cancel and callResponse come in (__lunacyHost.receive); response, call,
// cancelCall, ready and exit go out.
(function () {
	"use strict";
	var H = top.LunaHost;
	var W = window.__lunacyWindow;
	var N = window.__lunacyNode;
	var EventEmitter = N.EventEmitter;
	var util = N.util;
	var query = {};
	location.search.replace(/^\?/, "").split("&").forEach(function (kv) {
		var i = kv.indexOf("=");
		if (i > 0) { query[kv.substr(0, i)] = decodeURIComponent(kv.substr(i + 1)); }
	});
	var SERVICE_DIR = query.dir;

	function send(msg) {
		if (process.__exited() && msg.t !== "exit") { return; }
		H.serviceSend(W, JSON.stringify(msg));
	}
	window.__lunacyExit = function (code) { send({ t: "exit", code: code | 0 }); };

	// ---- palmbus ----

	var nextCall = 1;
	var handles = [];
	var outgoing = {};

	function Message(m) { this.m = m; }
	Message.prototype.payload = function () { return this.m.payload; };
	Message.prototype.category = function () { return this.m.category || "/"; };
	Message.prototype.method = function () { return this.m.method; };
	Message.prototype.uniqueToken = function () { return String(this.m.id); };
	Message.prototype.token = function () { return this.m.id; };
	Message.prototype.sender = function () { return this.m.sender || ""; };
	// A service's own bus name when a service called (mojoservice's controller_command adopts
	// an activity only from com.palm.activitymanager); an app's call has none.
	Message.prototype.senderServiceName = function () { return this.m.senderService || (this.m.fromService ? this.m.sender : "") || ""; };
	Message.prototype.applicationID = function () { return this.m.sender || ""; };
	Message.prototype.isSubscription = function () { return !!this.m.subscribe; };
	Message.prototype.respond = function (json) { send({ t: "response", id: this.m.id, payload: String(json) }); return true; };
	Message.prototype.print = function () {};

	function Request(url, payload, subscribe) {
		EventEmitter.call(this);
		this.id = "c" + nextCall++;
		outgoing[this.id] = this;
		send({ t: "call", id: this.id, url: url, payload: payload, subscribe: subscribe });
	}
	util.inherits(Request, EventEmitter);
	Request.prototype.cancel = function () {
		if (outgoing[this.id]) { delete outgoing[this.id]; send({ t: "cancelCall", id: this.id }); }
	};

	function Handle(name, isPublic) {
		// ls-hubd refused an unnamed handle on the private bus; PalmCall relies on that.
		if (!name && !isPublic) { throw new Error("Invalid permissions for (null)"); }
		EventEmitter.call(this);
		this.name = name;
		this.isPublic = !!isPublic;
		this.methods = {};
		handles.push(this);
	}
	util.inherits(Handle, EventEmitter);
	Handle.prototype.registerMethod = function (category, method) {
		var c = category || "/";
		if (c.charAt(0) !== "/") { c = "/" + c; }
		this.methods[c.replace(/\/$/, "") + "/" + method] = true;
	};
	Handle.prototype.subscriptionAdd = function () {};
	Handle.prototype.unregister = function () { var i = handles.indexOf(this); if (i >= 0) { handles.splice(i, 1); } };
	Handle.prototype.pushRole = function () {};
	Handle.prototype.call = function (url, payload) { return new Request(url, payload, false); };
	Handle.prototype.subscribe = function (url, payload) { return new Request(url, payload, true); };
	Handle.prototype.cancel = function (req) { if (req && req.cancel) { req.cancel(); } };

	function handleFor(key, fromOutside) {
		var has = handles.filter(function (h) { return h.name && h.methods[key]; });
		var pick = has.filter(function (h) { return h.isPublic === fromOutside; })[0];
		return pick || (fromOutside ? undefined : has[0]);
	}

	function receive(json) {
		var m = JSON.parse(json);
		if (m.t === "request") {
			var key = (m.category === "/" ? "" : m.category) + "/" + m.method;
			var h = handleFor(key, m.outside);
			if (!h && m.outside && !handles.some(function (x) { return x.name === m.service && x.isPublic; })) {
				send({ t: "response", id: m.id, payload: JSON.stringify({ returnValue: false, errorCode: -1, errorText: "Service does not exist: " + m.service + "." }) });
				return;
			}
			if (!h) {
				send({ t: "response", id: m.id, payload: JSON.stringify({ returnValue: false, errorCode: -1, errorText: "Unknown method \"" + m.method + "\" for category \"" + m.category + "\"" }) });
				return;
			}
			h.emit("request", new Message(m));
		} else if (m.t === "cancel") {
			handles.slice().forEach(function (x) { x.emit("cancel", new Message(m)); });
		} else if (m.t === "callResponse") {
			var r = outgoing[m.id];
			if (r) {
				if (!m.subscribe) { delete outgoing[m.id]; }
				r.emit("response", new Message({ id: m.id, payload: m.payload, sender: m.sender, senderService: m.sender }));
			}
		}
	}
	window.__lunacyHost = {
		receive: function (json) {
			try { receive(json); } catch (e) { N.hostLog("error", "host: " + (e && e.stack || e)); }
		}
	};

	// ---- webos: include and the MojoLoader require ----

	var fs = N.core.fs;
	var webos = {
		// A file in the service's global scope (sources.json "source" entries).
		include: function (p) {
			N.runIn(window, fs.readFileSync(p, "utf8"), require("path").resolve(p));
		},
		// A MojoLoader library: its files share a fresh context (a frame) holding MojoLoader,
		// require and exports; the context's global is the result.
		require: function (req, loader, files) {
			var ctx = N.core.vm.createContext({
				// root is the service's global, where its sources define the command assistants.
				MojoLoader: loader, require: req, exports: {}, root: window, console: console, process: process, Buffer: Buffer,
				setTimeout: setTimeout.bind(window), clearTimeout: clearTimeout.bind(window),
				setInterval: setInterval.bind(window), clearInterval: clearInterval.bind(window),
				setImmediate: setImmediate.bind(window), clearImmediate: clearImmediate.bind(window)
			});
			files.forEach(function (f) { N.runIn(ctx, fs.readFileSync(f, "utf8"), f); });
			return ctx;
		}
	};

	// The console's own methods, taken now as Android's host.js takes them: Palm's frameworks
	// make console.log pmloglib's (bootstrap-node.js), and a pmloglib that looked console up at
	// each call called itself until the stack ran out (the accounts service).
	var pmloglib = {};
	["log", "info", "warn", "error", "debug"].forEach(function (level) {
		pmloglib[level] = console[level].bind(console);
	});

	N.natives.palmbus = { Handle: Handle };
	N.natives.webos = webos;
	N.natives.pmloglib = pmloglib;
	N.natives.mojoloader = function () { return require("/usr/palm/frameworks/mojoloader.js"); };

	// ---- start, as run-js-service did ----

	try {
		process.chdir(SERVICE_DIR);
		// After "--": the cgroup tasks file, then the service directory (no cgroups here).
		process.argv = ["node", "bootstrap-node.js", "--", "/no-group/not-present", SERVICE_DIR];
		N.runMain("/usr/palm/services/jsservicelauncher/bootstrap-node.js");
	} catch (e) {
		// As a process whose bootstrap threw: it ends, and its callers hear that it went.
		N.hostLog("error", "service " + SERVICE_DIR + " failed to start: " + (e && e.stack || e));
		send({ t: "exit", code: 1 });
		return;
	}
	send({ t: "ready" });
}());
