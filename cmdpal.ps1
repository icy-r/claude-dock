# Dot-source this file: . "$PSScriptRoot\cmdpal.ps1"
#
# Stopping only Microsoft.CmdPal.UI leaves every extension process running. The next Command Palette
# then reuses those processes, and their links to the old Command Palette are dead, so their dock bands
# stop updating (seen with the Media Controls extension). So always stop the extensions with it.

function Get-CmdPalExtensionProcesses {
    # Every installed package that declares a com.microsoft.commandpalette app extension.
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $folders = [System.Collections.Generic.List[string]]::new()
    foreach ($pkg in Get-AppxPackage) {
        if (-not $pkg.InstallLocation) { continue }
        $manifest = Join-Path $pkg.InstallLocation "AppxManifest.xml"
        if (-not (Test-Path -LiteralPath $manifest)) { continue }
        $text = [IO.File]::ReadAllText($manifest)
        if ($text -notmatch 'Name="com\.microsoft\.commandpalette"') { continue }
        try { [xml]$xml = $text } catch { continue }
        foreach ($app in $xml.Package.Applications.Application) {
            if ($app.OuterXml -match 'Name="com\.microsoft\.commandpalette"' -and $app.Executable) {
                # By name: a sparse package (PowerToys) runs its exe from outside the package folder.
                [void]$names.Add([IO.Path]::GetFileNameWithoutExtension($app.Executable))
            }
        }
        # By folder: helper processes the extension starts itself (for example MediaHost.exe).
        if (-not $pkg.IsPartiallyStaged -and $pkg.SignatureKind -ne "System") {
            $folders.Add($pkg.InstallLocation.TrimEnd('\') + '\')
        }
    }

    foreach ($proc in Get-Process) {
        if ($names.Contains($proc.Name)) { $proc; continue }
        $path = $proc.Path
        if (-not $path) { continue }
        foreach ($folder in $folders) {
            if ($path.StartsWith($folder, [StringComparison]::OrdinalIgnoreCase)) { $proc; break }
        }
    }
}

# Stops Command Palette and all its extension processes. Returns $true if Command Palette was running.
function Stop-CommandPalette {
    $wasRunning = [bool](Get-Process Microsoft.CmdPal.UI -ErrorAction SilentlyContinue)
    # UI first, so it does not start the extensions again while they stop.
    Get-Process Microsoft.CmdPal.UI -ErrorAction SilentlyContinue | Stop-Process -Force
    $extensions = @(Get-CmdPalExtensionProcesses)
    if ($extensions.Count -gt 0) {
        Write-Host "Stopping Command Palette extensions: $(($extensions.Name | Sort-Object -Unique) -join ', ')"
        $extensions | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 800
    return $wasRunning
}

# Starts Command Palette. It starts its extensions again on its own.
function Start-CommandPalette {
    Start-Process "explorer.exe" "shell:AppsFolder\Microsoft.CommandPalette_8wekyb3d8bbwe!App"
}
