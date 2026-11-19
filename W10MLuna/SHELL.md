# W10MLuna: the full shell

The kit (README.md) runs one webOS app per UWP package. The goal is all of Lunacy on Windows 10
Mobile: one UWP app that is the Luna shell, with Palm's apps (`CoreApps/`), App Museum installs,
cards, dashboards and the services AndroidLuna has, under the same rules (CLAUDE.md).

AndroidLuna is about 34,000 lines of Kotlin: `card/` (21,000: windows, the bus, about forty
services, db8, packages) and `shell/` (13,000: card view, launcher, status bar, Just Type,
notifications). None of it runs here; the design and the decisions carry over, the code is
written again in C#, as iOSLuna's was in Swift.

## The one decision the platform forces: what a window is

On Android every window (a card, a dashboard, a popup alert) is its own WebView, and an app's
windows script each other: `window.open` returns the new window, Enyo's `openWindow` writes
`windowParams` into it, `enyo.application` and Mojo's stages are shared by reference. A UWP
WebView has no way to hand a page a window it opened (`NewWindowRequested` gives no opener),
and two WebViews share no JavaScript. So on W10M a window has to be a **frame in one WebView**.
Which arrangement works depends on what EdgeHTML 15254 allows; `Spikes/shell-frames` asks it:

| Test | Question | Needed by |
|---|---|---|
| T1 | Does a frame from another app's `ms-local-stream` origin reach the resolver? | A |
| T2 | Do frames get `window.LunaHost`? | A, B |
| T3 | Does a value set on a new frame's window before its page loads survive the load (the initial `about:blank` window reused)? | A, B, C |
| T4 | Do same-origin sibling frames script each other? | A, B |
| T6 | ...also under a parent of another origin? | A |
| T5 | Can a frame's `localStorage` be shadowed (per-app storage on one origin)? | B |

- **A. One WebView, an origin per app.** The shell page holds every app's windows as frames
  of that app's origin, positioned anywhere (card, dashboard strip, popup), so per-app
  storage stays as on Android. Needs T1, T2, T6; `window.open` would go through the bridge
  to the shell, which can't create a frame synchronously in a parent of another origin, so
  an app's windows are made by the app's own first frame and laid out by the shell.
- **B. One WebView, one origin.** As on webOS itself, where every app was `file://`: the
  shell and all apps share one origin, `window.open` makes a frame at once, and per-app
  storage comes from the compat layer (T5). WebSQL is per app already (native).
- **C. A WebView per app, its windows as frames inside it.** The shell is XAML; an app's
  dashboards can't leave its WebView, so they would be drawn into the shell's notification
  area by a `WebViewBrush` with taps sent back. The fallback if A and B fail.

## Order of work

1. **Spike** (above), read from the log; pick A, B or C.
2. **Shell core.** One UWP app (`LunaShell`) with the bundled frameworks and `CoreApps/`;
   an app registry (bundled + installed in LocalState); the launcher; cards: open, switch,
   close, the card view; `window.open` for cards, dashboards and popups; relaunch with
   params; the Back gesture to the focused card.
3. **Services**, in the order Palm's apps need them: ApplicationManager and ActivityManager,
   SystemService and preferences, db8 (on winsqlite3, as AndroidLuna's Db8.kt on SQLite),
   notifications and banners, audio, power and display, downloads, AppInstallService with
   `Ipk.kt`'s ipk reader (App Museum installs), location, media index; contacts and
   calendar over Windows' stores.
4. **Shell chrome**: status bar, system menu, notification area, Just Type, the Pre3's
   phone layout (Docs/phone.md).
5. **Mojo** and its apps, JS services (rootfs), then **PDK** on Megler's core (README).

Each step lands on the phone and is tried there before the next.
