# Packs the companion extension into a .vsix (no vsce/npm needed) and installs it into Cursor.
param([switch]$NoInstall)
$ErrorActionPreference = "Stop"
$src = $PSScriptRoot
$out = Join-Path $src "..\out"
$stage = Join-Path $out "vsix-stage"
$vsix = Join-Path $out "claude-dock-focus.vsix"
$pkg = Get-Content (Join-Path $src "package.json") -Raw | ConvertFrom-Json

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage "extension") | Out-Null
Copy-Item (Join-Path $src "package.json"), (Join-Path $src "extension.js") (Join-Path $stage "extension")

@"
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension=".json" ContentType="application/json"/>
  <Default Extension=".js" ContentType="application/javascript"/>
  <Default Extension=".vsixmanifest" ContentType="text/xml"/>
</Types>
"@ | ForEach-Object { [IO.File]::WriteAllText((Join-Path $stage "[Content_Types].xml"), $_) }

@"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Language="en-US" Id="$($pkg.name)" Version="$($pkg.version)" Publisher="$($pkg.publisher)"/>
    <DisplayName>$($pkg.displayName)</DisplayName>
    <Description xml:space="preserve">$($pkg.description)</Description>
    <Properties>
      <Property Id="Microsoft.VisualStudio.Code.Engine" Value="$($pkg.engines.vscode)"/>
      <Property Id="Microsoft.VisualStudio.Code.ExtensionKind" Value="ui"/>
    </Properties>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Code"/>
  </Installation>
  <Dependencies/>
  <Assets>
    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true"/>
  </Assets>
</PackageManifest>
"@ | ForEach-Object { [IO.File]::WriteAllText((Join-Path $stage "extension.vsixmanifest"), $_) }

if (Test-Path $vsix) { Remove-Item $vsix -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $vsix)
Write-Host "Packed $vsix"

if ($NoInstall) { return }
$cursor = Join-Path $env:LOCALAPPDATA "Programs\cursor\resources\app\bin\cursor.cmd"
& $cursor --install-extension $vsix --force
