# Red Alert installer. Installs Red Alert (the broadcaster's app: caster desk, OBS, replays,
# graphics, Twitch) for this Windows user (no admin needed), adds Start menu and desktop shortcuts,
# and opens it. Only whoever runs the stream needs it; hosts don't. Safe to run again: it replaces
# the app with this release's. The app updates itself after that.
#
# Works in Windows PowerShell 5.1 (what the .bat starts), so no PowerShell 7 syntax.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes Invoke-WebRequest much faster in 5.1
# The release this installer came with (filled in by the build); otherwise the newest broadcast release.
$AppUrl = if ($env:TT_APP_URL) { $env:TT_APP_URL } else { '__TTB_URL__' }
$AppName = 'Red Alert'

# The newest Red Alert release (they're tagged broadcast-v..., apart from The Button's).
function Get-AppUrl {
    if ($AppUrl.StartsWith('http')) { return $AppUrl }
    $list = Invoke-RestMethod -Uri 'https://api.github.com/repos/Ljbutton/AU/releases?per_page=30' -UseBasicParsing
    foreach ($r in $list) {
        if ($r.draft -or -not $r.tag_name.StartsWith('broadcast-v')) { continue }
        $asset = $r.assets | Where-Object { $_.name -eq 'RedAlert.exe' } | Select-Object -First 1
        if ($asset) { return $asset.browser_download_url }
    }
    throw 'No Red Alert release found.'
}

function Write-Step([string]$text) { Write-Host ''; Write-Host "  $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "  $text" -ForegroundColor Green }

function Get-InstallDir {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { [IO.Path]::GetTempPath() }
    return [IO.Path]::Combine($base, 'Programs', 'RedAlert')
}

# Puts the app in place, replacing an older copy (closing it first if it's open).
function Install-App([string]$exe, [string]$dir) {
    Get-Process -Name 'RedAlert', 'TTBroadcast' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $target = [IO.Path]::Combine($dir, 'RedAlert.exe')
    Copy-Item -LiteralPath $exe -Destination $target -Force
    return $target
}

# The app was called TT Broadcast before 0.2: its shortcuts and program folder go (its settings are
# copied into Red Alert's folder the first time Red Alert starts).
function Remove-OldName([string]$programs, [string]$desktop, [string]$oldDir) {
    foreach ($link in @([IO.Path]::Combine($programs, 'TT Broadcast.lnk'), [IO.Path]::Combine($desktop, 'TT Broadcast.lnk'))) {
        Remove-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $oldDir) { Remove-Item -LiteralPath $oldDir -Recurse -Force -ErrorAction SilentlyContinue }
}

function New-Shortcut([string]$path, [string]$target) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $target
    $link.WorkingDirectory = [IO.Path]::GetDirectoryName($target)
    $link.Description = 'Among Us tournament broadcast: caster desk, OBS, replays and graphics'
    $link.Save()
}

function Invoke-Installer {
    Write-Host ''
    Write-Host '  ============================================' -ForegroundColor Magenta
    Write-Host '        Red Alert - installer' -ForegroundColor Magenta
    Write-Host '  ============================================' -ForegroundColor Magenta
    Write-Host '  Only whoever runs the stream needs this (hosts use The Button).'

    Write-Step 'Downloading Red Alert...'
    $temp = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'redalert-' + [guid]::NewGuid().ToString('N') + '.exe')
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri (Get-AppUrl) -OutFile $temp -UseBasicParsing
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
        Remove-OldName ([Environment]::GetFolderPath('Programs')) ([Environment]::GetFolderPath('Desktop')) ([IO.Path]::Combine([IO.Path]::GetDirectoryName((Get-InstallDir)), 'TTBroadcast'))
    } catch {
        Write-Host "  (Couldn't add shortcuts: $($_.Exception.Message). The app is at $exe)"
    }

    Write-Ok 'Installed. Opening Red Alert...'
    Write-Host ''
    Write-Host '  The first time, it takes your caster setup (OBS, priorities, keys, roster,'
    Write-Host '  administration code) from TT Broadcast or The Button on this PC, if you had it there.'
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
