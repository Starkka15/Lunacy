<#
.SYNOPSIS
Lays out one webOS app as a W10MLuna port, in LunaHost\:
  webos\apps\<appid>\  the app, exactly as packaged
  webos\lunacy\        Lunacy's compatibility layer, shared with AndroidLuna as it is, and
                       LunaKit's w10m-native.js
  webos\fonts\         the Prelude family, which webOS had installed
  webos\fw\enyo\1.0\   the TouchPad's Enyo with Lunacy's patches, as webos-sdk-redux ships it
  webos\app-id.txt     which app this port is
  Assets\*.png         tiles and splash screen, from the app's own icon
  Package.appxmanifest the app's id, title, version and vendor, from its appinfo.json
Nothing is edited on the way: a fix to any of it belongs upstream (Lunacy's assets, or
LunaRuntimes/enyo-1.0/patches and the SDK). The sibling of iOSLuna's assemble-assets.sh.

.PARAMETER App
The webOS app's folder (the one holding appinfo.json).

.PARAMETER Enyo
Where Enyo 1.0 comes from; by default a webos-sdk-redux clone beside this repository.
#>
param(
    [Parameter(Mandatory = $true)][string]$App,
    [string]$Enyo
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $here
$assets = Join-Path $repo 'AndroidLuna\src\main\assets'
if (-not $Enyo) { $Enyo = Join-Path (Split-Path -Parent $repo) 'webos-sdk-redux\0.4.1\share\framework\enyo\1.0' }
if (-not (Test-Path (Join-Path $Enyo 'framework\build\enyo-build.js'))) { throw "assemble-assets: no Enyo at $Enyo (pass -Enyo)" }
$App = (Resolve-Path $App).Path
$info = Get-Content -Raw -Encoding UTF8 (Join-Path $App 'appinfo.json') | ConvertFrom-Json
$id = $info.id
if (-not $id) { throw "assemble-assets: $App\appinfo.json has no id" }

$host_ = Join-Path $here 'LunaHost'
$dest = Join-Path $host_ 'webos'
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
New-Item -ItemType Directory -Force (Join-Path $dest 'lunacy'), (Join-Path $dest 'apps') | Out-Null

# The page-side scripts only: services\ and the Mojo boot files are for things a port doesn't run.
foreach ($f in 'input.js', 'engine.js', 'media.js', 'layout.js', 'bridge.js', 'crypt.js', 'textedit.js', 'net.js', 'websql.js', 'fonts.css') {
    Copy-Item (Join-Path $assets "lunacy\$f") (Join-Path $dest "lunacy\$f")
}
Copy-Item (Join-Path $here 'LunaKit\JS\w10m-native.js') (Join-Path $dest 'lunacy\w10m-native.js')
Copy-Item -Recurse (Join-Path $assets 'luna\fonts') (Join-Path $dest 'fonts')
New-Item -ItemType Directory -Force (Join-Path $dest 'fw\enyo') | Out-Null
Copy-Item -Recurse $Enyo (Join-Path $dest 'fw\enyo\1.0')
Copy-Item -Recurse $App (Join-Path $dest "apps\$id")
[IO.File]::WriteAllText((Join-Path $dest 'app-id.txt'), $id)

# The manifest. A package name is letters, digits, dots and dashes, 3 to 50 of them.
function Escape-Xml([string]$s) { [Security.SecurityElement]::Escape($s) }
$name = ('Lunacy.' + ($id -replace '[^A-Za-z0-9.-]', '-'))
if ($name.Length -gt 50) { $name = $name.Substring(0, 50) }
$parts = @(([string]$info.version) -split '\.' | ForEach-Object { [int]($_ -replace '\D', '0') })
while ($parts.Count -lt 4) { $parts += 0 }
$version = ($parts[0..3] -join '.')
$md5 = [Security.Cryptography.MD5]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes("lunacy-w10m:$id"))
$phoneId = ([guid]::new($md5)).ToString()
$publisher = 'CN=Lunacy W10M'
$title = if ($info.title) { [string]$info.title } else { $id }
$vendor = if ($info.vendor) { [string]$info.vendor } else { 'webOS' }
$manifest = (Get-Content -Raw -Encoding UTF8 (Join-Path $host_ 'Package.appxmanifest.in')) `
    -replace '@ID@', $name -replace '@VERSION@', $version -replace '@PHONEID@', $phoneId `
    -replace '@PUBLISHER@', (Escape-Xml $publisher) -replace '@TITLE@', (Escape-Xml $title) -replace '@VENDOR@', (Escape-Xml $vendor)
[IO.File]::WriteAllText((Join-Path $host_ 'Package.appxmanifest'), $manifest, (New-Object Text.UTF8Encoding $false))

# The signing certificate for sideloading, made once and kept beside the project (not committed).
$pfx = Join-Path $host_ 'LunaHost_TemporaryKey.pfx'
if (-not (Test-Path $pfx)) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'Lunacy W10M sideload' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString -String 'lunacy' -Force -AsPlainText) | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}

# Tiles: the app's icon centred on black, as a webOS launcher showed it.
Add-Type -AssemblyName System.Drawing
$iconFile = Join-Path $App $(if ($info.icon) { $info.icon } else { 'icon.png' })
$icon = [Drawing.Image]::FromFile($iconFile)
New-Item -ItemType Directory -Force (Join-Path $host_ 'Assets') | Out-Null
foreach ($t in @(@('Square150x150Logo', 150, 150), @('Square44x44Logo', 44, 44), @('StoreLogo', 50, 50),
                 @('Wide310x150Logo', 310, 150), @('SplashScreen', 620, 300))) {
    $bmp = New-Object Drawing.Bitmap $t[1], $t[2]
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([Drawing.Color]::Transparent)
    $s = [int]([Math]::Min($t[1], $t[2]) * 0.75)
    $g.DrawImage($icon, [int](($t[1] - $s) / 2), [int](($t[2] - $s) / 2), $s, $s)
    $g.Dispose()
    $bmp.Save((Join-Path $host_ "Assets\$($t[0]).png"), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
$icon.Dispose()
Write-Host "assemble-assets: $id ($title $version) laid out in $host_"
