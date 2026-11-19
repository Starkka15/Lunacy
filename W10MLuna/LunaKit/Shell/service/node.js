// Lunacy's Node, for W10M (JsServices.cs). webOS's JS services were written for the TouchPad's
// node 0.4.12; AndroidLuna runs them on nodejs-mobile, and W10M has no Node, so this is the part
// of Node 0.4 Palm's frameworks and the ROM's services use, in a hidden frame of the shell's
// page, on the phone's own Chakra: CommonJS modules, process, Buffer, events, util, path, url,
// querystring, fs (the webOS filesystem, WebosFs.cs), http/https (NetShim.cs), crypto (hashes),
// stream, vm (a context is a frame of its own), and what host.js on Android adds for node 0.4.
//
// What this hasn't got fails as Node fails, never as a quiet success: an unknown module throws
// "Cannot find module", a child process is "not found", a server can't listen.
(function () {
	"use strict";
	var H = top.LunaHost;
	var W = window.__lunacyWindow;
	var G = window;

	// A message of at most 40 lines: Palm's futures nest each callback inside the last, and one
	// error's stack ran to thousands of frames, written to the phone's log each time.
	function hostLog(level, text) {
		text = String(text);
		var lines = text.split("\n");
		if (lines.length > 40) { text = lines.slice(0, 40).join("\n") + "\n   ... " + (lines.length - 40) + " more lines"; }
		try { H.console(W, level, text); } catch (e) {}
	}

	// ---- util ----

	function inspect(v, depth, seen) {
		depth = depth === undefined ? 2 : depth;
		seen = seen || [];
		if (v === null) { return "null"; }
		if (v === undefined) { return "undefined"; }
		var t = typeof v;
		if (t === "string") { return seen.length ? JSON.stringify(v) : v; }
		if (t === "number" || t === "boolean") { return String(v); }
		if (t === "function") { return "[Function" + (v.name ? ": " + v.name : "") + "]"; }
		if (v instanceof Error || (v && v.stack && v.message !== undefined)) { return v.stack || (v.name + ": " + v.message); }
		if (v instanceof Date) { return isNaN(v) ? "Invalid Date" : v.toISOString(); }
		if (v instanceof RegExp) { return String(v); }
		if (isBuffer(v)) { return "<Buffer " + Array.prototype.slice.call(v, 0, 50).map(function (b) { return (b < 16 ? "0" : "") + b.toString(16); }).join(" ") + (v.length > 50 ? " ..." : "") + ">"; }
		if (seen.indexOf(v) >= 0) { return "[Circular]"; }
		if (depth < 0) { return Array.isArray(v) ? "[Array]" : "[Object]"; }
		seen.push(v);
		var parts;
		try {
			if (Array.isArray(v)) {
				parts = v.map(function (x) { return inspect(x, depth - 1, seen); });
				return "[ " + parts.join(", ") + " ]";
			}
			parts = Object.keys(v).map(function (k) {
				var val;
				try { val = inspect(v[k], depth - 1, seen); } catch (e) { val = "[Getter]"; }
				return (/^[A-Za-z_$][\w$]*$/.test(k) ? k : JSON.stringify(k)) + ": " + val;
			});
			return parts.length ? "{ " + parts.join(", ") + " }" : "{}";
		} finally { seen.pop(); }
	}

	function format(f) {
		var args = Array.prototype.slice.call(arguments);
		var i = 1, s;
		if (typeof f !== "string") {
			return args.map(function (a) { return inspect(a); }).join(" ");
		}
		s = f.replace(/%[sdifjoO%]/g, function (m) {
			if (m === "%%") { return "%"; }
			if (i >= args.length) { return m; }
			var a = args[i++];
			switch (m) {
				case "%s": return typeof a === "string" ? a : inspect(a);
				case "%d": case "%i": return String(parseInt(a, 10));
				case "%f": return String(Number(a));
				case "%j": try { return JSON.stringify(a); } catch (e) { return "[Circular]"; }
				default: return inspect(a);
			}
		});
		for (; i < args.length; i++) { s += " " + (typeof args[i] === "string" ? args[i] : inspect(args[i])); }
		return s;
	}

	var util = {
		format: format,
		inspect: function (v, opts, depth) { return inspect(v, typeof opts === "object" && opts ? opts.depth : depth); },
		inherits: function (ctor, superCtor) {
			ctor.super_ = superCtor;
			ctor.prototype = Object.create(superCtor.prototype, { constructor: { value: ctor, enumerable: false, writable: true, configurable: true } });
		},
		isArray: Array.isArray,
		isDate: function (d) { return d instanceof Date; },
		isRegExp: function (r) { return r instanceof RegExp; },
		isError: function (e) { return e instanceof Error; },
		isFunction: function (f) { return typeof f === "function"; },
		isString: function (s) { return typeof s === "string"; },
		isNumber: function (n) { return typeof n === "number"; },
		isBoolean: function (b) { return typeof b === "boolean"; },
		isObject: function (o) { return o !== null && typeof o === "object"; },
		isUndefined: function (u) { return u === undefined; },
		isNull: function (n) { return n === null; },
		isNullOrUndefined: function (n) { return n === null || n === undefined; },
		isPrimitive: function (p) { return p === null || (typeof p !== "object" && typeof p !== "function"); },
		isBuffer: function (b) { return isBuffer(b); },
		debug: function (s) { hostLog("debug", "DEBUG: " + s); },
		error: function () { hostLog("error", format.apply(null, arguments)); },
		puts: function () { hostLog("log", Array.prototype.join.call(arguments, "\n")); },
		print: function () { hostLog("log", Array.prototype.join.call(arguments, "")); },
		log: function () { hostLog("log", new Date().toISOString() + " - " + format.apply(null, arguments)); },
		deprecate: function (f) { return f; },
		_extend: function (a, b) { if (b) { Object.keys(b).forEach(function (k) { a[k] = b[k]; }); } return a; }
	};

	// The frame's console is the service's log.
	["log", "info", "warn", "error", "debug"].forEach(function (level) {
		console[level] = function () { hostLog(level, format.apply(null, arguments)); };
	});
	console.trace = function () { hostLog("error", "Trace: " + format.apply(null, arguments) + "\n" + new Error().stack); };
	console.dir = function (o) { hostLog("log", inspect(o)); };
	console.assert = function (ok) { if (!ok) { throw new Error(format.apply(null, Array.prototype.slice.call(arguments, 1)) || "AssertionError"); } };
	console.time = function (l) { (console._times = console._times || {})[l] = Date.now(); };
	console.timeEnd = function (l) { var t = console._times && console._times[l]; if (t) { hostLog("log", l + ": " + (Date.now() - t) + "ms"); } };

	// ---- events ----

	function EventEmitter() { this._events = this._events || {}; }
	EventEmitter.prototype._maxListeners = 10;
	EventEmitter.prototype.setMaxListeners = function (n) { this._maxListeners = n; return this; };
	EventEmitter.prototype.getMaxListeners = function () { return this._maxListeners; };
	EventEmitter.prototype._add = function (type, f, prepend) {
		if (typeof f !== "function") { throw new TypeError("listener must be a function"); }
		if (!this._events) { this._events = {}; }
		if (this._events.newListener) { this.emit("newListener", type, f.listener || f); }
		var list = this._events[type] || (this._events[type] = []);
		if (prepend) { list.unshift(f); } else { list.push(f); }
		return this;
	};
	EventEmitter.prototype.on = EventEmitter.prototype.addListener = function (type, f) { return this._add(type, f, false); };
	EventEmitter.prototype.prependListener = function (type, f) { return this._add(type, f, true); };
	EventEmitter.prototype.once = function (type, f) {
		var self = this, fired = false;
		function g() {
			self.removeListener(type, g);
			if (!fired) { fired = true; return f.apply(this, arguments); }
		}
		g.listener = f;
		return this.on(type, g);
	};
	EventEmitter.prototype.off = EventEmitter.prototype.removeListener = function (type, f) {
		var list = this._events && this._events[type];
		if (!list) { return this; }
		for (var i = list.length - 1; i >= 0; i--) {
			if (list[i] === f || list[i].listener === f) { list.splice(i, 1); if (this._events.removeListener) { this.emit("removeListener", type, f); } break; }
		}
		if (!list.length) { delete this._events[type]; }
		return this;
	};
	EventEmitter.prototype.removeAllListeners = function (type) {
		if (!this._events) { return this; }
		if (type === undefined) { this._events = {}; } else { delete this._events[type]; }
		return this;
	};
	EventEmitter.prototype.listeners = function (type) { return ((this._events && this._events[type]) || []).map(function (f) { return f.listener || f; }); };
	EventEmitter.prototype.listenerCount = function (type) { return ((this._events && this._events[type]) || []).length; };
	EventEmitter.prototype.eventNames = function () { return Object.keys(this._events || {}); };
	EventEmitter.prototype.emit = function (type) {
		var list = this._events && this._events[type];
		if (type === "error" && (!list || !list.length)) {
			var er = arguments[1];
			if (er instanceof Error) { throw er; }
			throw new Error("Uncaught, unspecified 'error' event. (" + er + ")");
		}
		if (!list || !list.length) { return false; }
		var args = Array.prototype.slice.call(arguments, 1);
		list = list.slice();
		for (var i = 0; i < list.length; i++) { list[i].apply(this, args); }
		return true;
	};
	EventEmitter.listenerCount = function (em, type) { return em.listenerCount(type); };
	EventEmitter.EventEmitter = EventEmitter;
	EventEmitter.defaultMaxListeners = 10;

	// ---- Buffer ----

	function utf8Bytes(s) {
		var out = [], i, c, d;
		for (i = 0; i < s.length; i++) {
			c = s.charCodeAt(i);
			if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s.length && (d = s.charCodeAt(i + 1)) >= 0xDC00 && d <= 0xDFFF) {
				c = 0x10000 + ((c - 0xD800) << 10) + (d - 0xDC00); i++;
			} else if (c >= 0xD800 && c <= 0xDFFF) { c = 0xFFFD; }
			if (c < 0x80) { out.push(c); }
			else if (c < 0x800) { out.push(0xC0 | (c >> 6), 0x80 | (c & 63)); }
			else if (c < 0x10000) { out.push(0xE0 | (c >> 12), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63)); }
			else { out.push(0xF0 | (c >> 18), 0x80 | ((c >> 12) & 63), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63)); }
		}
		return out;
	}
	function utf8Text(b, start, end) {
		var s = "", parts = [], i = start, c, n, cp, k;
		while (i < end) {
			c = b[i];
			if (c < 0x80) { cp = c; n = 0; }
			else if (c >= 0xC2 && c < 0xE0) { cp = c & 31; n = 1; }
			else if (c >= 0xE0 && c < 0xF0) { cp = c & 15; n = 2; }
			else if (c >= 0xF0 && c < 0xF5) { cp = c & 7; n = 3; }
			else { cp = 0xFFFD; n = -1; }
			if (n > 0) {
				if (i + n >= end) { cp = 0xFFFD; n = end - i - 1; }
				else {
					for (k = 1; k <= n; k++) {
						if ((b[i + k] & 0xC0) !== 0x80) { cp = 0xFFFD; n = k - 1; break; }
						cp = (cp << 6) | (b[i + k] & 63);
					}
				}
			}
			i += 1 + Math.max(n, 0);
			if (cp >= 0x10000) { cp -= 0x10000; parts.push(0xD800 + (cp >> 10), 0xDC00 + (cp & 1023)); }
			else { parts.push(cp); }
			if (parts.length >= 8192) { s += String.fromCharCode.apply(null, parts); parts = []; }
		}
		return s + String.fromCharCode.apply(null, parts);
	}
	function binaryText(b, start, end, mask) {
		var s = "", i, chunk;
		for (i = start; i < end; i += 8192) {
			chunk = Array.prototype.slice.call(b, i, Math.min(end, i + 8192));
			if (mask !== 255) { chunk = chunk.map(function (x) { return x & mask; }); }
			s += String.fromCharCode.apply(null, chunk);
		}
		return s;
	}
	function normEnc(e) { return String(e || "utf8").toLowerCase().replace("-", ""); }
	function bytesOf(s, enc) {
		var i, out, bin;
		s = String(s);
		switch (normEnc(enc)) {
			case "utf8": return utf8Bytes(s);
			case "ascii": case "binary": case "latin1":
				out = new Array(s.length);
				for (i = 0; i < s.length; i++) { out[i] = s.charCodeAt(i) & 255; }
				return out;
			case "ucs2": case "utf16le":
				out = [];
				for (i = 0; i < s.length; i++) { out.push(s.charCodeAt(i) & 255, s.charCodeAt(i) >> 8); }
				return out;
			case "hex":
				out = [];
				for (i = 0; i + 1 < s.length; i += 2) { var h = parseInt(s.substr(i, 2), 16); if (isNaN(h)) { break; } out.push(h); }
				return out;
			case "base64":
				s = s.replace(/[^A-Za-z0-9+\/\-_]/g, "").replace(/-/g, "+").replace(/_/g, "/");
				while (s.length % 4) { s += "="; }
				bin = atob(s);
				out = new Array(bin.length);
				for (i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
				return out;
			default: throw new TypeError("Unknown encoding: " + enc);
		}
	}
	function wrapBytes(u) { Object.setPrototypeOf(u, Buffer.prototype); return u; }
	function Buffer(arg, enc, len) {
		var u, i;
		if (typeof arg === "number") { u = new Uint8Array(Math.max(0, arg | 0)); }
		else if (typeof arg === "string") { u = new Uint8Array(bytesOf(arg, enc)); }
		else if (arg instanceof ArrayBuffer) { u = new Uint8Array(arg, enc || 0, len === undefined ? arg.byteLength - (enc || 0) : len); }
		else if (arg && arg.type === "Buffer" && Array.isArray(arg.data)) { u = new Uint8Array(arg.data); }
		else if (arg && typeof arg.length === "number") { u = new Uint8Array(arg.length); for (i = 0; i < arg.length; i++) { u[i] = arg[i] & 255; } }
		else { u = new Uint8Array(0); }
		return wrapBytes(u);
	}
	Buffer.prototype = Object.create(Uint8Array.prototype);
	Buffer.prototype.constructor = Buffer;
	function isBuffer(b) { return b instanceof Buffer || (b && b._isBuffer === true); }
	Object.defineProperty(Buffer.prototype, "_isBuffer", { value: true });
	Buffer.isBuffer = isBuffer;
	Buffer.isEncoding = function (e) { try { bytesOf("", e); return true; } catch (x) { return false; } };
	Buffer.from = function (a, e, l) { return new Buffer(a, e, l); };
	Buffer.alloc = function (n, fill) { var b = new Buffer(n); if (fill !== undefined) { b.fill(typeof fill === "number" ? fill : 0); } return b; };
	Buffer.allocUnsafe = Buffer.allocUnsafeSlow = function (n) { return new Buffer(n); };
	Buffer.byteLength = function (s, enc) { return isBuffer(s) ? s.length : bytesOf(s, enc).length; };
	Buffer.concat = function (list, total) {
		if (total === undefined) { total = list.reduce(function (n, b) { return n + b.length; }, 0); }
		var out = new Buffer(total), at = 0;
		list.forEach(function (b) { if (at < total) { out.set(b.length + at > total ? b.subarray(0, total - at) : b, at); at += b.length; } });
		return out;
	};
	Buffer.prototype.toString = function (enc, start, end) {
		start = Math.max(0, start | 0);
		end = end === undefined ? this.length : Math.min(this.length, end);
		if (end <= start) { return ""; }
		switch (normEnc(enc)) {
			case "utf8": return utf8Text(this, start, end);
			case "ascii": return binaryText(this, start, end, 127);
			case "binary": case "latin1": return binaryText(this, start, end, 255);
			case "hex": var s = ""; for (var i = start; i < end; i++) { s += (this[i] < 16 ? "0" : "") + this[i].toString(16); } return s;
			case "base64": return btoa(binaryText(this, start, end, 255));
			case "ucs2": case "utf16le": var t = ""; for (var j = start; j + 1 < end; j += 2) { t += String.fromCharCode(this[j] | (this[j + 1] << 8)); } return t;
			default: throw new TypeError("Unknown encoding: " + enc);
		}
	};
	Buffer.prototype.toJSON = function () { return { type: "Buffer", data: Array.prototype.slice.call(this) }; };
	Buffer.prototype.inspect = function () { return inspect(this); };
	Buffer.prototype.equals = function (o) { if (this.length !== o.length) { return false; } for (var i = 0; i < this.length; i++) { if (this[i] !== o[i]) { return false; } } return true; };
	Buffer.prototype.compare = function (o) { var n = Math.min(this.length, o.length); for (var i = 0; i < n; i++) { if (this[i] !== o[i]) { return this[i] < o[i] ? -1 : 1; } } return this.length === o.length ? 0 : this.length < o.length ? -1 : 1; };
	// Node's slice shares the memory, as subarray does.
	Buffer.prototype.slice = function (s, e) { return wrapBytes(Uint8Array.prototype.subarray.call(this, s, e)); };
	Buffer.prototype.subarray = Buffer.prototype.slice;
	Buffer.prototype.write = function (s, offset, length, enc) {
		// node 0.4 also took (string, offset, encoding).
		if (typeof offset === "string") { enc = offset; offset = 0; length = undefined; }
		else if (typeof length === "string") { enc = length; length = undefined; }
		offset = offset | 0;
		var b = bytesOf(s, enc);
		var n = Math.min(b.length, this.length - offset, length === undefined ? Infinity : length);
		for (var i = 0; i < n; i++) { this[offset + i] = b[i]; }
		return n;
	};
	Buffer.prototype.copy = function (target, tStart, sStart, sEnd) {
		tStart = tStart | 0; sStart = sStart | 0;
		sEnd = sEnd === undefined ? this.length : Math.min(sEnd, this.length);
		var n = Math.max(0, Math.min(sEnd - sStart, target.length - tStart));
		target.set(Uint8Array.prototype.subarray.call(this, sStart, sStart + n), tStart);
		return n;
	};
	Buffer.prototype.indexOf = function (v, from) {
		if (typeof v === "number") { return Uint8Array.prototype.indexOf.call(this, v, from); }
		var needle = typeof v === "string" ? bytesOf(v) : v;
		outer: for (var i = from | 0; i <= this.length - needle.length; i++) {
			for (var k = 0; k < needle.length; k++) { if (this[i + k] !== needle[k]) { continue outer; } }
			return i;
		}
		return -1;
	};
	function dv(b) { return new DataView(b.buffer, b.byteOffset, b.byteLength); }
	[["UInt8", "getUint8", "setUint8", 1], ["Int8", "getInt8", "setInt8", 1],
	 ["UInt16", "getUint16", "setUint16", 2], ["Int16", "getInt16", "setInt16", 2],
	 ["UInt32", "getUint32", "setUint32", 4], ["Int32", "getInt32", "setInt32", 4],
	 ["Float", "getFloat32", "setFloat32", 4], ["Double", "getFloat64", "setFloat64", 8]].forEach(function (t) {
		var ends = t[3] === 1 ? [""] : ["LE", "BE"];
		ends.forEach(function (e) {
			Buffer.prototype["read" + t[0] + e] = function (o) { return dv(this)[t[1]](o | 0, e === "LE"); };
			Buffer.prototype["write" + t[0] + e] = function (v, o) { dv(this)[t[2]](o | 0, v, e === "LE"); return (o | 0) + t[3]; };
		});
	});
	Buffer.poolSize = 8192;

	// ---- path (posix) ----

	var cwd = "/";
	function normalizeArray(parts, absolute) {
		var out = [];
		parts.forEach(function (p) {
			if (!p || p === ".") { return; }
			if (p === "..") { if (out.length && out[out.length - 1] !== "..") { out.pop(); } else if (!absolute) { out.push(".."); } return; }
			out.push(p);
		});
		return out;
	}
	var path = {
		sep: "/", delimiter: ":",
		normalize: function (p) {
			p = String(p);
			var abs = p.charAt(0) === "/", trailing = p.length > 1 && p.charAt(p.length - 1) === "/";
			var s = normalizeArray(p.split("/"), abs).join("/");
			if (!s && !abs) { s = "."; }
			if (s && trailing) { s += "/"; }
			return (abs ? "/" : "") + s;
		},
		join: function () {
			var parts = Array.prototype.filter.call(arguments, function (p) {
				if (typeof p !== "string") { throw new TypeError("Arguments to path.join must be strings"); }
				return p.length > 0;
			});
			return path.normalize(parts.join("/") || ".");
		},
		resolve: function () {
			var r = "", abs = false;
			for (var i = arguments.length - 1; i >= -1 && !abs; i--) {
				var p = i >= 0 ? arguments[i] : cwd;
				if (typeof p !== "string" || !p) { continue; }
				r = p + "/" + r;
				abs = p.charAt(0) === "/";
			}
			return "/" + normalizeArray(r.split("/"), true).join("/");
		},
		isAbsolute: function (p) { return String(p).charAt(0) === "/"; },
		relative: function (from, to) {
			var f = path.resolve(from).split("/").filter(Boolean), t = path.resolve(to).split("/").filter(Boolean);
			var i = 0;
			while (i < f.length && i < t.length && f[i] === t[i]) { i++; }
			return f.slice(i).map(function () { return ".."; }).concat(t.slice(i)).join("/");
		},
		dirname: function (p) {
			p = String(p);
			var m = /^(\/?|)([\s\S]*?)((?:\.{1,2}|[^\/]+?|)(\.[^.\/]*|))(?:[\/]*)$/.exec(p);
			var root = m[1], dir = m[2];
			if (!root && !dir) { return "."; }
			if (dir) { dir = dir.substr(0, dir.length - 1); }
			return root + dir;
		},
		basename: function (p, ext) {
			var b = String(p).replace(/\/+$/, "");
			b = b.substr(b.lastIndexOf("/") + 1);
			if (ext && b.substr(-ext.length) === ext && b !== ext) { b = b.substr(0, b.length - ext.length); }
			return b;
		},
		extname: function (p) {
			var b = path.basename(p), i = b.lastIndexOf(".");
			return i <= 0 ? "" : b.substr(i);
		}
	};
	path.posix = path;
	// node 0.4 had these here; node 0.8 moved them to fs.
	path.exists = function (p, cb) { fs.exists(p, cb); };
	path.existsSync = function (p) { return fs.existsSync(p); };

	// ---- fs: the webOS filesystem (WebosFs.cs) ----

	function fsCall(op, args) {
		var r = JSON.parse(H.fs(op, JSON.stringify(args)));
		if (r.err) {
			var e = new Error(r.msg || r.err);
			e.code = r.err;
			e.errno = { ENOENT: -2, EEXIST: -17, ENOTDIR: -20, EISDIR: -21, ENOTEMPTY: -39, EROFS: -30, EACCES: -13 }[r.err] || -5;
			if (args.p) { e.path = args.p; }
			throw e;
		}
		return r;
	}
	function abs(p) {
		if (isBuffer(p)) { p = p.toString(); }
		if (typeof p !== "string") { throw new TypeError("path must be a string"); }
		return path.resolve(p);
	}
	function encOf(o) { return typeof o === "string" ? o : o && o.encoding; }
	function Stats(s) {
		this.dev = 2049; this.ino = 0; this.mode = s.mode; this.nlink = 1; this.uid = 0; this.gid = 0; this.rdev = 0;
		this.size = s.size; this.blksize = 4096; this.blocks = Math.ceil(s.size / 512);
		this.atime = new Date(s.atime); this.mtime = new Date(s.mtime); this.ctime = new Date(s.ctime); this.birthtime = new Date(s.ctime);
		this.atimeMs = s.atime; this.mtimeMs = s.mtime; this.ctimeMs = s.ctime;
		this._dir = s.dir;
	}
	Stats.prototype.isDirectory = function () { return this._dir; };
	Stats.prototype.isFile = function () { return !this._dir; };
	Stats.prototype.isSymbolicLink = function () { return false; };
	Stats.prototype.isBlockDevice = Stats.prototype.isCharacterDevice = Stats.prototype.isFIFO = Stats.prototype.isSocket = function () { return false; };

	function dataArgs(data, enc) {
		if (isBuffer(data) || data instanceof Uint8Array) { return { b64: Buffer.prototype.toString.call(data, "base64") }; }
		data = String(data);
		var e = normEnc(enc);
		if (e === "utf8") { return { text: data }; }
		return { b64: new Buffer(data, e).toString("base64") };
	}

	var fds = {}, nextFd = 3;
	var fs = {
		Stats: Stats,
		F_OK: 0, R_OK: 4, W_OK: 2, X_OK: 1,
		constants: { F_OK: 0, R_OK: 4, W_OK: 2, X_OK: 1, O_RDONLY: 0, O_WRONLY: 1, O_RDWR: 2, O_CREAT: 64, O_EXCL: 128, O_TRUNC: 512, O_APPEND: 1024 },
		existsSync: function (p) { try { return fsCall("exists", { p: abs(p) }).yes; } catch (e) { return false; } },
		statSync: function (p) { return new Stats(fsCall("stat", { p: abs(p) })); },
		accessSync: function (p) { fsCall("stat", { p: abs(p) }); },
		readFileSync: function (p, o) {
			var enc = encOf(o);
			if (typeof p === "number") { p = fdOf(p).path; }
			var e = enc && normEnc(enc);
			if (e === "utf8") { return fsCall("read", { p: abs(p), enc: "utf8" }).text; }
			var b = new Buffer(fsCall("read", { p: abs(p) }).b64, "base64");
			return enc ? b.toString(enc) : b;
		},
		writeFileSync: function (p, data, o) {
			var a = dataArgs(data, encOf(o));
			if (typeof p === "number") { p = fdOf(p).path; }
			a.p = abs(p);
			a.append = !!(o && typeof o === "object" && o.flag && o.flag.charAt(0) === "a");
			fsCall("write", a);
		},
		appendFileSync: function (p, data, o) {
			var a = dataArgs(data, encOf(o));
			a.p = abs(p); a.append = true;
			fsCall("write", a);
		},
		readdirSync: function (p) { return fsCall("list", { p: abs(p) }).names; },
		mkdirSync: function (p, mode) { fsCall("mkdir", { p: abs(p), recursive: !!(mode && typeof mode === "object" && mode.recursive) }); },
		rmdirSync: function (p, o) { fsCall("rmdir", { p: abs(p), recursive: !!(o && o.recursive) }); },
		unlinkSync: function (p) { fsCall("unlink", { p: abs(p) }); },
		renameSync: function (a, b) { fsCall("rename", { from: abs(a), to: abs(b) }); },
		copyFileSync: function (a, b) { fsCall("copy", { from: abs(a), to: abs(b) }); },
		realpathSync: function (p) { var a = abs(p); fsCall("stat", { p: a }); return a; },
		// Windows has no modes or owners for the webOS tree; the file has to be there.
		chmodSync: function (p) { fsCall("stat", { p: abs(p) }); },
		chownSync: function (p) { fsCall("stat", { p: abs(p) }); },
		utimesSync: function (p) { fsCall("stat", { p: abs(p) }); },
		truncateSync: function (p, len) {
			var b = fs.readFileSync(p);
			fs.writeFileSync(p, b.slice(0, len | 0));
		},
		readlinkSync: function (p) { var e = new Error("EINVAL: invalid argument, readlink '" + p + "'"); e.code = "EINVAL"; throw e; },
		symlinkSync: function () { var e = new Error("EPERM: operation not permitted, symlink"); e.code = "EPERM"; throw e; },
		// fds: a file opened is read whole and written through.
		openSync: function (p, flags) {
			flags = flags || "r";
			var a = abs(p);
			if (/^[wa]/.test(flags) || flags.indexOf("+") >= 0) {
				if (flags.indexOf("x") >= 0 && fs.existsSync(a)) { var e = new Error("EEXIST: file already exists, open '" + a + "'"); e.code = "EEXIST"; throw e; }
				if (flags.charAt(0) === "w" || !fs.existsSync(a)) { fsCall("write", { p: a, text: "" }); }
			} else {
				fsCall("stat", { p: a });
			}
			var fd = nextFd++;
			fds[fd] = { path: a, pos: 0, append: flags.charAt(0) === "a" };
			return fd;
		},
		closeSync: function (fd) { fdOf(fd); delete fds[fd]; },
		fstatSync: function (fd) { return fs.statSync(fdOf(fd).path); },
		fsyncSync: function (fd) { fdOf(fd); },
		writeSync: function (fd, data, a, b, c) {
			var f = fdOf(fd), buf;
			if (typeof data === "string") { buf = new Buffer(data, typeof b === "string" ? b : "utf8"); }
			else { var off = a | 0, len = b === undefined ? data.length - off : b; buf = Buffer.prototype.slice.call(data, off, off + len); }
			fsCall("write", { p: f.path, b64: buf.toString("base64"), append: true });
			f.pos += buf.length;
			return buf.length;
		},
		readSync: function (fd, buffer, offset, length, position) {
			var f = fdOf(fd);
			var all = fs.readFileSync(f.path);
			var at = position === null || position === undefined ? f.pos : position;
			var n = Math.max(0, Math.min(length, all.length - at));
			buffer.set(all.subarray(at, at + n), offset);
			if (position === null || position === undefined) { f.pos += n; }
			return n;
		},
		watch: function () { return new EventEmitter(); },
		watchFile: function () {},
		unwatchFile: function () {}
	};
	function fdOf(fd) {
		var f = fds[fd];
		if (!f) { var e = new Error("EBADF: bad file descriptor"); e.code = "EBADF"; throw e; }
		return f;
	}
	// The callback forms: the work is synchronous, the callback comes after, as Node's did.
	Object.keys(fs).forEach(function (k) {
		var m = /^(.*)Sync$/.exec(k);
		if (!m || m[1] === "exists") { return; }
		var sync = fs[k];
		fs[m[1]] = function () {
			var a = Array.prototype.slice.call(arguments);
			var cb = typeof a[a.length - 1] === "function" ? a.pop() : null;
			var r, err = null;
			try { r = sync.apply(fs, a); } catch (e) { err = e; }
			if (cb) { setImmediate(function () { cb(err, r); }); }
			else if (err) { hostLog("error", "fs." + m[1] + " failed with no callback: " + err.message); }
		};
	});
	fs.lstat = fs.stat; fs.lstatSync = fs.statSync;
	fs.exists = function (p, cb) { var y = fs.existsSync(p); if (cb) { setImmediate(function () { cb(y); }); } };
	fs.write = function (fd, data, a, b, c, d) {
		var args = Array.prototype.slice.call(arguments);
		var cb = typeof args[args.length - 1] === "function" ? args.pop() : null;
		var n, err = null;
		try { n = fs.writeSync.apply(fs, args); } catch (e) { err = e; }
		if (cb) { setImmediate(function () { cb(err, n, data); }); }
	};
	fs.read = function (fd, buffer, offset, length, position, cb) {
		var n, err = null;
		try { n = fs.readSync(fd, buffer, offset, length, position); } catch (e) { err = e; }
		if (cb) { setImmediate(function () { cb(err, n, buffer); }); }
	};

	// ---- stream ----

	function Stream() { EventEmitter.call(this); }
	util.inherits(Stream, EventEmitter);
	Stream.prototype.pipe = function (dest, opts) {
		var src = this;
		src.on("data", function (c) { if (dest.write(c) === false && src.pause) { src.pause(); } });
		if (dest.on) { dest.on("drain", function () { if (src.resume) { src.resume(); } }); }
		if (!opts || opts.end !== false) { src.on("end", function () { if (dest.end) { dest.end(); } }); }
		dest.emit && dest.emit("pipe", src);
		return dest;
	};
	Stream.Stream = Stream;
	// A stream that passes on what is written to it: what Readable, Writable and PassThrough
	// come to for the services here.
	function PassThrough(opts) {
		Stream.call(this);
		this.readable = this.writable = true;
		this._enc = null; this._paused = false; this._queue = []; this._ended = false;
	}
	util.inherits(PassThrough, Stream);
	PassThrough.prototype.setEncoding = function (e) { this._enc = e; return this; };
	PassThrough.prototype._out = function (c) {
		if (this._enc && isBuffer(c)) { c = c.toString(this._enc); }
		else if (!this._enc && typeof c === "string") { c = new Buffer(c); }
		this.emit("data", c);
	};
	PassThrough.prototype.write = function (c, enc, cb) {
		if (typeof enc === "function") { cb = enc; enc = null; }
		if (typeof c === "string" && enc && normEnc(enc) !== "utf8") { c = new Buffer(c, enc); }
		var self = this;
		if (this._paused) { this._queue.push(c); }
		else { setImmediate(function () { self._out(c); }); }
		if (cb) { setImmediate(cb); }
		return true;
	};
	PassThrough.prototype.end = function (c, enc, cb) {
		if (typeof c === "function") { cb = c; c = null; }
		if (c !== null && c !== undefined) { this.write(c, enc); }
		if (this._ended) { return; }
		this._ended = true;
		var self = this;
		var finish = function () {
			if (self._paused) { self._endWait = finish; return; }
			self.readable = self.writable = false;
			self.emit("finish"); self.emit("end"); self.emit("close");
			if (cb) { cb(); }
		};
		setImmediate(function () { setImmediate(finish); });
	};
	PassThrough.prototype.pause = function () { this._paused = true; return this; };
	PassThrough.prototype.resume = function () {
		this._paused = false;
		var q = this._queue; this._queue = [];
		var self = this;
		q.forEach(function (c) { self._out(c); });
		if (this._endWait) { var f = this._endWait; this._endWait = null; f(); }
		return this;
	};
	PassThrough.prototype.destroy = function () { this._ended = true; this.emit("close"); };
	Stream.PassThrough = Stream.Readable = Stream.Writable = Stream.Duplex = Stream.Transform = PassThrough;

	fs.createReadStream = function (p, o) {
		var s = new PassThrough();
		var enc = encOf(o);
		if (enc) { s.setEncoding(enc); }
		s.path = p;
		setImmediate(function () {
			var data;
			try { data = fs.readFileSync(p); } catch (e) { s.emit("error", e); return; }
			if (o && typeof o === "object" && (o.start !== undefined || o.end !== undefined)) {
				data = data.slice(o.start || 0, o.end === undefined ? data.length : o.end + 1);
			}
			s.emit("open", 3);
			if (data.length) { s.write(data); }
			s.end();
		});
		return s;
	};
	fs.createWriteStream = function (p, o) {
		var s = new EventEmitter();
		var append = o && o.flags && o.flags.charAt(0) === "a";
		s.path = p; s.writable = true; s.bytesWritten = 0;
		try { if (!append) { fs.writeFileSync(p, ""); } } catch (e) { setImmediate(function () { s.emit("error", e); }); return s; }
		setImmediate(function () { s.emit("open", 3); });
		s.write = function (c, enc, cb) {
			if (typeof enc === "function") { cb = enc; enc = null; }
			try {
				var a = dataArgs(c, enc); a.p = abs(p); a.append = true;
				fsCall("write", a);
				s.bytesWritten += isBuffer(c) ? c.length : Buffer.byteLength(String(c), enc);
			} catch (e) { setImmediate(function () { s.emit("error", e); }); return false; }
			if (cb) { setImmediate(cb); }
			return true;
		};
		s.end = function (c, enc, cb) {
			if (typeof c === "function") { cb = c; c = null; }
			if (c !== null && c !== undefined) { s.write(c, enc); }
			s.writable = false;
			setImmediate(function () { s.emit("finish"); s.emit("close"); if (cb) { cb(); } });
		};
		s.destroy = s.destroySoon = function () { s.writable = false; s.emit("close"); };
		return s;
	};

	// ---- querystring and url ----

	var querystring = {
		escape: function (s) { return encodeURIComponent(s); },
		unescape: function (s) { try { return decodeURIComponent(s); } catch (e) { return unescape(s); } },
		parse: function (s, sep, eq) {
			var o = {};
			if (typeof s !== "string" || !s.length) { return o; }
			s.split(sep || "&").forEach(function (kv) {
				if (!kv) { return; }
				var i = kv.indexOf(eq || "=");
				var k = querystring.unescape((i >= 0 ? kv.substr(0, i) : kv).replace(/\+/g, " "));
				var v = i >= 0 ? querystring.unescape(kv.substr(i + 1).replace(/\+/g, " ")) : "";
				if (!Object.prototype.hasOwnProperty.call(o, k)) { o[k] = v; }
				else if (Array.isArray(o[k])) { o[k].push(v); }
				else { o[k] = [o[k], v]; }
			});
			return o;
		},
		stringify: function (o, sep, eq) {
			sep = sep || "&"; eq = eq || "=";
			if (!o || typeof o !== "object") { return ""; }
			function str(v) { return typeof v === "string" ? v : typeof v === "number" && isFinite(v) ? String(v) : typeof v === "boolean" ? String(v) : ""; }
			return Object.keys(o).map(function (k) {
				var ek = encodeURIComponent(k) + eq;
				return Array.isArray(o[k]) ? o[k].map(function (v) { return ek + encodeURIComponent(str(v)); }).join(sep) : ek + encodeURIComponent(str(o[k]));
			}).filter(Boolean).join(sep);
		}
	};
	querystring.decode = querystring.parse; querystring.encode = querystring.stringify;

	var slashed = { "http:": 1, "https:": 1, "ftp:": 1, "gopher:": 1, "file:": 1, "ws:": 1, "wss:": 1 };
	function Url() {
		this.protocol = null; this.slashes = null; this.auth = null; this.host = null; this.port = null; this.hostname = null;
		this.hash = null; this.search = null; this.query = null; this.pathname = null; this.path = null; this.href = null;
	}
	var url = {
		Url: Url,
		parse: function (s, parseQuery, slashesHost) {
			if (s instanceof Url) { return s; }
			var u = new Url(), rest = String(s).trim(), m;
			u.href = rest;
			var hi = rest.indexOf("#");
			if (hi >= 0) { u.hash = rest.substr(hi); rest = rest.substr(0, hi); }
			var qi = rest.indexOf("?");
			if (qi >= 0) { u.search = rest.substr(qi); u.query = rest.substr(qi + 1); rest = rest.substr(0, qi); }
			else if (parseQuery) { u.search = ""; u.query = {}; }
			if (parseQuery && typeof u.query === "string") { u.query = querystring.parse(u.query); }
			if ((m = /^([a-z0-9.+\-]+:)/i.exec(rest))) { u.protocol = m[1].toLowerCase(); rest = rest.substr(m[1].length); }
			if ((slashesHost || u.protocol) && rest.substr(0, 2) === "//" && u.protocol !== "javascript:") {
				u.slashes = true;
				rest = rest.substr(2);
				var end = rest.search(/[\/]/);
				var authority = end < 0 ? rest : rest.substr(0, end);
				rest = end < 0 ? "" : rest.substr(end);
				var at = authority.lastIndexOf("@");
				if (at >= 0) { u.auth = decodeURIComponent(authority.substr(0, at)); authority = authority.substr(at + 1); }
				var pm = /:(\d*)$/.exec(authority);
				if (pm) { u.port = pm[1] || null; authority = authority.substr(0, authority.length - pm[0].length); }
				u.hostname = authority.toLowerCase().replace(/^\[|\]$/g, "");
				u.host = authority.toLowerCase() + (u.port ? ":" + u.port : "");
				if (!rest && slashed[u.protocol]) { rest = "/"; }
			}
			u.pathname = rest || (u.slashes ? "/" : null);
			if (u.pathname === null && !u.protocol && !u.search && !u.hash) { u.pathname = ""; }
			u.path = (u.pathname || "") + (u.search || "") || null;
			u.href = url.format(u);
			return u;
		},
		format: function (u) {
			if (typeof u === "string") { u = url.parse(u); }
			var auth = u.auth ? encodeURIComponent(u.auth).replace(/%3A/i, ":") + "@" : "";
			var protocol = u.protocol || "";
			if (protocol && protocol.substr(-1) !== ":") { protocol += ":"; }
			var host = "";
			if (u.host) { host = auth + u.host; }
			else if (u.hostname) { host = auth + (u.hostname.indexOf(":") >= 0 ? "[" + u.hostname + "]" : u.hostname) + (u.port ? ":" + u.port : ""); }
			var pathname = u.pathname || "";
			var search = u.search || (u.query && typeof u.query === "object" && Object.keys(u.query).length ? "?" + querystring.stringify(u.query) : "");
			if (search && search.charAt(0) !== "?") { search = "?" + search; }
			var hash = u.hash || "";
			if (hash && hash.charAt(0) !== "#") { hash = "#" + hash; }
			if (u.slashes || ((!protocol || slashed[protocol]) && host)) {
				host = "//" + host;
				if (pathname && pathname.charAt(0) !== "/") { pathname = "/" + pathname; }
			}
			return protocol + host + pathname.replace(/[?#]/g, encodeURIComponent) + search.replace("#", "%23") + hash;
		},
		// RFC 3986 section 5.2.
		resolve: function (from, to) {
			var b = url.parse(from), r = url.parse(to), t = new Url();
			if (r.protocol) { return url.format(r); }
			t.protocol = b.protocol;
			if (r.slashes && r.host !== null) { t.slashes = true; t.auth = r.auth; t.host = r.host; t.pathname = dots(r.pathname || "/"); t.search = r.search; }
			else {
				t.slashes = b.slashes; t.auth = b.auth; t.host = b.host;
				if (!r.pathname) { t.pathname = b.pathname; t.search = r.search !== null ? r.search : b.search; }
				else {
					if (r.pathname.charAt(0) === "/") { t.pathname = dots(r.pathname); }
					else {
						var base = b.pathname || (b.host ? "/" : "");
						t.pathname = dots(base.substr(0, base.lastIndexOf("/") + 1) + r.pathname);
					}
					t.search = r.search;
				}
			}
			t.hash = r.hash;
			return url.format(t);
		}
	};
	function dots(p) {
		var out = [], segs = p.split("/");
		segs.forEach(function (s, i) {
			if (s === ".") { if (i === segs.length - 1) { out.push(""); } return; }
			if (s === "..") { if (out.length > 1) { out.pop(); } if (i === segs.length - 1) { out.push(""); } return; }
			out.push(s);
		});
		return out.join("/");
	}

	// ---- process ----

	var startTime = Date.now();
	var exited = false;
	var process = new EventEmitter();
	process.title = "node";
	process.version = "v0.4.12";
	process.versions = { node: "0.4.12", v8: "3.1.8.26", chakra: "edge" };
	process.platform = "linux";
	process.arch = "arm";
	process.pid = 1000 + (W | 0);
	process.execPath = "/usr/bin/node";
	process.argv = ["node"];
	process.execArgv = [];
	process.env = { PATH: "/usr/bin:/bin:/usr/sbin:/sbin", HOME: "/home/root", USER: "root", TMPDIR: "/tmp", LANG: "en_US.UTF-8", TZ: "" };
	process.cwd = function () { return cwd; };
	process.chdir = function (d) {
		var a = path.resolve(d), s = fsCall("stat", { p: a });
		if (!s.dir) { var e = new Error("ENOTDIR: not a directory, chdir '" + a + "'"); e.code = "ENOTDIR"; throw e; }
		cwd = a;
	};
	process.nextTick = function (f) {
		var args = Array.prototype.slice.call(arguments, 1);
		Promise.resolve().then(function () { f.apply(null, args); }).catch(reportUncaught);
	};
	// The webOS launcher ran services as root; Lunacy's frame isn't, as on Android, so the
	// launcher skips its roles (bootstrap-node.js).
	process.getuid = process.geteuid = function () { return 1000; };
	process.getgid = process.getegid = function () { return 1000; };
	process.umask = function () { return 18; };
	process.uptime = function () { return (Date.now() - startTime) / 1000; };
	process.hrtime = function (prev) {
		var t = performance.now(), s = Math.floor(t / 1000), ns = Math.floor((t % 1000) * 1e6);
		if (prev) { s -= prev[0]; ns -= prev[1]; if (ns < 0) { s--; ns += 1e9; } }
		return [s, ns];
	};
	process.memoryUsage = function () { return { rss: 0, heapTotal: 0, heapUsed: 0, external: 0 }; };
	process.kill = function () {};
	process.setArgs = function () {};
	process.setName = function () {};
	process.binding = function (n) { throw new Error("No such module: " + n); };
	process.emitWarning = function (w) { hostLog("warn", "Warning: " + w); };
	process.stdout = { write: function (s) { hostLog("log", String(s).replace(/\n$/, "")); return true; }, isTTY: false };
	process.stderr = { write: function (s) { hostLog("error", String(s).replace(/\n$/, "")); return true; }, isTTY: false };
	process.stdin = new PassThrough();
	process.exit = function (code) {
		if (exited) { return; }
		exited = true;
		code = code === undefined ? 0 : code;
		try { process.emit("exit", code); } catch (e) {}
		G.__lunacyExit(code);
	};
	process.reallyExit = process.exit;
	process.abort = function () { process.exit(134); };
	process.__exited = function () { return exited; };

	function reportUncaught(e) {
		if (process.listenerCount("uncaughtException")) { process.emit("uncaughtException", e); }
		else { hostLog("error", "uncaught: " + (e && e.stack || e)); }
	}
	G.addEventListener("error", function (ev) {
		ev.preventDefault();
		reportUncaught(ev.error || new Error(ev.message + " (" + ev.filename + ":" + ev.lineno + ")"));
	});

	// ---- http and https (NetShim.cs) ----

	var nets = {}, nextNet = 1;
	G.__lunacyNetDone = function (id) {
		var cb = nets[id];
		if (!cb) { return; }
		delete nets[id];
		var r;
		try { r = JSON.parse(H.netResult(W, id)); } catch (e) { r = { error: "error", errorText: "unreadable result" }; }
		cb(r);
	};
	var STATUS_CODES = { 100: "Continue", 200: "OK", 201: "Created", 202: "Accepted", 204: "No Content", 206: "Partial Content",
		301: "Moved Permanently", 302: "Found", 303: "See Other", 304: "Not Modified", 307: "Temporary Redirect", 308: "Permanent Redirect",
		400: "Bad Request", 401: "Unauthorized", 403: "Forbidden", 404: "Not Found", 405: "Method Not Allowed", 409: "Conflict",
		410: "Gone", 412: "Precondition Failed", 413: "Payload Too Large", 429: "Too Many Requests",
		500: "Internal Server Error", 501: "Not Implemented", 502: "Bad Gateway", 503: "Service Unavailable", 504: "Gateway Timeout" };

	function IncomingMessage(r) {
		PassThrough.call(this);
		this.statusCode = r.status;
		this.statusMessage = STATUS_CODES[r.status] || "";
		this.httpVersion = "1.1"; this.httpVersionMajor = 1; this.httpVersionMinor = 1;
		var h = {}, raw = [];
		(r.headers || []).forEach(function (kv) {
			var k = String(kv[0]).toLowerCase(), v = kv[1];
			raw.push(kv[0], v);
			if (k === "set-cookie") { (h[k] = h[k] || []).push(v); }
			else if (h[k] !== undefined) { h[k] += ", " + v; }
			else { h[k] = v; }
		});
		this.headers = h; this.rawHeaders = raw; this.trailers = {};
		this.url = r.url;
		this.socket = this.connection = { remoteAddress: "", encrypted: /^https:/.test(r.url || "") };
	}
	util.inherits(IncomingMessage, PassThrough);

	function ClientRequest(options, cb, secure) {
		EventEmitter.call(this);
		if (typeof options === "string") { options = url.parse(options); }
		options = options || {};
		if (options.protocol) { secure = options.protocol === "https:"; }
		this._secure = secure;
		this.method = String(options.method || "GET").toUpperCase();
		this.path = options.path || ((options.pathname || "/") + (options.search || ""));
		this._host = options.hostname || (options.host ? String(options.host).replace(/:\d+$/, "") : "localhost");
		var hp = options.host && /:(\d+)$/.exec(options.host);
		this._port = options.port || (hp ? hp[1] : null);
		this._headers = {};
		this._names = {};
		this._chunks = [];
		this._timeout = 0;
		this._done = false;
		this.aborted = false;
		var self = this;
		var headers = options.headers || {};
		Object.keys(headers).forEach(function (k) { self.setHeader(k, headers[k]); });
		if (options.auth && !this.getHeader("authorization")) { this.setHeader("Authorization", "Basic " + new Buffer(options.auth).toString("base64")); }
		if (cb) { this.once("response", cb); }
		this.socket = this.connection = new EventEmitter();
		this.socket.setTimeout = function (ms, f) { self.setTimeout(ms, f); };
		this.socket.setNoDelay = this.socket.setKeepAlive = function () {};
		this.socket.destroy = function () { self.abort(); };
		setImmediate(function () { self.emit("socket", self.socket); });
	}
	util.inherits(ClientRequest, EventEmitter);
	ClientRequest.prototype.setHeader = function (k, v) { this._headers[k.toLowerCase()] = v; this._names[k.toLowerCase()] = k; };
	ClientRequest.prototype.getHeader = function (k) { return this._headers[k.toLowerCase()]; };
	ClientRequest.prototype.removeHeader = function (k) { delete this._headers[k.toLowerCase()]; delete this._names[k.toLowerCase()]; };
	ClientRequest.prototype.setTimeout = function (ms, f) { this._timeout = ms; if (f) { this.once("timeout", f); } return this; };
	ClientRequest.prototype.setNoDelay = ClientRequest.prototype.setSocketKeepAlive = function () {};
	ClientRequest.prototype.flushHeaders = function () {};
	ClientRequest.prototype.write = function (c, enc, cb) {
		if (typeof enc === "function") { cb = enc; enc = null; }
		if (c !== null && c !== undefined) { this._chunks.push(isBuffer(c) ? c : new Buffer(String(c), enc || "utf8")); }
		if (cb) { setImmediate(cb); }
		return true;
	};
	ClientRequest.prototype.abort = function () {
		if (this.aborted) { return; }
		this.aborted = true;
		if (this._net) { H.netAbort(W, this._net); delete nets[this._net]; }
		this.emit("abort");
	};
	ClientRequest.prototype.destroy = ClientRequest.prototype.abort;
	ClientRequest.prototype.end = function (c, enc, cb) {
		if (typeof c === "function") { cb = c; c = null; }
		if (typeof enc === "function") { cb = enc; enc = null; }
		this.write(c, enc);
		if (this._done) { return; }
		this._done = true;
		var self = this;
		var port = this._port && String(this._port) !== (this._secure ? "443" : "80") ? ":" + this._port : "";
		var host = this._host.indexOf(":") >= 0 ? "[" + this._host + "]" : this._host;
		var target = (this._secure ? "https://" : "http://") + host + port + this.path;
		var headers = [];
		Object.keys(this._headers).forEach(function (k) {
			// Windows' HTTP stack sets these itself.
			if (k === "host" || k === "content-length" || k === "connection" || k === "transfer-encoding") { return; }
			var v = self._headers[k];
			(Array.isArray(v) ? v : [v]).forEach(function (x) { headers.push([self._names[k], String(x)]); });
		});
		var body = this._chunks.length ? Buffer.concat(this._chunks) : null;
		var req = { url: target, method: this.method, headers: headers, binary: true, node: true, timeout: this._timeout || 0 };
		if (body && this.method !== "GET" && this.method !== "HEAD") { req.body = body.toString("base64"); req.bodyBase64 = true; }
		var id = this._net = nextNet++;
		nets[id] = function (r) {
			self._net = 0;
			if (self.aborted) { return; }
			if (r.error) {
				var timeout = r.error === "timeout";
				if (timeout) { self.emit("timeout"); }
				var e = new Error((timeout ? "ETIMEDOUT" : "ECONNREFUSED") + ", " + (r.errorText || "request failed") + " (" + target + ")");
				e.code = timeout ? "ETIMEDOUT" : "ECONNREFUSED";
				self.emit("error", e);
				return;
			}
			var res = new IncomingMessage(r);
			self.res = res;
			self.emit("response", res);
			var data = r.base64 ? new Buffer(r.base64, "base64") : new Buffer(r.text || "");
			if (data.length) { res.write(data); }
			res.end();
		};
		H.netSend(W, id, JSON.stringify(req));
		if (cb) { setImmediate(cb); }
	};

	function Agent(o) { this.options = o || {}; this.maxSockets = Infinity; }
	Agent.prototype.destroy = function () {};
	function makeHttp(secure) {
		var m = {
			STATUS_CODES: STATUS_CODES,
			METHODS: ["DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT"],
			Agent: Agent, globalAgent: new Agent(),
			ClientRequest: ClientRequest, IncomingMessage: IncomingMessage,
			request: function (o, cb) {
				if (typeof o === "string") { o = url.parse(o); }
				return new ClientRequest(o, cb, o && o.protocol ? o.protocol === "https:" : secure);
			},
			get: function (o, cb) { var r = m.request(o, cb); r.end(); return r; },
			createServer: function () {
				var s = new EventEmitter();
				s.listen = function (port) {
					var e = new Error("listen EACCES: a webOS service on W10M can't serve http (port " + port + ")");
					e.code = "EACCES";
					setImmediate(function () { s.emit("error", e); });
					return s;
				};
				s.close = function () {};
				return s;
			}
		};
		return m;
	}
	var http = makeHttp(false), https = makeHttp(true);

	// ---- crypto (Crypto.cs) ----

	function Hash(alg, key) { this._alg = alg; this._key = key; this._parts = []; }
	Hash.prototype.update = function (d, enc) { this._parts.push(isBuffer(d) || d instanceof Uint8Array ? new Buffer(d) : new Buffer(String(d), enc || "utf8")); return this; };
	Hash.prototype.digest = function (enc) {
		var r = JSON.parse(H.hash(this._alg, Buffer.concat(this._parts).toString("base64"), this._key ? this._key.toString("base64") : ""));
		if (r.err) { throw new Error(r.err); }
		var b = new Buffer(r.b64, "base64");
		return enc ? b.toString(enc) : b;
	};
	var crypto = {
		createHash: function (alg) { var h = new Hash(alg, null); h.digest = Hash.prototype.digest; JSON.parse(H.hash(alg, "", "")).err && h.digest(); return h; },
		createHmac: function (alg, key) { return new Hash(alg, isBuffer(key) ? key : new Buffer(String(key))); },
		randomBytes: function (n, cb) {
			var b = new Buffer(H.randomBytes(n), "base64");
			if (cb) { setImmediate(function () { cb(null, b); }); return undefined; }
			return b;
		},
		pseudoRandomBytes: function (n, cb) { return crypto.randomBytes(n, cb); },
		getHashes: function () { return ["md5", "sha1", "sha256", "sha384", "sha512"]; },
		getCiphers: function () { return []; }
	};
	["createCipher", "createCipheriv", "createDecipher", "createDecipheriv", "createSign", "createVerify", "createDiffieHellman", "pbkdf2", "pbkdf2Sync"].forEach(function (n) {
		crypto[n] = function () { throw new Error("crypto." + n + " isn't available to webOS services on W10M yet"); };
	});

	// ---- child_process ----
	// No processes on W10M: the commands webOS's services run are done here, in the service's
	// own frame - curl (AndroidLuna's curl.js, over this shim's http), and the file commands
	// (rm, mv, cp, mkdir) over its fs - with exit codes and output as the real ones gave.
	// initctl and rdx_reporter, which told upstart and the crash reporter, answer success.
	// Anything else is "not found", as a shell said of a command it hadn't got.

	var commands = {};

	// A shell command line, as sh read the ones services build: words with '' and "" quotes,
	// ; && || between commands, > >> 2> 2>&1 redirections.
	function parseLine(line) {
		var out = [], cmd = { argv: [], redirects: [] }, word = null, i = 0, s = String(line);
		function endWord() { if (word !== null) { cmd.argv.push(word); word = null; } }
		function endCmd(op) { endWord(); if (cmd.argv.length) { cmd.op = op; out.push(cmd); } cmd = { argv: [], redirects: [] }; }
		while (i < s.length) {
			var c = s.charAt(i);
			if (c === "'") { var j = s.indexOf("'", i + 1); if (j < 0) { j = s.length; } word = (word || "") + s.slice(i + 1, j); i = j + 1; continue; }
			if (c === "\"") {
				var v = ""; i++;
				while (i < s.length && s.charAt(i) !== "\"") { if (s.charAt(i) === "\\" && i + 1 < s.length && /["\\$`]/.test(s.charAt(i + 1))) { i++; } v += s.charAt(i); i++; }
				word = (word || "") + v; i++; continue;
			}
			if (c === "\\" && i + 1 < s.length) { word = (word || "") + s.charAt(i + 1); i += 2; continue; }
			if (/\s/.test(c)) { endWord(); i++; continue; }
			if (s.substr(i, 2) === "&&") { endCmd("&&"); i += 2; continue; }
			if (s.substr(i, 2) === "||") { endCmd("||"); i += 2; continue; }
			if (c === ";" || c === "\n") { endCmd(";"); i++; continue; }
			var m = /^(\d?)(>>|>&|>)/.exec(s.slice(i));
			if (m && word === null) {
				i += m[0].length;
				while (/\s/.test(s.charAt(i))) { i++; }
				var t = /^[^\s;&|]+/.exec(s.slice(i)); t = t ? t[0].replace(/^['"]|['"]$/g, "") : "";
				i += t.length;
				cmd.redirects.push({ fd: m[1] === "2" ? 2 : 1, append: m[2] === ">>", dup: m[2] === ">&", target: t });
				continue;
			}
			word = (word || "") + c; i++;
		}
		endCmd(";");
		return out;
	}

	// Runs one command: io.out(Buffer|string), io.err(string), done(code).
	function runCommand(argv, io, done) {
		var name = String(argv[0] || "").replace(/^.*\//, "");
		var f = commands[name];
		if (!f) { io.err("sh: " + argv[0] + ": not found\n"); hostLog("warn", "child_process: " + argv[0] + " isn't available on W10M"); return setImmediate(function () { done(127); }); }
		try { f(argv.slice(1), io, function (code) { done(code || 0); }); }
		catch (e) { io.err(name + ": " + (e && e.message || e) + "\n"); setImmediate(function () { done(1); }); }
	}

	// A command line with its redirections and && || ;.
	function runLine(line, io, done) {
		var list = parseLine(line), status = 0;
		(function next(k, skip) {
			if (k >= list.length) { return done(status); }
			var c = list[k];
			if (skip) { return next(k + 1, (c.op === "&&" && status !== 0) || (c.op === "||" && status === 0)); }
			var sub = { out: io.out, err: io.err };
			c.redirects.forEach(function (r) {
				var sink;
				if (r.dup) { sink = r.target === "1" ? io.out : io.err; }
				else if (r.target === "/dev/null") { sink = function () {}; }
				else {
					var p = r.target, first = !r.append;
					sink = function (d) { if (first) { fs.writeFileSync(p, d); first = false; } else { fs.appendFileSync(p, d); } };
					if (!r.append) { fs.writeFileSync(p, ""); first = false; }
				}
				if (r.fd === 2) { sub.err = sink; } else { sub.out = sink; }
			});
			runCommand(c.argv, sub, function (code) {
				status = code;
				next(k + 1, (c.op === "&&" && code !== 0) || (c.op === "||" && code === 0));
			});
		})(0, false);
	}

	function childFor(run) {
		var child = new EventEmitter();
		child.stdin = new PassThrough(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
		child.pid = 4000 + Math.floor(Math.random() * 1000); child.killed = false; child.exitCode = null;
		child.kill = function () { child.killed = true; };
		setImmediate(function () {
			run({ out: function (d) { if (!child.killed) { child.stdout.write(typeof d === "string" ? new Buffer(d) : d); } },
				err: function (d) { if (!child.killed) { child.stderr.write(new Buffer(String(d))); } } },
				function (code) {
					child.exitCode = child.killed ? null : code;
					child.stdout.end(); child.stderr.end();
					setImmediate(function () {
						child.emit("exit", child.killed ? null : code, child.killed ? "SIGTERM" : null);
						child.emit("close", child.killed ? null : code, child.killed ? "SIGTERM" : null);
					});
				});
		});
		return child;
	}

	// exec and execFile: the output gathered for the callback, a timeout ending it.
	function gathered(child, label, opts, cb) {
		if (!cb) { return child; }
		var out = [], err = [], timer = null, timedOut = false;
		child.stdout.on("data", function (d) { out.push(d); });
		child.stderr.on("data", function (d) { err.push(d); });
		if (opts && opts.timeout) { timer = setTimeout(function () { timedOut = true; child.kill(); }, opts.timeout); }
		child.on("close", function (code) {
			if (timer) { clearTimeout(timer); }
			var enc = opts && opts.encoding === "buffer";
			var o = Buffer.concat(out), e = Buffer.concat(err);
			var so = enc ? o : o.toString(), se = enc ? e : e.toString();
			if (code === 0 && !timedOut) { return cb(null, so, se); }
			var x = new Error("Command failed: " + label + "\n" + e.toString());
			x.code = timedOut ? null : code; x.killed = timedOut; x.signal = timedOut ? "SIGTERM" : null; x.cmd = label;
			cb(x, so, se);
		});
		return child;
	}

	var child_process = {
		spawn: function (file, args, opts) {
			var argv = [file].concat(Array.isArray(args) ? args : []);
			if (opts && opts.shell && !Array.isArray(args)) { return childFor(function (io, done) { runLine(file, io, done); }); }
			return childFor(function (io, done) { runCommand(argv, io, done); });
		},
		execFile: function (file, args, opts, cb) {
			cb = [args, opts, cb].filter(function (x) { return typeof x === "function"; })[0];
			var a = Array.isArray(args) ? args : [];
			var o = [args, opts].filter(function (x) { return x && typeof x === "object" && !Array.isArray(x); })[0] || {};
			return gathered(childFor(function (io, done) { runCommand([file].concat(a), io, done); }), [file].concat(a).join(" "), o, cb);
		},
		exec: function (cmd, opts, cb) {
			if (typeof opts === "function") { cb = opts; opts = {}; }
			return gathered(childFor(function (io, done) { runLine(cmd, io, done); }), cmd, opts || {}, cb);
		},
		// The file commands run at once; curl, which waits on the network, can't answer synchronously.
		spawnSync: function (file, args) {
			var r = syncRun([file].concat(Array.isArray(args) ? args : []));
			return { pid: 0, status: r.code, signal: null, stdout: r.out, stderr: r.err, output: [null, r.out, r.err] };
		},
		execSync: function (cmd, opts) {
			var list = parseLine(cmd), r = { code: 0, out: new Buffer(0), err: new Buffer(0) };
			for (var k = 0; k < list.length; k++) { r = syncRun(list[k].argv); if (r.code !== 0 && list[k].op !== ";") { break; } }
			if (r.code !== 0) { var e = new Error("Command failed: " + cmd + "\n" + r.err.toString()); e.status = r.code; e.stdout = r.out; e.stderr = r.err; throw e; }
			return opts && opts.encoding && opts.encoding !== "buffer" ? r.out.toString() : r.out;
		},
		execFileSync: function (file, args, opts) {
			var r = syncRun([file].concat(Array.isArray(args) ? args : []));
			if (r.code !== 0) { var e = new Error("Command failed: " + file + "\n" + r.err.toString()); e.status = r.code; e.stdout = r.out; e.stderr = r.err; throw e; }
			return opts && opts.encoding && opts.encoding !== "buffer" ? r.out.toString() : r.out;
		}
	};

	function syncRun(argv) {
		var name = String(argv[0] || "").replace(/^.*\//, ""), out = [], err = [], code = null;
		if (name === "curl" || !commands[name]) {
			return { code: 127, out: new Buffer(0), err: new Buffer("sh: " + argv[0] + ": " + (name === "curl" ? "can't run synchronously on W10M" : "not found") + "\n") };
		}
		commands[name](argv.slice(1), { out: function (d) { out.push(new Buffer(d)); }, err: function (d) { err.push(new Buffer(String(d))); } }, function (c) { code = c || 0; }, true);
		return { code: code === null ? 0 : code, out: Buffer.concat(out), err: Buffer.concat(err) };
	}

	// ---- the file commands ----

	function flags(args, known) {
		var f = {}, rest = [];
		args.forEach(function (a) {
			if (/^-[A-Za-z]+$/.test(a) && rest.length === 0 || /^-[A-Za-z]+$/.test(a)) { a.slice(1).split("").forEach(function (c) { f[c] = true; }); }
			else if (a === "--") { return; }
			else { rest.push(a); }
		});
		return { f: f, args: rest };
	}
	function isDir(p) { try { return fs.statSync(p).isDirectory(); } catch (e) { return false; } }
	function exists(p) { try { fs.statSync(p); return true; } catch (e) { return false; } }
	function removeTree(p) {
		if (isDir(p)) { fs.readdirSync(p).forEach(function (n) { removeTree(p + "/" + n); }); fs.rmdirSync(p); }
		else { fs.unlinkSync(p); }
	}
	function copyTree(a, b) {
		if (isDir(a)) {
			if (!exists(b)) { fs.mkdirSync(b); }
			fs.readdirSync(a).forEach(function (n) { copyTree(a + "/" + n, b + "/" + n); });
		} else { fs.writeFileSync(b, fs.readFileSync(a)); }
	}
	function finish(sync, done, code) { if (sync) { done(code); } else { setImmediate(function () { done(code); }); } }

	commands.rm = function (args, io, done, sync) {
		var o = flags(args), code = 0;
		o.args.forEach(function (p) {
			if (!exists(p)) { if (!o.f.f) { io.err("rm: can't remove '" + p + "': No such file or directory\n"); code = 1; } return; }
			if (isDir(p) && !o.f.r && !o.f.R) { io.err("rm: '" + p + "' is a directory\n"); code = 1; return; }
			try { removeTree(p); } catch (e) { io.err("rm: can't remove '" + p + "': " + e.message + "\n"); code = 1; }
		});
		finish(sync, done, code);
	};
	commands.rmdir = function (args, io, done, sync) {
		var code = 0;
		flags(args).args.forEach(function (p) { try { fs.rmdirSync(p); } catch (e) { io.err("rmdir: '" + p + "': " + e.message + "\n"); code = 1; } });
		finish(sync, done, code);
	};
	commands.mkdir = function (args, io, done, sync) {
		var o = flags(args), code = 0;
		o.args.forEach(function (p) {
			try { if (o.f.p) { if (!isDir(p)) { fs.mkdirSync(p, { recursive: true }); } } else { fs.mkdirSync(p); } }
			catch (e) { io.err("mkdir: can't create directory '" + p + "': " + e.message + "\n"); code = 1; }
		});
		finish(sync, done, code);
	};
	commands.mv = function (args, io, done, sync) {
		var o = flags(args), list = o.args, code = 0;
		if (list.length < 2) { io.err("mv: missing operand\n"); return finish(sync, done, 1); }
		var dest = list.pop();
		list.forEach(function (src) {
			var to = isDir(dest) ? dest + "/" + src.replace(/^.*\//, "") : dest;
			try { if (exists(to) && !isDir(to)) { fs.unlinkSync(to); } fs.renameSync(src, to); }
			catch (e) {
				try { copyTree(src, to); removeTree(src); }
				catch (e2) { io.err("mv: can't rename '" + src + "': " + e2.message + "\n"); code = 1; }
			}
		});
		finish(sync, done, code);
	};
	commands.cp = function (args, io, done, sync) {
		var o = flags(args), list = o.args, code = 0;
		if (list.length < 2) { io.err("cp: missing operand\n"); return finish(sync, done, 1); }
		var dest = list.pop();
		list.forEach(function (src) {
			var to = isDir(dest) ? dest + "/" + src.replace(/^.*\//, "") : dest;
			if (isDir(src) && !o.f.r && !o.f.R && !o.f.a) { io.err("cp: omitting directory '" + src + "'\n"); code = 1; return; }
			try { copyTree(src, to); } catch (e) { io.err("cp: can't copy '" + src + "': " + e.message + "\n"); code = 1; }
		});
		finish(sync, done, code);
	};
	commands.touch = function (args, io, done, sync) {
		flags(args).args.forEach(function (p) { if (!exists(p)) { fs.writeFileSync(p, ""); } });
		finish(sync, done, 0);
	};
	commands.cat = function (args, io, done, sync) {
		var code = 0;
		flags(args).args.forEach(function (p) { try { io.out(fs.readFileSync(p)); } catch (e) { io.err("cat: can't open '" + p + "': No such file or directory\n"); code = 1; } });
		finish(sync, done, code);
	};
	commands.echo = function (args, io, done, sync) {
		var n = args[0] === "-n"; io.out((n ? args.slice(1) : args).join(" ") + (n ? "" : "\n"));
		finish(sync, done, 0);
	};
	commands["true"] = function (args, io, done, sync) { finish(sync, done, 0); };
	commands["false"] = function (args, io, done, sync) { finish(sync, done, 1); };
	commands.sync = commands["true"];
	// ionice -c3 <command...>: the command, at whatever priority the frame has.
	commands.ionice = function (args, io, done, sync) {
		var i = 0;
		while (i < args.length && /^-/.test(args[i])) { i += /^-[cnp]$/.test(args[i]) ? 2 : 1; }
		if (sync) { var r = syncRun(args.slice(i)); io.out(r.out); io.err(r.err.toString()); return done(r.code); }
		runCommand(args.slice(i), io, done);
	};
	commands.nice = function (args, io, done, sync) {
		var i = 0;
		while (i < args.length && /^-/.test(args[i])) { i += args[i] === "-n" ? 2 : 1; }
		if (sync) { var r = syncRun(args.slice(i)); io.out(r.out); io.err(r.err.toString()); return done(r.code); }
		runCommand(args.slice(i), io, done);
	};
	// upstart's and the crash reporter's: told, and nothing on W10M listens.
	commands.initctl = function (args, io, done, sync) { hostLog("info", "initctl " + args.join(" ")); finish(sync, done, 0); };
	commands.rdx_reporter = function (args, io, done, sync) { hostLog("info", "rdx_reporter " + args.join(" ")); finish(sync, done, 0); };
	commands.sh = function (args, io, done, sync) {
		if (args[0] === "-c" && args.length > 1) {
			if (sync) { var list = parseLine(args[1]), r = { code: 0, out: new Buffer(0), err: new Buffer(0) }; for (var k = 0; k < list.length; k++) { r = syncRun(list[k].argv); io.out(r.out); io.err(r.err.toString()); } return done(r.code); }
			return runLine(args[1], io, done);
		}
		io.err("sh: only sh -c runs on W10M\n");
		finish(sync, done, 127);
	};

	// ---- curl (AndroidLuna's curl.js) ----

	commands.curl = function (argv, io, done) {
		var E = { UNKNOWN_OPTION: 2, URL_MALFORMAT: 3, RESOLVE: 6, CONNECT: 7, HTTP: 22, WRITE: 23, READ: 26, TIMEOUT: 28, TOO_MANY_REDIRECTS: 47, RECV: 56 };
		var opt = { silent: false, showError: false, location: false, fail: false, maxTime: 0, connectTimeout: 0, headers: [], method: null, data: null,
			head: false, include: false, dumpHeader: null, writeOut: null, userAgent: "curl/7.88.1", insecure: false, referer: null, user: null, maxRedirs: 50,
			createDirs: false, jobs: [], get: false };
		var pendingOutput = [], finished = false, maxTimer = null;
		function end(code) {
			if (finished) { return; }
			finished = true;
			if (maxTimer) { clearTimeout(maxTimer); }
			done(code);
		}
		function die(code, text) { if (!opt.silent || opt.showError) { io.err("curl: (" + code + ") " + text + "\n"); } end(code); }
		var TAKES = { "max-time": 1, "connect-timeout": 1, header: 1, request: 1, data: 1, "data-binary": 1, "data-raw": 1, "data-ascii": 1, "data-urlencode": 1,
			"dump-header": 1, output: 1, "write-out": 1, "user-agent": 1, config: 1, referer: 1, user: 1, "max-redirs": 1, retry: 1, "retry-delay": 1,
			"retry-max-time": 1, noproxy: 1, cacert: 1, capath: 1, cert: 1, key: 1, "limit-rate": 1, url: 1 };
		var SHORT = { m: "max-time", H: "header", X: "request", d: "data", D: "dump-header", o: "output", w: "write-out", A: "user-agent", K: "config",
			e: "referer", u: "user", s: "silent", S: "show-error", L: "location", f: "fail", I: "head", k: "insecure", v: "verbose", i: "include",
			G: "get", "#": "progress-bar" };
		var FLAGS = { silent: 1, "show-error": 1, location: 1, fail: 1, head: 1, insecure: 1, compressed: 1, verbose: 1, "no-buffer": 1, globoff: 1,
			"http1.1": 1, "no-keepalive": 1, "tr-encoding": 1, "no-progress-meter": 1, "progress-bar": 1, include: 1, "create-dirs": 1, get: 1,
			"fail-with-body": 1, "no-sessionid": 1, "tlsv1.2": 1, "tlsv1.3": 1, sslv3: 1, "ssl-reqd": 1 };
		var bad = null;
		function apply(name, value) {
			switch (name) {
				case "silent": opt.silent = true; break;
				case "show-error": opt.showError = true; break;
				case "location": opt.location = true; break;
				case "fail": case "fail-with-body": opt.fail = true; break;
				case "head": opt.head = true; break;
				case "include": opt.include = true; break;
				case "insecure": opt.insecure = true; break;
				case "create-dirs": opt.createDirs = true; break;
				case "get": opt.get = true; break;
				// Windows checks certificates against its own store; a CA file has nothing to add to it.
				case "cacert": case "capath": case "cert": case "key": case "limit-rate": break;
				case "compressed": case "verbose": case "no-buffer": case "globoff": case "http1.1": case "no-keepalive": case "tr-encoding":
				case "no-progress-meter": case "progress-bar": case "retry": case "retry-delay": case "retry-max-time": case "noproxy":
				case "no-sessionid": case "tlsv1.2": case "tlsv1.3": case "sslv3": case "ssl-reqd": break;
				case "max-time": opt.maxTime = parseFloat(value) * 1000; break;
				case "connect-timeout": opt.connectTimeout = parseFloat(value) * 1000; break;
				case "max-redirs": opt.maxRedirs = parseInt(value, 10); break;
				case "header": opt.headers.push(value); break;
				case "request": opt.method = value; break;
				case "data": case "data-ascii": case "data-binary": case "data-raw": case "data-urlencode":
					if (name === "data-urlencode") { var eq = value.indexOf("="); value = eq < 0 ? encodeURIComponent(value) : value.slice(0, eq + 1) + encodeURIComponent(value.slice(eq + 1)); }
					else if (name !== "data-raw" && value.charAt(0) === "@") { value = fs.readFileSync(value.slice(1)); }
					opt.data = opt.data === null ? value : Buffer.concat([new Buffer(opt.data), new Buffer("&"), new Buffer(value)]);
					break;
				case "dump-header": opt.dumpHeader = value; break;
				case "output": pendingOutput.push(value); break;
				case "write-out": opt.writeOut = value; break;
				case "user-agent": opt.userAgent = value; break;
				case "referer": opt.referer = value; break;
				case "user": opt.user = value; break;
				case "config": readConfig(value); break;
				case "url": opt.jobs.push({ url: value, output: null }); break;
				default: bad = bad || "option --" + name + ": is unknown";
			}
		}
		function readConfig(file) {
			var text = fs.readFileSync(file, "utf8");
			text.split(/\r?\n/).forEach(function (line) {
				line = line.trim();
				if (!line || line.charAt(0) === "#") { return; }
				var m = /^-{0,2}([A-Za-z0-9.-]+)\s*(?:[=:]\s*|\s+)?(.*)$/.exec(line);
				if (!m) { return; }
				var name = m[1], value = m[2];
				if (name.length === 1 && SHORT[name]) { name = SHORT[name]; }
				if (value.charAt(0) === "\"") { value = JSON.parse(value.replace(/\\(?!["\\\/bfnrtu])/g, "\\\\")); }
				apply(name, FLAGS[name] ? null : value);
			});
		}
		for (var i = 0; i < argv.length && !bad; i++) {
			var a = argv[i];
			if (a.indexOf("--") === 0) {
				var nm = a.slice(2);
				if (TAKES[nm]) { apply(nm, argv[++i]); } else if (FLAGS[nm]) { apply(nm); } else { bad = "option " + a + ": is unknown"; }
			} else if (a.charAt(0) === "-" && a.length > 1) {
				for (var j = 1; j < a.length; j++) {
					var sn = SHORT[a.charAt(j)];
					if (!sn) { bad = "option -" + a.charAt(j) + ": is unknown"; break; }
					if (TAKES[sn]) { apply(sn, j + 1 < a.length ? a.slice(j + 1) : argv[++i]); break; }
					apply(sn);
				}
			} else { apply("url", a); }
		}
		if (bad) { return setImmediate(function () { die(E.UNKNOWN_OPTION, bad); }); }
		opt.jobs.forEach(function (job, k) { job.output = pendingOutput[k] || null; });
		if (!opt.jobs.length) { return setImmediate(function () { die(E.URL_MALFORMAT, "no URL specified"); }); }
		if (opt.get && opt.data !== null) {
			opt.jobs.forEach(function (job) { job.url += (job.url.indexOf("?") < 0 ? "?" : "&") + opt.data.toString(); });
			opt.data = null;
		}
		if (opt.dumpHeader && opt.dumpHeader !== "-") { try { fs.writeFileSync(opt.dumpHeader, ""); } catch (e) { return setImmediate(function () { die(E.WRITE, "Failed writing header"); }); } }
		if (opt.maxTime) { maxTimer = setTimeout(function () { die(E.TIMEOUT, "Operation timed out after " + opt.maxTime + " milliseconds"); }, opt.maxTime); }

		function rawHeaders(res) {
			var s = "HTTP/" + res.httpVersion + " " + res.statusCode + " " + res.statusMessage + "\r\n";
			for (var k = 0; k < res.rawHeaders.length; k += 2) { s += res.rawHeaders[k] + ": " + res.rawHeaders[k + 1] + "\r\n"; }
			return s + "\r\n";
		}
		function dumpHeaders(res) { if (opt.dumpHeader === "-") { io.out(rawHeaders(res)); } else { fs.appendFileSync(opt.dumpHeader, rawHeaders(res)); } }
		function pad3(n) { n = String(n || 0); while (n.length < 3) { n = "0" + n; } return n; }
		function writeOut(fmt, r) {
			return fmt.replace(/%\{([a-z_]+)\}|\\n|\\r|\\t|%%/g, function (m, v) {
				if (m === "\\n") { return "\n"; } if (m === "\\r") { return "\r"; } if (m === "\\t") { return "\t"; } if (m === "%%") { return "%"; }
				switch (v) {
					case "http_code": case "response_code": return pad3(r.status);
					case "url_effective": return r.url;
					case "size_download": return String(r.size || 0);
					case "content_type": return r.contentType || "";
					case "num_redirects": return String(r.redirects || 0);
					case "redirect_url": return r.redirectUrl || "";
					case "time_total": return ((Date.now() - r.start) / 1000).toFixed(6);
					default: return "";
				}
			});
		}
		function fetchOnce(job, target, method, body, redirects, start, fin) {
			var answered = false;
			function ok(code, text, result) { if (!answered) { answered = true; fin(code, text, result); } }
			var u = url.parse(target);
			if (!u.protocol || !u.host) { return ok(E.URL_MALFORMAT, "URL rejected: Malformed input to a URL function"); }
			var mod = u.protocol === "https:" ? https : u.protocol === "http:" ? http : null;
			if (!mod) { return ok(1, "Protocol \"" + u.protocol.replace(":", "") + "\" not supported"); }
			var headers = {};
			function setHeader(name, value) {
				Object.keys(headers).forEach(function (k) { if (k.toLowerCase() === name.toLowerCase()) { delete headers[k]; } });
				if (value !== null) { headers[name] = value; }
			}
			setHeader("User-Agent", opt.userAgent);
			setHeader("Accept", "*/*");
			if (opt.referer) { setHeader("Referer", opt.referer); }
			var fromUrl = null;
			if (u.auth) { try { fromUrl = decodeURIComponent(u.auth); } catch (e) { fromUrl = u.auth; } }
			var user = opt.user || fromUrl;
			if (user) { setHeader("Authorization", "Basic " + new Buffer(user).toString("base64")); }
			if (body !== null) { setHeader("Content-Type", "application/x-www-form-urlencoded"); }
			opt.headers.forEach(function (h) {
				var k = h.indexOf(":");
				if (k > 0) { var v = h.slice(k + 1).trim(); setHeader(h.slice(0, k).trim(), v || null); }
			});
			var req = mod.request({ protocol: u.protocol, hostname: u.hostname, port: u.port, path: u.path, method: method, headers: headers }, function (res) {
				var loc = res.headers.location;
				if (opt.location && loc && [301, 302, 303, 307, 308].indexOf(res.statusCode) >= 0) {
					res.resume();
					if (redirects >= opt.maxRedirs) { return ok(E.TOO_MANY_REDIRECTS, "Maximum (" + opt.maxRedirs + ") redirects followed"); }
					var nextUrl = url.resolve(target, loc);
					var keep = res.statusCode === 307 || res.statusCode === 308 || opt.method;
					if (opt.dumpHeader) { dumpHeaders(res); }
					return fetchOnce(job, nextUrl, keep ? method : (method === "POST" ? "GET" : method), keep ? body : null, redirects + 1, start, ok);
				}
				if (opt.dumpHeader) { dumpHeaders(res); }
				var result = { status: res.statusCode, url: target, contentType: res.headers["content-type"], redirects: redirects, start: start, size: 0,
					redirectUrl: loc ? url.resolve(target, loc) : "" };
				if (opt.fail && res.statusCode >= 400) { res.resume(); return ok(E.HTTP, "The requested URL returned error: " + res.statusCode, result); }
				var chunks = [];
				if (opt.include && !opt.head) { chunks.push(new Buffer(rawHeaders(res))); }
				res.on("data", function (d) { result.size += d.length; if (!opt.head) { chunks.push(d); } });
				res.on("error", function () { ok(E.RECV, "Failure when receiving data from the peer", result); });
				res.on("end", function () {
					if (opt.head && !opt.dumpHeader) { chunks = [new Buffer(rawHeaders(res))]; }
					var data = Buffer.concat(chunks);
					if (job.output && job.output !== "-") {
						try {
							if (opt.createDirs) { var dir = job.output.replace(/\/[^\/]*$/, ""); if (dir && !isDir(dir)) { fs.mkdirSync(dir, { recursive: true }); } }
							fs.writeFileSync(job.output, data);
						} catch (e) { return ok(E.WRITE, "Failure writing output to destination", result); }
					} else { io.out(data); }
					ok(0, null, result);
				});
			});
			req.on("error", function (e) {
				var code = e.code === "ETIMEDOUT" ? E.TIMEOUT : e.code === "ENOTFOUND" ? E.RESOLVE : E.CONNECT;
				ok(code, code === E.TIMEOUT ? "Connection timed out" : code === E.RESOLVE ? "Could not resolve host: " + u.hostname
					: "Failed to connect to " + u.hostname + " port " + (u.port || (u.protocol === "https:" ? 443 : 80)));
			});
			if (opt.connectTimeout) { req.setTimeout(opt.connectTimeout); }
			if (body !== null) { req.write(body); }
			req.end();
		}
		var status = 0;
		(function next(k) {
			if (finished) { return; }
			if (k >= opt.jobs.length) { return end(status); }
			var job = opt.jobs[k];
			var method = opt.method || (opt.head ? "HEAD" : opt.data !== null ? "POST" : "GET");
			fetchOnce(job, job.url, method, opt.data, 0, Date.now(), function (code, text, result) {
				if (opt.writeOut && result) { io.out(writeOut(opt.writeOut, result)); }
				if (code) { status = code; if (!opt.silent || opt.showError) { io.err("curl: (" + code + ") " + text + "\n"); } }
				next(k + 1);
			});
		})(0);
	};

	// ---- vm: a context is a frame of its own ----

	// A strict file's top-level declarations stay in its eval; webOS's sources declared their
	// globals at the top of files node ran as scripts, so the directive is put out of the way.
	function sloppy(code) {
		return code.replace(/^﻿?#![^\n]*/, "").replace(/^((?:\s|\/\/[^\n]*\n|\/\*[\s\S]*?\*\/)*)(["'])use strict\2/, "$1$2not strict$2");
	}
	function runIn(win, code, filename) {
		return win.eval(sloppy(String(code)) + "\n//# sourceURL=" + (filename || "evalmachine.<anonymous>").replace(/\s/g, "%20"));
	}
	var contexts = [];
	var vm = {
		runInThisContext: function (code, o) { return runIn(G, code, typeof o === "string" ? o : o && o.filename); },
		createContext: function (sandbox) {
			var f = document.createElement("iframe");
			f.style.display = "none";
			document.body.appendChild(f);
			var w = f.contentWindow;
			contexts.push(f);
			if (sandbox) { Object.keys(sandbox).forEach(function (k) { w[k] = sandbox[k]; }); }
			w.global = w;
			w.__lunacyContext = true;
			return w;
		},
		isContext: function (c) { return !!(c && c.__lunacyContext); },
		runInContext: function (code, ctx, o) { return runIn(ctx, code, typeof o === "string" ? o : o && o.filename); },
		runInNewContext: function (code, sandbox, o) {
			var ctx = vm.createContext(sandbox);
			var r = vm.runInContext(code, ctx, o);
			if (sandbox) { Object.keys(ctx).forEach(function (k) { if (k !== "global" && k !== "__lunacyContext") { try { sandbox[k] = ctx[k]; } catch (e) {} } }); }
			return r;
		},
		Script: function (code, o) {
			this.runInThisContext = function () { return vm.runInThisContext(code, o); };
			this.runInContext = function (ctx) { return vm.runInContext(code, ctx, o); };
			this.runInNewContext = function (s) { return vm.runInNewContext(code, s, o); };
		}
	};
	vm.createScript = function (code, o) { return new vm.Script(code, o); };

	// ---- the rest of node's core ----

	function StringDecoder(enc) { this.enc = normEnc(enc); this.held = new Buffer(0); }
	StringDecoder.prototype.write = function (b) {
		if (typeof b === "string") { return b; }
		if (this.enc !== "utf8") { return new Buffer(b).toString(this.enc); }
		var all = this.held.length ? Buffer.concat([this.held, b]) : new Buffer(b), cut = all.length;
		// A sequence split across chunks waits for its end.
		for (var i = Math.max(0, all.length - 3); i < all.length; i++) {
			var c = all[i], need = c >= 0xF0 ? 4 : c >= 0xE0 ? 3 : c >= 0xC0 ? 2 : 0;
			if (need && i + need > all.length) { cut = i; break; }
		}
		this.held = all.slice(cut);
		return all.toString("utf8", 0, cut);
	};
	StringDecoder.prototype.end = function (b) { var s = b ? this.write(b) : ""; var h = this.held; this.held = new Buffer(0); return s + (h.length ? h.toString("utf8") : ""); };

	function AssertionError(o) { this.name = "AssertionError"; this.message = o.message; this.actual = o.actual; this.expected = o.expected; this.stack = new Error(o.message).stack; }
	AssertionError.prototype = Object.create(Error.prototype);
	function fail(a, e, msg, op) { throw new AssertionError({ message: msg || inspect(a) + " " + op + " " + inspect(e), actual: a, expected: e }); }
	function deepEq(a, b) {
		if (a === b) { return true; }
		if (a instanceof Date && b instanceof Date) { return a.getTime() === b.getTime(); }
		if (typeof a !== "object" || typeof b !== "object" || !a || !b) { return a == b; } // jshint ignore:line
		var ka = Object.keys(a), kb = Object.keys(b);
		if (ka.length !== kb.length) { return false; }
		return ka.every(function (k) { return deepEq(a[k], b[k]); });
	}
	function assert(v, msg) { if (!v) { fail(v, true, msg, "=="); } }
	assert.AssertionError = AssertionError;
	assert.ok = assert;
	assert.fail = fail;
	assert.equal = function (a, b, m) { if (a != b) { fail(a, b, m, "=="); } }; // jshint ignore:line
	assert.notEqual = function (a, b, m) { if (a == b) { fail(a, b, m, "!="); } }; // jshint ignore:line
	assert.strictEqual = function (a, b, m) { if (a !== b) { fail(a, b, m, "==="); } };
	assert.notStrictEqual = function (a, b, m) { if (a === b) { fail(a, b, m, "!=="); } };
	assert.deepEqual = assert.deepStrictEqual = function (a, b, m) { if (!deepEq(a, b)) { fail(a, b, m, "deepEqual"); } };
	assert.notDeepEqual = function (a, b, m) { if (deepEq(a, b)) { fail(a, b, m, "notDeepEqual"); } };
	assert.throws = function (f, x, m) { var threw = false; try { f(); } catch (e) { threw = true; } if (!threw) { fail(undefined, x, m || "Missing expected exception.", "throws"); } };
	assert.doesNotThrow = function (f) { f(); };
	assert.ifError = function (e) { if (e) { throw e; } };

	var os = {
		EOL: "\n",
		hostname: function () { return "webos-device"; },
		type: function () { return "Linux"; },
		platform: function () { return "linux"; },
		arch: function () { return "arm"; },
		release: function () { return "2.6.35-palm-tenderloin"; },
		tmpdir: function () { return "/tmp"; },
		tmpDir: function () { return "/tmp"; },
		homedir: function () { return "/home/root"; },
		endianness: function () { return "LE"; },
		uptime: function () { return Math.floor(performance.now() / 1000); },
		loadavg: function () { return [0, 0, 0]; },
		totalmem: function () { return 1024 * 1024 * 1024; },
		freemem: function () { return 256 * 1024 * 1024; },
		cpus: function () { return []; },
		networkInterfaces: function () { return {}; }
	};

	function unavailable(name) {
		return new Proxy({}, { get: function (t, k) {
			if (k === "then" || typeof k === "symbol") { return undefined; }
			return function () { throw new Error(name + "." + String(k) + " isn't available to webOS services on W10M"); };
		} });
	}

	var timers = { setTimeout: setTimeout.bind(G), clearTimeout: clearTimeout.bind(G), setInterval: setInterval.bind(G), clearInterval: clearInterval.bind(G),
		setImmediate: setImmediate.bind(G), clearImmediate: clearImmediate.bind(G) };

	var core = {
		events: EventEmitter, util: util, sys: util, buffer: { Buffer: Buffer, SlowBuffer: Buffer, kMaxLength: 0x3fffffff, INSPECT_MAX_BYTES: 50 },
		path: path, fs: fs, stream: Stream, url: url, querystring: querystring, http: http, https: https, crypto: crypto,
		child_process: child_process, vm: vm, string_decoder: { StringDecoder: StringDecoder }, assert: assert, os: os, timers: timers,
		constants: fs.constants,
		net: { isIP: function (s) { return /^\d+\.\d+\.\d+\.\d+$/.test(s) ? 4 : /:/.test(s) ? 6 : 0; }, isIPv4: function (s) { return /^\d+\.\d+\.\d+\.\d+$/.test(s); },
			isIPv6: function (s) { return /:/.test(s); }, createConnection: function () { throw new Error("net sockets aren't available to webOS services on W10M"); },
			connect: function () { throw new Error("net sockets aren't available to webOS services on W10M"); },
			createServer: function () { throw new Error("net servers aren't available to webOS services on W10M"); } },
		tls: unavailable("tls"), dgram: unavailable("dgram"), dns: unavailable("dns"), zlib: unavailable("zlib"), cluster: unavailable("cluster"),
		readline: unavailable("readline"), repl: unavailable("repl"), tty: { isatty: function () { return false; } }
	};

	// ---- modules ----

	var natives = {};
	var cache = {};
	var requirePaths = [];

	function statOf(p) { try { return fsCall("stat", { p: p }); } catch (e) { return null; } }
	function asFile(p) {
		var s = statOf(p);
		if (s && !s.dir) { return p; }
		var exts = [".js", ".json", ".node"];
		for (var i = 0; i < exts.length; i++) { s = statOf(p + exts[i]); if (s && !s.dir) { return p + exts[i]; } }
		return null;
	}
	function asDir(p) {
		var s = statOf(p);
		if (!s || !s.dir) { return null; }
		var pkg = statOf(p + "/package.json");
		if (pkg && !pkg.dir) {
			try {
				var main = JSON.parse(fsCall("read", { p: p + "/package.json", enc: "utf8" }).text).main;
				if (main) { var m = asFile(path.resolve(p, main)) || asIndex(path.resolve(p, main)); if (m) { return m; } }
			} catch (e) {}
		}
		return asIndex(p);
	}
	function asIndex(p) { return asFile(p + "/index"); }
	function resolveFrom(request, dir) {
		if (Object.prototype.hasOwnProperty.call(natives, request) || Object.prototype.hasOwnProperty.call(core, request)) { return request; }
		var f;
		if (/^(\/|\.\.?(\/|$))/.test(request)) {
			var p = path.resolve(dir, request);
			f = asFile(p) || asDir(p);
			if (f) { return f; }
		} else {
			// node_modules up from the module, then require.paths (node 0.4), from the working directory.
			var d = dir;
			while (true) {
				if (path.basename(d) !== "node_modules") {
					var nm = path.join(d, "node_modules", request);
					f = asFile(nm) || asDir(nm);
					if (f) { return f; }
				}
				if (d === "/") { break; }
				d = path.dirname(d);
			}
			for (var i = 0; i < requirePaths.length; i++) {
				var rp = path.resolve(String(requirePaths[i]), request);
				f = asFile(rp) || asDir(rp);
				if (f) { return f; }
			}
		}
		var e = new Error("Cannot find module '" + request + "'");
		e.code = "MODULE_NOT_FOUND";
		throw e;
	}

	function Module(id, parent) {
		this.id = id; this.filename = id; this.exports = {}; this.parent = parent || null;
		this.loaded = false; this.children = []; this.paths = [];
	}
	function makeRequire(mod) {
		var dir = mod ? path.dirname(mod.filename) : cwd;
		function req(request) {
			if (typeof request !== "string") { throw new TypeError("path must be a string"); }
			var id = resolveFrom(request, mod ? dir : cwd);
			if (Object.prototype.hasOwnProperty.call(natives, id)) { return typeof natives[id] === "function" ? (natives[id] = natives[id]()) : natives[id]; }
			if (Object.prototype.hasOwnProperty.call(core, id)) { return core[id]; }
			return load(id, mod);
		}
		req.resolve = function (r) { return resolveFrom(r, mod ? dir : cwd); };
		req.cache = cache;
		req.paths = requirePaths;
		req.main = mainModule;
		return req;
	}
	function load(file, parent) {
		if (cache[file]) { return cache[file].exports; }
		var m = new Module(file, parent);
		cache[file] = m;
		if (parent) { parent.children.push(m); }
		try {
			if (/\.json$/.test(file)) {
				m.exports = JSON.parse(fsCall("read", { p: file, enc: "utf8" }).text);
			} else if (/\.node$/.test(file)) {
				throw new Error("Lunacy: native addon " + file + " can't load on W10M");
			} else {
				var src = sloppy(fsCall("read", { p: file, enc: "utf8" }).text);
				var fn = (0, eval)("(function (exports, require, module, __filename, __dirname) {" + src +
					"\n})\n//# sourceURL=" + file.replace(/\s/g, "%20"));
				var r = makeRequire(m);
				fn.call(m.exports, m.exports, r, m, file, path.dirname(file));
			}
			m.loaded = true;
		} catch (e) {
			delete cache[file];
			throw e;
		}
		return m.exports;
	}
	var mainModule = null;

	G.global = G.GLOBAL = G.root = G;
	G.process = process;
	G.Buffer = Buffer;
	G.require = makeRequire(null);

	G.__lunacyNode = {
		core: core,
		natives: natives,
		util: util,
		EventEmitter: EventEmitter,
		sloppy: sloppy,
		runIn: runIn,
		requirePaths: requirePaths,
		hostLog: hostLog,
		// Runs a file as the main module, as node ran its script.
		runMain: function (file) {
			var abs1 = path.resolve(file);
			mainModule = new Module(".", null);
			mainModule.filename = abs1;
			cache[abs1] = mainModule;
			var src = sloppy(fsCall("read", { p: abs1, enc: "utf8" }).text);
			var fn = (0, eval)("(function (exports, require, module, __filename, __dirname) {" + src + "\n})\n//# sourceURL=" + abs1);
			var r = makeRequire(mainModule);
			r.main = mainModule;
			fn.call(mainModule.exports, mainModule.exports, r, mainModule, abs1, path.dirname(abs1));
			mainModule.loaded = true;
		}
	};
}());
