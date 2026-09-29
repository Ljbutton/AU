# Tournament Tracker installer. Installs the Tournament Tracker app for this Windows user
# (no admin needed), adds Start menu and desktop shortcuts, and opens it. The app then finds
# Among Us, installs and updates the mod, and takes the setup code. Safe to run again: it
# replaces the app with the newest one.
#
# Works in Windows PowerShell 5.1 (what the .bat starts), so no PowerShell 7 syntax.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes Invoke-WebRequest much faster in 5.1
$AppUrl = if ($env:TT_APP_URL) { $env:TT_APP_URL } else { 'https://github.com/Ljbutton/AU/releases/latest/download/TournamentTracker.exe' }
$AppName = 'Tournament Tracker'

function Write-Step([string]$text) { Write-Host ''; Write-Host "  $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "  $text" -ForegroundColor Green }

function Get-InstallDir {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { [IO.Path]::GetTempPath() }
    return [IO.Path]::Combine($base, 'Programs', 'TournamentTracker')
}

# Puts the app in place, replacing an older copy (closing it first if it's open).
function Install-App([string]$exe, [string]$dir) {
    Get-Process -Name 'TournamentTracker' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $target = [IO.Path]::Combine($dir, 'TournamentTracker.exe')
    Copy-Item -LiteralPath $exe -Destination $target -Force
    return $target
}

function New-Shortcut([string]$path, [string]$target) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $target
    $link.WorkingDirectory = [IO.Path]::GetDirectoryName($target)
    $link.Description = 'Among Us tournament stats, automute and replays'
    $link.Save()
}

function Invoke-Installer {
    Write-Host ''
    Write-Host '  ============================================' -ForegroundColor Magenta
    Write-Host '    Among Us Tournament Tracker - installer' -ForegroundColor Magenta
    Write-Host '  ============================================' -ForegroundColor Magenta
    Write-Host '  Only the lobby host needs this.'

    Write-Step 'Downloading the Tournament Tracker app...'
    $temp = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'tt-app-' + [guid]::NewGuid().ToString('N') + '.exe')
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $AppUrl -OutFile $temp -UseBasicParsing
    try {
        Write-Step 'Installing...'
        $exe = Install-App $temp (Get-InstallDir)
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }

    try {
        New-Shortcut ([IO.Path]::Combine([Environment]::GetFolderPath('Programs'), "$AppName.lnk")) $exe
        New-Shortcut ([IO.Path]::Combine([Environment]::GetFolderPath('Desktop'), "$AppName.lnk")) $exe
        Write-Ok 'Added it to the Start menu and the desktop.'
    } catch {
        Write-Host "  (Couldn't add shortcuts: $($_.Exception.Message). The app is at $exe)"
    }

    Write-Ok 'Installed. Opening Tournament Tracker...'
    Write-Host ''
    Write-Host '  In the app: install the mod into Among Us, paste your setup code, then start'
    Write-Host '  Among Us. Everything is done from the app; nothing is typed in the game chat.'
    Start-Process -FilePath $exe
}

if ($env:TT_INSTALLER_TEST -ne '1') {
    try {
        Invoke-Installer
    } catch {
        Write-Host ''
        Write-Host "  Install failed: $($_.Exception.Message)" -ForegroundColor Red
    }
}
