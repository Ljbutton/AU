# Tournament Tracker installer. Finds Among Us (Steam or Epic), downloads the mod with
# BepInEx, and installs both into the game folder. Safe to run again to update: settings,
# links and stats are never touched.
#
# Works in Windows PowerShell 5.1 (what the .bat starts), so no PowerShell 7 syntax.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes Invoke-WebRequest much faster in 5.1
$DownloadUrl = if ($env:TT_DOWNLOAD_URL) { $env:TT_DOWNLOAD_URL } else { 'https://github.com/Ljbutton/AU/releases/latest/download/TournamentTracker-Full.zip' }

# Joins path parts. Unlike Join-Path, it doesn't fail when the drive doesn't exist.
function Join-Parts([string[]]$parts) {
    return [IO.Path]::Combine([string[]]$parts)
}

function Write-Step([string]$text) { Write-Host ''; Write-Host "  $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "  $text" -ForegroundColor Green }
function Write-Problem([string]$text) { Write-Host "  $text" -ForegroundColor Yellow }

# Test-Path, but a path on a drive that doesn't exist is just "not there" instead of an error.
function Test-Exists([string]$path) {
    try { return [bool]($path -and (Test-Path -LiteralPath $path)) } catch { return $false }
}

function Test-AmongUsFolder([string]$path) {
    return $path -and (Test-Exists (Join-Parts @($path, 'Among Us.exe')))
}

# Steam keeps a list of its library folders in libraryfolders.vdf.
function Get-SteamLibraries([string]$steamPath) {
    $libraries = @()
    if (-not $steamPath) { return $libraries }
    $libraries += $steamPath
    $vdf = Join-Parts @($steamPath, 'steamapps', 'libraryfolders.vdf')
    if (Test-Exists $vdf) {
        foreach ($m in [regex]::Matches((Get-Content -Raw -LiteralPath $vdf), '"path"\s+"([^"]+)"')) {
            $libraries += ($m.Groups[1].Value -replace '\\\\', '\')
        }
    }
    return $libraries | Select-Object -Unique
}

# Epic writes one JSON manifest per installed game.
function Get-EpicInstalls([string]$manifestDir) {
    $found = @()
    if (-not (Test-Exists $manifestDir)) { return $found }
    foreach ($file in Get-ChildItem -LiteralPath $manifestDir -Filter '*.item' -ErrorAction SilentlyContinue) {
        try {
            $item = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
            if ($item.DisplayName -like 'Among Us*' -and $item.InstallLocation) { $found += $item.InstallLocation }
        } catch { }
    }
    return $found
}

function Find-AmongUs {
    $candidates = @()
    $steamPath = $null
    try { $steamPath = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath } catch { }
    foreach ($lib in Get-SteamLibraries $steamPath) {
        $candidates += [pscustomobject]@{ Store = 'Steam'; Path = (Join-Parts @($lib, 'steamapps', 'common', 'Among Us')) }
    }
    foreach ($path in Get-EpicInstalls 'C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests') {
        $candidates += [pscustomobject]@{ Store = 'Epic Games'; Path = $path }
    }
    foreach ($path in @('C:\Program Files (x86)\Steam\steamapps\common\Among Us', 'C:\Program Files\Epic Games\AmongUs')) {
        $candidates += [pscustomobject]@{ Store = 'Found'; Path = $path }
    }
    $seen = @{}
    return @($candidates | Where-Object { (Test-AmongUsFolder $_.Path) -and -not $seen.ContainsKey($_.Path.ToLower()) -and ($seen[$_.Path.ToLower()] = $true) })
}

function Test-XboxInstall {
    if (Test-Exists 'C:\XboxGames\Among Us') { return $true }
    try { return [bool](Get-ChildItem 'C:\Program Files\WindowsApps' -Filter 'Innersloth.AmongUs*' -ErrorAction SilentlyContinue) } catch { return $false }
}

function Select-FolderByHand {
    Write-Problem "Couldn't find Among Us automatically. Pick the folder that contains 'Among Us.exe'."
    Write-Host '  (In Steam: right-click Among Us > Manage > Browse local files.)'
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = "Select the Among Us folder (the one with 'Among Us.exe' in it)"
        if ($dialog.ShowDialog() -eq 'OK') { return $dialog.SelectedPath }
    } catch {
        return (Read-Host '  Paste the folder path')
    }
    return $null
}

# Copies the extracted bundle into the game folder. Existing settings, links and stats live
# in BepInEx\config, which the bundle never contains, so they survive an update.
function Install-Bundle([string]$bundleDir, [string]$gameDir) {
    foreach ($item in Get-ChildItem -LiteralPath $bundleDir -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $gameDir -Recurse -Force
    }
    return (Test-Path -LiteralPath (Join-Parts @($gameDir, 'BepInEx', 'plugins', 'TournamentTracker.dll'))) -and
           (Test-Path -LiteralPath (Join-Parts @($gameDir, 'winhttp.dll')))
}

function Invoke-Installer {
    Write-Host ''
    Write-Host '  ============================================' -ForegroundColor Magenta
    Write-Host '    Among Us Tournament Tracker - installer' -ForegroundColor Magenta
    Write-Host '  ============================================' -ForegroundColor Magenta
    Write-Host '  Only the lobby host needs this.'

    Write-Step 'Looking for Among Us...'
    $found = Find-AmongUs
    $gameDir = $null
    if ($found.Count -eq 1) {
        $gameDir = $found[0].Path
        Write-Ok "Found it ($($found[0].Store)): $gameDir"
    } elseif ($found.Count -gt 1) {
        for ($i = 0; $i -lt $found.Count; $i++) { Write-Host "   [$($i + 1)] $($found[$i].Store): $($found[$i].Path)" }
        $pick = Read-Host '  Which one? Type the number'
        $gameDir = $found[[int]$pick - 1].Path
    } else {
        if (Test-XboxInstall) {
            Write-Problem 'Among Us from the Xbox app / Game Pass can''t be modded.'
            Write-Problem 'The host needs the Steam or Epic Games version.'
        }
        $gameDir = Select-FolderByHand
    }
    if (-not (Test-AmongUsFolder $gameDir)) {
        throw "That folder doesn't contain 'Among Us.exe'. Run the installer again and pick the Among Us folder."
    }

    $running = Get-Process -Name 'Among Us' -ErrorAction SilentlyContinue
    if ($running) {
        Write-Problem 'Among Us is running. It has to be closed to install.'
        Read-Host '  Close the game, then press Enter' | Out-Null
        if (Get-Process -Name 'Among Us' -ErrorAction SilentlyContinue) { throw 'Among Us is still running. Close it and run the installer again.' }
    }

    $existing = @(Get-ChildItem -LiteralPath (Join-Parts @($gameDir, 'BepInEx', 'plugins')) -Filter '*.dll' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'TournamentTracker.dll' })
    if ($existing.Count -gt 0) {
        Write-Problem "Other mods are installed ($($existing.Name -join ', ')). They'll stay, but mods built for a different BepInEx version may stop working."
    }

    Write-Step 'Downloading the mod...'
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('tt-install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $zip = Join-Path $temp 'bundle.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $zip -UseBasicParsing
        Write-Step 'Installing...'
        Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $temp 'bundle') -Force
        if (-not (Install-Bundle (Join-Path $temp 'bundle') $gameDir)) { throw 'The files didn''t copy. Is the game folder read-only?' }
    } finally {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Write-Ok 'Installed!'
    Write-Host ''
    Write-Host '  Next:'
    Write-Host '   1. Start Among Us. The FIRST start takes a few minutes: a black console window'
    Write-Host '      appears while the mod loader sets itself up. That only happens once.'
    Write-Host '   2. Close the game, then fill in the settings file:'
    Write-Host "      $(Join-Parts @($gameDir, 'BepInEx', 'config', 'com.ljbutton.tournamenttracker.cfg'))"
    Write-Host ''
    Write-Host '  To update later, run this installer again. Your settings are kept.'
}

if ($env:TT_INSTALLER_TEST -ne '1') {
    try {
        Invoke-Installer
    } catch {
        Write-Host ''
        Write-Host "  Install failed: $($_.Exception.Message)" -ForegroundColor Red
    }
}
