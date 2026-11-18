<#
  Installs a built port on a Windows 10 Mobile phone through its Device Portal, as an update
  when the package is already there (the app's data stays).

    .\deploy.ps1 -Pin 123456       # the first time: the code from the phone's
                                   # Settings > Update & security > For developers > Pair
    .\deploy.ps1                   # after that, with the saved pairing

  The pairing is kept in %LOCALAPPDATA%\W10MLuna\portal.cookies; a code works once, so when the
  portal stops accepting the saved one, pair again with a new code.
#>
param(
    [string]$Phone = $(if ($env:W10M_PHONE) { $env:W10M_PHONE } else { "192.168.5.47" }),
    [string]$Pin,
    [string]$Package,
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$base = "https://$Phone"
$jarDir = Join-Path $env:LOCALAPPDATA "W10MLuna"
New-Item -ItemType Directory -Force $jarDir | Out-Null
$jar = Join-Path $jarDir "portal.cookies"

if (-not $Package) {
    $suffix = if ($Configuration -eq "Debug") { "_ARM_Debug.appx" } else { "_ARM.appx" }
    $Package = Get-ChildItem (Join-Path $PSScriptRoot "LunaHost\AppPackages") -Recurse -Filter "*$suffix" |
        Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $Package) { throw "No built $Configuration ARM package under LunaHost\AppPackages." }
}

function Invoke-Portal([string[]]$CurlArgs) {
    $out = & curl.exe -sk --noproxy "*" -b $jar -c $jar -w "`n%{http_code}" @CurlArgs
    $lines = @($out)
    [pscustomobject]@{ Code = [int]$lines[-1]; Body = ($lines[0..($lines.Count - 2)] -join "`n") }
}
function Get-Csrf {
    $line = Get-Content $jar | Where-Object { $_ -match "`tCSRF-Token`t" } | Select-Object -Last 1
    if (-not $line) { throw "The portal gave no CSRF token." }
    ($line -split "`t")[-1]
}

Invoke-Portal @("$base/") | Out-Null   # the CSRF cookie
if ($Pin) {
    $r = Invoke-Portal @("-X", "POST", "-H", "Content-Length: 0", "-H", "X-CSRF-Token: $(Get-Csrf)",
        "$base/api/authorize/pair?pin=$Pin&persistent=0")
    if ($r.Code -ne 200) { throw "Pairing failed (HTTP $($r.Code)); get a new code from the phone." }
}
$r = Invoke-Portal @("$base/api/os/machinename")
if ($r.Code -ne 200) { throw "Not paired (HTTP $($r.Code)); run again with -Pin <code from the phone>." }
Write-Host "Phone: $($r.Body)"

# The package, then its ARM framework dependencies (the portal skips ones already installed).
$name = Split-Path $Package -Leaf
$form = @("-F", "$name=@$Package")
$deps = Join-Path (Split-Path $Package) "Dependencies\arm"
if (Test-Path $deps) {
    Get-ChildItem $deps -Filter *.appx | ForEach-Object { $form += @("-F", "$($_.Name)=@$($_.FullName)") }
}
Write-Host "Installing $name"
$r = Invoke-Portal (@("-X", "POST", "-H", "X-CSRF-Token: $(Get-Csrf)") + $form +
    @("$base/api/app/packagemanager/package?package=$name"))
if ($r.Code -ne 202 -and $r.Code -ne 200) { throw "Upload refused (HTTP $($r.Code)): $($r.Body)" }

do {
    Start-Sleep -Seconds 3
    $r = Invoke-Portal @("$base/api/app/packagemanager/state")
} while ($r.Code -eq 204)
if ($r.Body -match '"Success"\s*:\s*true') { Write-Host "Installed." }
else { throw "Install failed (HTTP $($r.Code)): $($r.Body)" }
