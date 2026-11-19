<#
.SYNOPSIS
Lays out Lunacy for Windows 10 Mobile in LunaHost\ (SHELL.md):
  webos\apps\<appid>\  Palm's apps and Lunacy's own (CoreApps\), exactly as they are, and any
                       app passed with -App
  webos\lunacy\        Lunacy's compatibility layer, shared with AndroidLuna as it is, and
                       LunaKit's w10m-native.js
  webos\shell\         the shell's page (LunaKit\Shell)
  webos\luna\          Luna's art and wallpapers, shared with AndroidLuna
  webos\fonts\         the Prelude family, which webOS had installed
  webos\fw\enyo\1.0\   the TouchPad's Enyo with Lunacy's patches, as webos-sdk-redux ships it
  Assets\*.png         tiles and splash screen, from Lunacy's logo
  Package.appxmanifest Lunacy's identity
Nothing is edited on the way: a fix to any of it belongs upstream (Lunacy's assets, or
LunaRuntimes/enyo-1.0/patches and the SDK).

.PARAMETER App
More webOS app folders (each holding appinfo.json) to bundle beside CoreApps.

.PARAMETER Enyo
Where Enyo 1.0 comes from; by default a webos-sdk-redux clone beside this repository.

.PARAMETER Version
The package's version.

.PARAMETER Apk
An AndroidLuna release APK, whose ROM (assets/rootfs) gives webos\rootfs\ its /usr/palm (Palm's
service frameworks and the system's JS services) and /etc (db8's system kinds, activities): what
fetch-assets.sh copies off the reference TouchPad, which isn't in git. The ROM's native
binaries wait for the PDK runtime (SHELL.md). By default $env:LUNACY_APK, else the 0.9.7 release
beside the Android checkout.
#>
param(
    [string[]]$App = @(),
    [string]$Enyo,
    [string]$Version = '0.2.0.0',
    [string]$Apk = $(if ($env:LUNACY_APK) { $env:LUNACY_APK } else { 'H:\Lunacy\AndroidLuna\local-build\release-0.9.7\AndroidLuna-arm64-release.apk' })
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $here
$assets = Join-Path $repo 'AndroidLuna\src\main\assets'
if (-not $Enyo) { $Enyo = Join-Path (Split-Path -Parent $repo) 'webos-sdk-redux\0.4.1\share\framework\enyo\1.0' }
if (-not (Test-Path (Join-Path $Enyo 'framework\build\enyo-build.js'))) { throw "assemble-assets: no Enyo at $Enyo (pass -Enyo)" }

$host_ = Join-Path $here 'LunaHost'
$dest = Join-Path $host_ 'webos'
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
New-Item -ItemType Directory -Force (Join-Path $dest 'lunacy'), (Join-Path $dest 'apps'), (Join-Path $dest 'luna') | Out-Null

# The page-side scripts only: services\ and the Mojo boot files are for later (SHELL.md).
foreach ($f in 'input.js', 'engine.js', 'media.js', 'layout.js', 'bridge.js', 'crypt.js', 'textedit.js', 'net.js', 'websql.js', 'fonts.css') {
    Copy-Item (Join-Path $assets "lunacy\$f") (Join-Path $dest "lunacy\$f")
}
Copy-Item (Join-Path $here 'LunaKit\JS\w10m-native.js') (Join-Path $dest 'lunacy\w10m-native.js')
Copy-Item -Recurse (Join-Path $here 'LunaKit\Shell') (Join-Path $dest 'shell')
foreach ($d in 'images', 'wallpapers') { Copy-Item -Recurse (Join-Path $assets "luna\$d") (Join-Path $dest "luna\$d") }
Copy-Item (Join-Path $assets 'luna\*.json') (Join-Path $dest 'luna')
Copy-Item -Recurse (Join-Path $assets 'luna\fonts') (Join-Path $dest 'fonts')
New-Item -ItemType Directory -Force (Join-Path $dest 'fw\enyo') | Out-Null
Copy-Item -Recurse $Enyo (Join-Path $dest 'fw\enyo\1.0')

# The ROM's /usr/palm and /etc, from the APK.
if (-not (Test-Path $Apk)) { throw "assemble-assets: no AndroidLuna APK at $Apk (pass -Apk)" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($Apk)
$romFiles = 0
try {
    foreach ($e in $zip.Entries) {
        if ($e.FullName.EndsWith('/')) { continue }
        # The ROM's /usr/palm and /etc; and the page frameworks with Lunacy's patches (mojoloader.js
        # and Palm's libraries, Mojo, mojocommon), served at /usr/palm/frameworks (AppServer.cs).
        if ($e.FullName -match '^assets/rootfs/((usr/palm|etc)/.+)$') { $out = Join-Path $dest ('rootfs\' + ($Matches[1] -replace '/', '\')) }
        elseif ($e.FullName -match '^assets/fw/((frameworks|mojo|mojocommon)/.+)$') { $out = Join-Path $dest ('fw\' + ($Matches[1] -replace '/', '\')) }
        else { continue }
        New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $out, $true)
        $romFiles++
    }
    # The framework trees' symlinks, which fetch-assets.sh records in fw.links ("link target",
    # both under fw/): version/1.0 is a link to submission/N, Mojo's art links into mojocommon,
    # and Enyo's build/g11n into source/. An appx can't hold a link either, so each becomes a
    # copy of what it points at; a few passes cover a link inside a linked folder.
    $linkEntry = $zip.GetEntry('assets/fw.links')
    $links = @()
    if ($linkEntry) {
        $r = New-Object IO.StreamReader($linkEntry.Open())
        try { while (($l = $r.ReadLine()) -ne $null) { $p = $l -split ' ', 2; if ($p.Count -eq 2) { $links += ,@($p[0], $p[1]) } } } finally { $r.Dispose() }
    }
    for ($pass = 0; $pass -lt 4; $pass++) {
        $made = 0
        foreach ($k in $links) {
            $from = Join-Path $dest ('fw\' + ($k[1] -replace '/', '\'))
            $to = Join-Path $dest ('fw\' + ($k[0] -replace '/', '\'))
            if ((Test-Path $to) -or -not (Test-Path $from)) { continue }
            New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
            Copy-Item -Recurse $from $to
            $made++
        }
        if ($made -eq 0) { break }
    }
} finally { $zip.Dispose() }

# The apps, each in a folder named by its id.
$folders = @(Get-ChildItem (Join-Path $repo 'CoreApps') -Directory | ForEach-Object FullName) + @($App | ForEach-Object { (Resolve-Path $_).Path })
$ids = @()
foreach ($f in $folders) {
    $infoFile = Join-Path $f 'appinfo.json'
    if (-not (Test-Path $infoFile)) { continue }
    $info = Get-Content -Raw -Encoding UTF8 $infoFile | ConvertFrom-Json
    if (-not $info.id) { Write-Warning "assemble-assets: $infoFile has no id"; continue }
    Copy-Item -Recurse $f (Join-Path $dest "apps\$($info.id)")
    $ids += $info.id
}

# The manifest.
function Escape-Xml([string]$s) { [Security.SecurityElement]::Escape($s) }
$publisher = 'CN=Lunacy W10M'
$manifest = (Get-Content -Raw -Encoding UTF8 (Join-Path $host_ 'Package.appxmanifest.in')) `
    -replace '@ID@', 'Lunacy' -replace '@VERSION@', $Version -replace '@PHONEID@', 'a5d0a1f8-6c3b-4f55-9a8e-1a0c6b4e2c17' `
    -replace '@PUBLISHER@', (Escape-Xml $publisher) -replace '@TITLE@', 'Lunacy' -replace '@VENDOR@', 'webOS Archive'
[IO.File]::WriteAllText((Join-Path $host_ 'Package.appxmanifest'), $manifest, (New-Object Text.UTF8Encoding $false))

# The signing certificate for sideloading, made once and kept beside the project (not committed).
$pfx = Join-Path $host_ 'LunaHost_TemporaryKey.pfx'
if (-not (Test-Path $pfx)) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'Lunacy W10M sideload' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString -String 'lunacy' -Force -AsPlainText) | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}

# Tiles: Lunacy's logo on black.
Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Image]::FromFile((Join-Path $repo 'AndroidLuna\src\main\res\drawable-xxhdpi\lunacy_logo.png'))
New-Item -ItemType Directory -Force (Join-Path $host_ 'Assets') | Out-Null
foreach ($t in @(@('Square150x150Logo', 150, 150), @('Square44x44Logo', 44, 44), @('StoreLogo', 50, 50),
                 @('Wide310x150Logo', 310, 150), @('SplashScreen', 620, 300))) {
    $bmp = New-Object Drawing.Bitmap $t[1], $t[2]
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([Drawing.Color]::Transparent)
    $s = [Math]::Min($t[1], $t[2]) * 0.75
    $w = $s * $icon.Width / [Math]::Max($icon.Width, $icon.Height); $h = $s * $icon.Height / [Math]::Max($icon.Width, $icon.Height)
    $g.DrawImage($icon, [int](($t[1] - $w) / 2), [int](($t[2] - $h) / 2), [int]$w, [int]$h)
    $g.Dispose()
    $bmp.Save((Join-Path $host_ "Assets\$($t[0]).png"), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
$icon.Dispose()
Write-Host "assemble-assets: Lunacy $Version with $($ids.Count) apps and $romFiles ROM files laid out in $host_"
