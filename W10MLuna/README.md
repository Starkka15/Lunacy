# W10MLuna

**Lunacy on Windows 10 Mobile.** `LunaKit` is a UWP Windows Runtime Component that hosts a
webOS app in a WebView and gives it the contract it was written against, using Lunacy's own
compatibility layer and modernized Enyo, not new copies of them. `LunaHost` is the app around
it: one webOS app, bundled into one UWP package, shown as one full-screen card.

That is the first step. Windows 10 Mobile sideloads, so unlike iOS it can have what Lunacy is on
Android, a shell that installs and runs apps from the App Museum. The plan is the kit first,
proven on a phone, then the shell on top of it (see "Still to do").

Lunacy's rules apply ([CLAUDE.md](../CLAUDE.md)): the look and feel is the device's, an app is
never patched by the host, a fix goes into the shared layer or the frameworks, and the bus never
fakes a success.

## What is shared, and what is Windows' own

Shared with AndroidLuna and iOSLuna, used as they are (copied in by `assemble-assets.ps1`,
never edited):

- `AndroidLuna/src/main/assets/lunacy/`: `input.js`, `engine.js`, `media.js`, `layout.js`,
  `bridge.js`, `crypt.js`, `textedit.js`, `net.js`, `websql.js`, `fonts.css`, and the Prelude
  fonts.
- Enyo 1.0, the TouchPad's with Lunacy's patches, as
  [webos-sdk-redux](https://github.com/webOSArchive/webos-sdk-redux) ships it
  (`0.4.1/share/framework/enyo/1.0`).

Windows' own (`LunaKit/`), the native half of the `window.LunacyNative` contract:

| File | What it is | Android counterpart |
|---|---|---|
| `JS/w10m-native.js` | `window.LunacyNative` over the WinRT bridge | `AppWindow.kt`'s JavaScript interface |
| `Bridge.cs` | `window.LunaHost`, the `[AllowForWeb]` object the page calls | `AppWindow.kt`'s `@JavascriptInterface` methods |
| `LunaCard.cs` | The card and its window: the WebView, the native dispatch | `AppWindow.kt` |
| `AppServer.cs` | Serves the app, the frameworks and `/__lunacy/` on the app's origin | `AppServer.kt` |
| `Transforms.cs` | The serve-time HTML, JS and CSS transforms | `AppServer.kt` (`HtmlTransforms` etc.) |
| `NetShim.cs` | Cross-origin XHR, sent as the TouchPad sent it | `NetShim.kt`, `Http.kt` |
| `WebSql.cs`, `Sqlite.cs` | WebSQL on the SQLite Windows ships (`winsqlite3.dll`) | `WebSql.kt` |
| `Bus.cs`, `Services.cs` | The bus, and the services apps have needed | `Bus.kt` and the services |
| `DeviceProfile.cs` | Pre3 on a phone, TouchPad otherwise | `DeviceProfile.kt` |
| `Log.cs` | The log, with secrets blanked, in a file Device Portal can pull | `Bus.redact` |

Two small changes to the shared scripts make them work on an origin that isn't
`<appid>.media.cryptofs.apps`; both are no-ops on Android and iOS, where the host doesn't
provide the method:

- `bridge.js`: `PalmSystem.identifier` takes the app id from `LunacyNative.appId()` when there is one.
- `media.js`: an app's `file://` URL maps to `LunacyNative.appOrigin(id)` when there is one.

## Building a port

Needs Visual Studio 2022 with the **Universal Windows Platform development** workload, the
Windows SDKs 10.0.15063 and 10.0.16299, and the Windows Mobile Extension SDK 10.0.15063.

1. Lay out the app (the folder holding its `appinfo.json`):

   ```powershell
   .\assemble-assets.ps1 -App ..\CoreApps\com.palm.app.calculator
   ```

   This fills `LunaHost\webos\`, makes the tiles from the app's icon, writes
   `Package.appxmanifest` from the app's id, title, version and vendor, and makes a self-signed
   sideloading certificate the first time. Enyo comes from a webos-sdk-redux clone beside this
   repository, or `-Enyo <path>`.

2. Build for the phone:

   ```powershell
   msbuild W10MLuna.sln /restore /p:Configuration=Release /p:Platform=ARM /p:AppxPackageSigningEnabled=true
   ```

   The package and its framework dependencies land in `LunaHost\AppPackages\`.

3. Install it through the phone's Device Portal (Apps → Install app, with the dependencies),
   with the phone in developer mode.

The log is `LocalState\lunakit.log` in the app's data, reachable from Device Portal's file
explorer; the previous run's is `lunakit.1.log`.

## Where W10M differs, and why

Each of these is the platform's, not a choice; Lunacy's [luna-deltas](../Docs/luna-deltas.md)
is the Android list.

- **The origin is `ms-local-stream://<package>_<hex of appid>/`.** A WebView on W10M can't
  intercept http or https (`WebResourceRequested` came in Windows 10 1803, after W10M's last
  build), so pages are served by a stream resolver on the WebView's own scheme, one origin per
  app. The paths are the device's, as on Android. Lunacy's scripts get the app id and origins
  from the host (above) instead of reading them from the host name.
- **A synchronous bridge, as on Android.** An `[AllowForWeb]` WinRT object is called
  synchronously and answers at once, so `w10m-native.js` is a thin wrapper and `netResult` and
  `sqlResult` read the host's result map, as Android's do. No `prompt()` channel as on iOS.
- **The resolver answers with bytes only.** The WebView takes a file's type from its
  extension, a file that isn't there fails the request instead of answering 404, and there are
  no byte ranges. It can't name a charset either, so every page gets `<meta charset="utf-8">`
  first, as AndroidLuna answers every file as utf-8; without it EdgeHTML read Lunacy's scripts
  as windows-1252.
- **Phone Zoom** is AndroidLuna's fixed viewport (Docs/phone.md §4) with its defaults (Clock,
  Calculator, Memos, App Catalog at 640 px), on a phone only; there is no switch for it yet.
- **No document-start scripts.** `w10m-native.js` is the first script the server puts in every
  page's head, served with the values that don't change while the page lives.
- **No WebSQL in EdgeHTML at all**, so every database is `websql.js` over `WebSql.cs`, as on
  Android.
- **Cookies are shared.** `Windows.Web.Http` keeps cookies in the WebView's store, so native
  requests and the page's own share them, as on the TouchPad (iOSLuna can't).
- **Banners are toasts.** A phone shows a toast at the top of the screen; Luna's banners rose
  at the bottom. They are silent; a banner's sound isn't played yet.
- **The Back button is the back gesture.** It sends the ESC key webOS sent for the gesture,
  and never closes the app, as the gesture never closed a card. The app leaves through Start or
  the task switcher.
- **Media keys** come from the system media transport controls (the volume flyout, a headset,
  a car) and reach `com.palm.keys/media/status` as the keys a TouchPad's Bluetooth stack sent.

## Still to do

- **Measure everything above on the Lumia** (15254): EdgeHTML's rendering of Enyo against the
  TouchPad's, which thread a WebView calls the bridge on (logged at the first call), and
  whether `http://` images and media load into an `ms-local-stream` page or need a
  `/__lunacy/net` route, as iOS's media does.
- **`window.open`.** A UWP WebView can't hand a page a window it opened, so an app's
  dashboards, popup alerts and extra cards don't open yet. The general fix is a window that
  lives in the same WebView as a same-origin frame; it has to keep Lunacy's rule that a card is
  a window.
- `com.palm.keys/headset/status`: Windows has no plain "headset plugged in" event; it answers
  with the bus's honest error until one is found.
- A banner's sound, `playSound`, `statusBarColor`.
- **The shell**: card view, launcher, status bar and App Museum installs, on top of this kit.
- **PDK apps**, after the kit. They are ARM Linux binaries (glibc, softfp), and AndroidLuna
  runs them on Android's kernel ([pdk.md](../Docs/pdk.md)). W10M has no Linux kernel, but
  [Megler](https://github.com/Computershik73/megler-w10m) runs Android's ARM code inside a UWP
  app with its own ELF loader (rewriting thread-pointer reads and ARM PLT entries), a
  softfp-to-hard-float bridge and a Linux system-call layer over Win32. On that core the
  TouchPad's own glibc, SDL and PDL could load unchanged, with GL ES on ANGLE. Megler is
  GPL-3.0, so a W10MLuna build that uses it is GPL-3.0 (Lunacy itself is Apache-2.0).
