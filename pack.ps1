# Builds ClaudeDock, packs it into an unsigned MSIX with makeappx, and installs it for the current user.
# Works without admin rights, Developer Mode, or the Windows SDK.
param(
    [string]$Configuration = "Debug",
    [switch]$NoInstall
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "ClaudeDock"
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet-user\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

# makeappx/signtool (only needed without Developer Mode): from the Microsoft.Windows.SDK.BuildTools
# NuGet package, downloaded once into tools\ (git-ignored). No Windows SDK install needed.
function Get-MakeAppx {
    $toolsDir = Join-Path $root "tools\sdk-buildtools"
    $find = { Get-ChildItem (Join-Path $toolsDir "bin") -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -like "*\x64\*" | Select-Object -First 1 -ExpandProperty FullName }
    $exe = & $find
    if ($exe) { return $exe }
    Write-Host "== download Microsoft.Windows.SDK.BuildTools (makeappx, signtool)"
    $nupkg = Join-Path $env:TEMP "sdk-buildtools.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/10.0.26100.9457/microsoft.windows.sdk.buildtools.10.0.26100.9457.nupkg" -OutFile $nupkg -UseBasicParsing
    Expand-Archive $nupkg $toolsDir -Force
    Remove-Item $nupkg
    $exe = & $find
    if (-not $exe) { throw "makeappx.exe not found in the downloaded package" }
    return $exe
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$env:DOTNET_MULTILEVEL_LOOKUP = 0
if ($dotnet -ne "dotnet") { $env:DOTNET_ROOT = Split-Path $dotnet }

Write-Host "== build"
& $dotnet build (Join-Path $proj "ClaudeDock.csproj") -c $Configuration -p:Platform=x64 -v:q -nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$bin = Join-Path $proj "bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64"
$layout = Join-Path $root "out\layout"
$msix = Join-Path $root "out\ClaudeDock.msix"

# The running extension (started by Command Palette) locks files in the layout folder.
# Stop Command Palette with all its extensions, so none keeps a dead link to the old instance.
. (Join-Path $root "cmdpal.ps1")
$cmdPalWasRunning = $false
if (-not $NoInstall) {
    $cmdPalWasRunning = Stop-CommandPalette
}
for ($try = 1; (Test-Path $layout) -and $try -le 10; $try++) {
    try { Remove-Item $layout -Recurse -Force -ErrorAction Stop }
    catch {
        # Command Palette (or PowerToys) may relaunch the extension right away; stop it again.
        Get-Process Microsoft.CmdPal.UI, ClaudeDock -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 700
    }
}
if (Test-Path $layout) { throw "Could not clear $layout (files in use)" }
New-Item -ItemType Directory -Force $layout | Out-Null

Write-Host "== layout"
Copy-Item "$bin\*" $layout -Recurse -Force
# No PRI file, so give every logo a plain (unqualified) file name.
$assets = Join-Path $layout "Assets"
New-Item -ItemType Directory -Force $assets | Out-Null
Get-ChildItem (Join-Path $proj "Assets") -Filter *.png | ForEach-Object {
    $plain = ($_.Name -replace '\.scale-\d+', '' -replace '\.targetsize-[^.]+', '')
    Copy-Item $_.FullName (Join-Path $assets $plain) -Force
}
Copy-Item (Join-Path $proj "Assets\*.svg") $assets -Force
New-Item -ItemType Directory -Force (Join-Path $layout "Public") | Out-Null
Set-Content (Join-Path $layout "Public\readme.txt") "Claude Dock"

$manifest = Get-Content (Join-Path $proj "Package.appxmanifest") -Raw
$manifest = $manifest.Replace('$targetnametoken$', 'ClaudeDock').Replace('$targetentrypoint$', 'Windows.FullTrustApplication')
$manifest = $manifest.Replace('<Resource Language="x-generate"/>', '<Resource Language="en-us"/>')
Set-Content (Join-Path $layout "AppxManifest.xml") $manifest -Encoding UTF8

$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1
if ($devMode -and -not $NoInstall) {
    # Developer Mode: register the loose layout directly. No MSIX, no signing, no certificate.
    Write-Host "== register (Developer Mode)"
    Get-Process ClaudeDock -ErrorAction SilentlyContinue | Stop-Process -Force
    Add-AppxPackage -Register (Join-Path $layout "AppxManifest.xml") -ForceApplicationShutdown -ForceUpdateFromAnyVersion
    Get-AppxPackage -Name ClaudeDock | Select-Object Name, Version, PackageFamilyName, InstallLocation
    if ($cmdPalWasRunning) { Start-CommandPalette }
    return
}

Write-Host "== pack"
$makeappx = Get-MakeAppx
& $makeappx pack /d $layout /p $msix /o /nv | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
Write-Host "Packed $msix"

Write-Host "== sign"
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=ClaudeDock' -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $cert) { throw "Signing certificate 'CN=ClaudeDock' not found. Run trust-cert.ps1 first." }
$signtool = Join-Path (Split-Path $makeappx) "signtool.exe"
& $signtool sign /q /fd SHA256 /sha1 $cert.Thumbprint /s My $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed" }

if ($NoInstall) { return }

Write-Host "== install"
Get-Process ClaudeDock -ErrorAction SilentlyContinue | Stop-Process -Force
Add-AppxPackage -Path $msix -ForceApplicationShutdown -ForceUpdateFromAnyVersion
Get-AppxPackage -Name ClaudeDock | Select-Object Name, Version, PackageFamilyName, InstallLocation
if ($cmdPalWasRunning) { Start-CommandPalette }
