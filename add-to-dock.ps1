# Pins the Claude Dock band to the Command Palette Dock and restarts Command Palette.
# Band: com.claudedock.sessions (Claude Code sessions).
# A band that is already on the dock stays where you put it. A missing band goes right after
# another Claude Dock band if there is one, else at the start of -Section. Backs up settings.json.
param(
    [ValidateSet("StartBands", "CenterBands", "EndBands")][string]$Section = "EndBands",
    [string[]]$BandIds = @("com.claudedock.sessions")
)
$ErrorActionPreference = "Stop"

$pkg = Get-AppxPackage -Name ClaudeDock
if (-not $pkg) { throw "ClaudeDock package is not installed. Run pack.ps1 first." }
$providerId = "$($pkg.PackageFamilyName)!App!ClaudeDock"
$settingsPath = Join-Path $env:LOCALAPPDATA "Packages\Microsoft.CommandPalette_8wekyb3d8bbwe\LocalState\settings.json"

# Stop Command Palette so it does not overwrite our change on exit. Stop its extensions too,
# else they keep a dead link to the old instance and their bands stop updating.
. (Join-Path $PSScriptRoot "cmdpal.ps1")
[void](Stop-CommandPalette)

$backup = "$settingsPath.claudedock-backup-$(Get-Date -Format yyyyMMdd-HHmmss)"
Copy-Item $settingsPath $backup
# JsonNode keeps every other value exactly as Command Palette wrote it.
$root = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($settingsPath))
$dock = $root["DockSettings"]
if ($null -eq $dock) { throw "No DockSettings in settings.json - enable the Dock in Command Palette settings first." }

function Find-Band([string]$id) {
    foreach ($name in "StartBands", "CenterBands", "EndBands") {
        if ($null -eq $dock[$name]) { continue }
        $arr = $dock[$name].AsArray()
        for ($i = 0; $i -lt $arr.Count; $i++) {
            if ($arr[$i]["ProviderId"].GetValue[string]() -eq $providerId -and $arr[$i]["CommandId"].GetValue[string]() -eq $id) {
                return @{ Section = $name; Index = $i; Node = $arr[$i] }
            }
        }
    }
    return $null
}

foreach ($bandId in $BandIds) {
    $found = Find-Band $bandId
    if ($found) {
        $found.Node["ShowSubtitles"] = [System.Text.Json.Nodes.JsonValue]::Create($false)  # hover text lives in the subtitle
        Write-Host "$bandId already on the dock ($($found.Section))."
        continue
    }

    $band = [System.Text.Json.Nodes.JsonNode]::Parse((@{ ProviderId = $providerId; CommandId = $bandId; ShowTitles = $null; ShowSubtitles = $false } | ConvertTo-Json -Compress))
    $sibling = $BandIds | Where-Object { $_ -ne $bandId } | ForEach-Object { Find-Band $_ } | Select-Object -First 1
    if ($sibling) {
        $dock[$sibling.Section].AsArray().Insert($sibling.Index + 1, $band)
        Write-Host "Added $bandId to $($sibling.Section) after its sibling band."
    }
    else {
        if ($null -eq $dock[$Section]) { $dock[$Section] = [System.Text.Json.Nodes.JsonArray]::new() }
        $dock[$Section].AsArray().Insert(0, $band)
        Write-Host "Added $bandId to $Section."
    }
}

$opts = [System.Text.Json.JsonSerializerOptions]::new(); $opts.WriteIndented = $true
$opts.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
[IO.File]::WriteAllText($settingsPath, $root.ToJsonString($opts))
Write-Host "Backup: $backup"

Start-CommandPalette
Write-Host "Command Palette restarted."
