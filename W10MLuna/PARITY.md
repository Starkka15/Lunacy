# W10MLuna ↔ AndroidLuna parity

What AndroidLuna (with LunaCore) does that W10MLuna doesn't yet, from a scan of both trees
(2026-11-20): every bus method each side registers (constants resolved), the pages' native
interface (`LunacyNative`), the page server's routes, the JS services' Node runtime, and every
Android source file without a W10M counterpart. Ticked when ported and seen working on the
phone. The shared page scripts (`AndroidLuna/src/main/assets/lunacy/*.js`) and the ROM's JS
services are the same on both, so they aren't listed.

## Bus core
- [ ] The private bus (Bus.kt): a call's `privateBus` flag; JS services' calls and `com.palm.*`
      callers on it (`privileged`); `registerPrivate` for the 18 methods only it reaches
      (applicationManager's, the device profile's, the installers', systemmanager's...);
      the JS services' `outside` is `!privateBus`. W10M had its own narrower rule, which
      refused palmprofile's `com.palm.service.accounts/listAccounts`, so First Use made no
      profile account.
- [ ] The bus log's redaction of passwords and tokens (Bus.redact).

## Services
- [ ] `com.palm.deviceprofile`: getDeviceId, getDeviceProfile (DeviceProfileService.kt).
- [ ] `com.palm.wifi`: getstatus, setstate (WifiService.kt).
- [ ] `com.palm.btmonitor`: radio state, notifications, radioon/radiooff (BtMonitorService.kt).
- [ ] `org.webosarchive.lunacy.oauth`: listProviders, signIn, redirected, token, forget (OAuth.kt) - Gmail and Outlook mail.
- [ ] Media index: `com.palm.media.image.file:1`, `video.file:1`, `image.album:1` from the files (MediaIndex.kt), `com.palm.filenotifyd/state`, `org.webosarchive.lunacy/media/find` and `media/search`.
- [ ] File index: `com.palm.media.misc.file:1` for Quick Office and Adobe Reader (FileIndex.kt).
- [ ] Music library: songs, albums, artists, genres and playlists for the music player (MusicLibrary.kt), from the Music library here; the audio kind holds only the ringtones until then.
- [ ] Photos' screennails at the screen's size, LUNACY_SCREEN_PX (PhotoCache.kt).
- [ ] systemmanager's Exchange security policies and device lock (SecurityPolicy.kt).
- [ ] `org.webosarchive.lunacy`: files/list (the file picker), launcher/listApps, launcher/setHidden, launcher/pinStatus, launcher/pin, system/setHostWallpaper, system/setNotificationIcons.

## Shell
- [ ] Hidden apps: Software Manager's hide, honoured by the launcher, dock and Just Type (HiddenApps.kt).
- [ ] The launcher as arranged: tabs, renamed and added, icon order, groups, the quick-launch bar (LauncherLayout.kt, LauncherPages.kt).
- [ ] Just Type's decisions as Palm's launcher made them (JustTypeSearch.kt).
- [ ] Pages' alert, confirm and prompt drawn as webOS drew them, in the card (PageDialog.kt).
- [ ] Certificate errors: Trust Always per host, else dialogSSLConfirm (SiteTrust.kt).
- [ ] Banner and notification sounds by LunaSysMgr's rules (NotificationSound.kt).
- [ ] `/var/log/messages` for palm-log (SysLog.kt).
- [ ] enyo.WebView's BrowserAdapter on a native view: the Browser, Email's message view (BrowserViews.kt, EmailDocument.kt, `webView*` in LunacyNative).
- [ ] webOS 3 portability: an app's metadata.json devices (Portability.kt).

## Native code
- [ ] PDK apps and plug-ins (PdkHost, PdkRuntime, PdkWindow, PdkGl, HybridPlugins): W10MLuna's own ELF loader, Megler read for ideas only.
- [ ] Native services: the TouchPad's C++ services from their D-Bus service files - mojomail-imap, mojomail-smtp, filecache (NativeServices.kt, ServiceProcess.kt). Needs the loader above. Email's IMAP and SMTP accounts depend on it.

## Developer tools
- [ ] novacomd's device side for palm-install, palm-launch, palm-log (Novacom.kt), over TCP since W10M has no adb.

## JS services' Node runtime
- [ ] `net` sockets, `tls`, `dns`, `zlib` (W10M's node.js answers "not available"); some App Museum apps' services use them.

## Not applicable on W10M
- Android's own notifications, players and settings panels (`android/*`), MediaFiles' content URIs,
  HostWallpaper's Android side, MediaServer and LocalNet (EdgeHTML fetches local http itself),
  CookieFlush (WinINet keeps cookies on disk), FixedViewport's Chromium versions.
