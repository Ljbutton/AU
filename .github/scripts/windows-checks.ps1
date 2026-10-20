# The Windows job's checks that need the real apps (.github/workflows/build.yml, job "windows"). PowerShell 7.
#
#   -Apps        Starts the published TheButton.exe and RedAlert.exe as a user would: each must keep running,
#                answer on its local server (the page with its per-launch token; /app/state refused without the
#                token, answered with it) and still answer after its window (WebView2) has opened. Also shows
#                whether WebView2 started, Red Alert's global Mute all key, and The Button's lobby voice capture
#                (Windows' per-program audio capture) with a stand-in Discord running. Then closes them.
#   -Installers  Builds Install-TheButton.bat and Install-RedAlert.bat as the release does and runs each as a
#                user would (cmd, then Windows PowerShell 5.1) from a folder named "Jo's Downloads",
#                downloading the exe from a web server on this PC. Checks the app is installed, has its
#                Start menu and desktop shortcuts, and opens.
#
# Logs, screenshots and the installers' output go to $env:RUNNER_TEMP\windows-checks (kept with the run).

param([switch]$Apps, [switch]$Installers)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$out = Join-Path $temp 'windows-checks'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$local = [Environment]::GetFolderPath('LocalApplicationData')
$buttonDir = Join-Path $local 'TheButton'
$redAlertDir = Join-Path $local 'RedAlert'

# ---- Helpers -----------------------------------------------------------------------------------------

# A GET that never throws: the status (0 when nothing answered) and the text.
function Get-Http([string]$Url, [hashtable]$Headers = @{}) {
    try {
        $r = Invoke-WebRequest -Uri $Url -Headers $Headers -TimeoutSec 15 -SkipHttpErrorCheck
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Body = [string]$r.Content }
    } catch {
        return [pscustomobject]@{ Status = 0; Body = $_.Exception.Message }
    }
}

# An /app/ call with the app's token: its JSON, or it throws.
function Get-Json([string]$Url, [string]$Token) {
    $r = Get-Http $Url @{ 'X-App-Token' = $Token }
    if ($r.Status -ne 200) { throw "$Url answered $($r.Status): $($r.Body)" }
    return $r.Body | ConvertFrom-Json
}

# A process and everything it started (WebView2's processes), closed.
function Stop-Tree($Process) {
    if ($null -eq $Process) { return }
    try { if (-not $Process.HasExited) { & taskkill.exe /PID $Process.Id /T /F 2>&1 | Out-Null } } catch { }
    try { $null = $Process.WaitForExit(10000) } catch { }
}

function Save-Screen([string]$Name) {
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
        $bmp.Save((Join-Path $out "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Host "  Screenshot: $Name.png (in the windows-checks artifact)."
    } catch { Write-Host "  (No screenshot: $($_.Exception.Message))" }
}

function Get-FreePort {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    try { return $l.LocalEndpoint.Port } finally { $l.Stop() }
}

# The Button's settings, in the format it writes: it doesn't update itself or the mod while it's tested
# (the page asks for /app/state, which would install a newer release over the exe being tested).
function Reset-ButtonSettings {
    New-Item -ItemType Directory -Force -Path $buttonDir | Out-Null
    [IO.File]::WriteAllText((Join-Path $buttonDir 'app.json'), '{"AutoUpdateApp":false,"AutoUpdateMod":false}')
}

# Where each app's server may be: The Button picks a free port and writes it to app.json; Red Alert uses
# 8768, or the next three when that's taken.
$buttonPorts = {
    try {
        $s = [IO.File]::ReadAllText((Join-Path $buttonDir 'app.json')) | ConvertFrom-Json
        if ($s.AppPort -gt 0) { [int]$s.AppPort }
    } catch { }
}
$redAlertPorts = { 8768..8771 }

# Waits (up to a minute) for the app's own page on one of its ports; the address and the page, or it throws.
function Wait-Server($Process, [string]$Name, [scriptblock]$Ports, [string]$Title) {
    $started = [DateTime]::UtcNow
    while ($true) {
        if ($Process.HasExited) { throw "$Name closed by itself before its server answered (exit code $($Process.ExitCode))." }
        foreach ($port in @(& $Ports)) {
            $r = Get-Http "http://127.0.0.1:$port/"
            if ($r.Status -eq 200 -and $r.Body.Contains("<title>$Title</title>")) {
                Write-Host "  Its server answers at http://127.0.0.1:$port/ ($([int]([DateTime]::UtcNow - $started).TotalSeconds) s after it started)."
                return [pscustomobject]@{ Base = "http://127.0.0.1:$port"; Page = $r.Body }
            }
        }
        if (([DateTime]::UtcNow - $started).TotalSeconds -gt 60) { throw "$Name's server didn't answer within 60 s." }
        Start-Sleep -Milliseconds 500
    }
}

function Assert-Running($Process, [string]$Name, [int]$Seconds) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if ($Process.HasExited) { throw "$Name closed by itself (exit code $($Process.ExitCode))." }
        Start-Sleep -Seconds 1
    }
}

function Show-Log([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { Write-Host "  (No $(Split-Path -Leaf $Path).)"; return @() }
    Copy-Item -LiteralPath $Path -Destination $out -Force
    $lines = @(Get-Content -LiteralPath $Path)
    Write-Host "  $(Split-Path -Leaf $Path):"
    $lines | Select-Object -Last 60 | ForEach-Object { Write-Host "    $_" }
    return $lines
}

# ---- The apps ----------------------------------------------------------------------------------------

function Test-App {
    param(
        [string]$Name, [string]$Exe, [string]$Title, [scriptblock]$Ports,
        [string]$DataDir,       # its folder in %LOCALAPPDATA% (settings, log, WebView2's profile)
        [string]$LogName,
        [string[]]$FailOn = @(), # log lines that mean a real problem
        [scriptblock]$More      # its own checks, given the server's address, the token and /app/state
    )
    Write-Host "::group::$Name"
    $p = $null
    try {
        if (-not (Test-Path -LiteralPath $Exe)) { throw "$Exe wasn't built." }
        Write-Host "  Starting $Exe"
        $p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe) -PassThru
        $null = $p.Handle   # keeps its exit code readable once it has exited
        $server = Wait-Server $p $Name $Ports $Title
        $base = $server.Base
        $m = [regex]::Match($server.Page, "const TOKEN = '([^']+)'")
        if (-not $m.Success -or $m.Groups[1].Value -eq '__APP_TOKEN__') { throw "$Name's page came without its token." }
        $token = $m.Groups[1].Value
        $anon = Get-Http "$base/app/state"
        if ($anon.Status -ne 401) { throw "$Name answered /app/state without the token with $($anon.Status), not 401." }
        $state = Get-Json "$base/app/state" $token
        Write-Host "  /app/state: 401 without the token, 200 with it (version $($state.app))."

        # The window: WebView2 starts and loads the page (without WebView2 the app opens the browser instead).
        Assert-Running $p $Name 15
        $views = @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf((Join-Path $DataDir 'WebView2'), [StringComparison]::OrdinalIgnoreCase) -ge 0 })
        if ($views.Count -gt 0) { Write-Host "  Its window is showing the page in WebView2 ($($views.Count) WebView2 processes)." }
        else { Write-Host "::warning::$($Name): no WebView2 process for its window (no WebView2 runtime on this runner?). Without it the app opens the page in the browser; its server was still checked." }

        if ($More) { & $More $base $token (Get-Json "$base/app/state" $token) }

        Assert-Running $p $Name 3
        $null = Get-Json "$base/app/state" $token
        Save-Screen $Name
        Write-Host "  $Name is still running and answering."
    } finally {
        Stop-Tree $p
        $lines = Show-Log (Join-Path $DataDir $LogName)
        Write-Host '::endgroup::'
    }
    foreach ($bad in $FailOn) {
        foreach ($line in $lines) { if ($line -and $line.Contains($bad)) { throw "$($Name)'s log says: $line" } }
    }
}

# The Button's lobby voice: Windows' per-program audio capture (WASAPI process loopback) of Discord and
# Among Us, for the send page. A copy of node.exe (one file that runs anywhere) named Discord.exe stands in
# for Discord, so The Button finds a Discord and opens its sound. A runner has no sound card, so what it
# says is shown, not checked; The Button has to keep running whatever Windows says.
$buttonMore = {
    param($base, $token, $state)
    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    $discord = $null
    if ($node) {
        $fakeDir = Join-Path $temp 'stand-in-discord'
        New-Item -ItemType Directory -Force -Path $fakeDir | Out-Null
        $fake = Join-Path $fakeDir 'Discord.exe'
        Copy-Item -LiteralPath $node -Destination $fake -Force
        $discord = Start-Process -FilePath $fake -ArgumentList '-e', 'setInterval(()=>{},1000)' -PassThru -WindowStyle Hidden
    } else { Write-Host '  (No node.exe for a stand-in Discord: The Button will say Discord is not running.)' }
    try {
        $voice = $null
        for ($i = 0; $i -lt 30; $i++) {
            $voice = (Get-Json "$base/app/voice" $token).state
            if ($voice.voice.state -notin @('starting', 'off', 'not running')) { break }
            Start-Sleep -Milliseconds 500
        }
        Write-Host "  Lobby voice capture: supported $($voice.supported), running $($voice.running); Discord (stand-in): $($voice.voice.state); Among Us: $($voice.game.state)."
        if (-not $voice.supported) { Write-Host '::warning::The Button says this Windows has no per-program audio capture (it needs Windows 10 2004 or later).' }
        elseif ($voice.voice.state -ne 'capturing') { Write-Host "::notice::The Button couldn't capture the stand-in Discord's sound on this runner: $($voice.voice.state)" }
    } finally { Stop-Tree $discord }
}

$redAlertMore = {
    param($base, $token, $state)
    # The Stream Deck plugin finds Red Alert by this, without its key.
    $hello = Get-Http "$base/deck/hello"
    if ($hello.Status -ne 200 -or -not $hello.Body.Contains('Red Alert')) { throw "/deck/hello answered $($hello.Status): $($hello.Body)" }
    Write-Host "  Stream Deck: $(($hello.Body | ConvertFrom-Json).message)"
    if ($state.hotkeyProblem) { Write-Host "::warning::Red Alert's global Mute all key: $($state.hotkeyProblem)" }
    else { Write-Host '  Its global Mute all key (Ctrl+Shift+M) is registered.' }
}

# ---- The installers ----------------------------------------------------------------------------------

# A .bat as the release builds it: the header, #PS-START, then the PowerShell, with Windows line endings
# and no byte order mark (cmd would read one as part of the first line).
function Write-Bat([string]$Header, [string]$Script, [string]$Path) {
    $text = [IO.File]::ReadAllText($Header) + "#PS-START`n" + $Script
    [IO.File]::WriteAllText($Path, ($text -replace "`r?`n", "`r`n"))
}

function Test-Installer {
    param(
        [string]$Name, [string]$Bat, [string]$Source, [string]$Installed, [string]$ProcessName,
        [string]$Title, [scriptblock]$Ports, [hashtable]$Environment = @{}
    )
    Write-Host "::group::$Name installer"
    $app = $null
    try {
        # "pause" at its end reads Enter from here instead of waiting for a key.
        $keys = Join-Path (Split-Path -Parent $Bat) 'enter.txt'
        [IO.File]::WriteAllText($keys, "`r`n")
        $said = "$Bat.out.txt"
        foreach ($k in $Environment.Keys) { [Environment]::SetEnvironmentVariable($k, $Environment[$k]) }
        try {
            $p = Start-Process -FilePath $env:ComSpec -ArgumentList '/d', '/c', "`"$Bat`"" -NoNewWindow -PassThru `
                -RedirectStandardInput $keys -RedirectStandardOutput $said -RedirectStandardError "$Bat.err.txt"
            $null = $p.Handle
            if (-not $p.WaitForExit(240000)) { Stop-Tree $p; throw "$Name's installer didn't finish within 4 minutes." }
        } finally {
            foreach ($k in $Environment.Keys) { [Environment]::SetEnvironmentVariable($k, $null) }
        }
        $text = [IO.File]::ReadAllText($said) + [IO.File]::ReadAllText("$Bat.err.txt")
        Copy-Item -LiteralPath $said -Destination (Join-Path $out "$Name installer output.txt") -Force
        $text -split "`r?`n" | ForEach-Object { Write-Host "    $_" }
        if ($text -match 'Install failed') { throw "$Name's installer failed." }

        if (-not (Test-Path -LiteralPath $Installed)) { throw "$Name isn't at $Installed." }
        $built = (Get-FileHash -LiteralPath $Source).Hash
        if ((Get-FileHash -LiteralPath $Installed).Hash -ne $built) { throw "$Installed isn't the exe that was built." }
        Write-Host "  Installed at $Installed."
        # (Paths are compared by the file they lead to: one may be written with short folder names, RUNNER~1.)
        $leaf = Split-Path -Leaf $Installed
        $shell = New-Object -ComObject WScript.Shell
        foreach ($folder in 'Programs', 'Desktop') {
            $lnk = Join-Path ([Environment]::GetFolderPath($folder)) "$Name.lnk"
            if (-not (Test-Path -LiteralPath $lnk)) { throw "No $Name shortcut in $folder ($lnk)." }
            $target = $shell.CreateShortcut($lnk).TargetPath
            if (-not $target -or (Split-Path -Leaf $target) -ne $leaf -or -not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne $built) {
                throw "The $Name shortcut in $folder opens '$target', not $Installed."
            }
        }
        Write-Host '  Its Start menu and desktop shortcuts open it.'

        # The installer opens the app it installed (not the one in dist).
        $suffix = '\' + (Split-Path -Leaf (Split-Path -Parent $Installed)) + '\' + $leaf
        for ($i = 0; $i -lt 60 -and -not $app; $i++) {
            $app = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -and $_.Path.EndsWith("\Programs$suffix", [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
            if (-not $app) { Start-Sleep -Milliseconds 500 }
        }
        if (-not $app) { throw "The installer didn't open $Name." }
        $null = $app.Handle
        $null = Wait-Server $app $Name $Ports $Title
        Write-Host "  The installer opened $Name, and it answers."
    } finally {
        Stop-Tree $app
        Write-Host '::endgroup::'
    }
}

# ---- Run ---------------------------------------------------------------------------------------------

try {
    if ($Apps) {
        Reset-ButtonSettings
        Test-App -Name 'The Button' -Exe (Join-Path $root 'dist\app\TheButton.exe') -Title 'The Button' -Ports $buttonPorts `
            -DataDir $buttonDir -LogName 'the-button.log' -More $buttonMore
        Test-App -Name 'Red Alert' -Exe (Join-Path $root 'dist\broadcast\RedAlert.exe') -Title 'Red Alert' -Ports $redAlertPorts `
            -DataDir $redAlertDir -LogName 'red-alert.log' -FailOn @('Unhandled error', 'Red Alert stopped') -More $redAlertMore
    }

    if ($Installers) {
        $dist = Join-Path $root 'dist'
        $buttonExe = Join-Path $dist 'app\TheButton.exe'
        $redAlertExe = Join-Path $dist 'broadcast\RedAlert.exe'
        foreach ($f in $buttonExe, $redAlertExe) { if (-not (Test-Path -LiteralPath $f)) { throw "$f wasn't built." } }
        Reset-ButtonSettings
        Remove-Item Env:TT_INSTALLER_TEST -ErrorAction SilentlyContinue
        # Run from a folder with a space and an apostrophe in its name, as a user's Downloads can be.
        $work = Join-Path $temp "installers\Jo's Downloads"
        New-Item -ItemType Directory -Force -Path $work | Out-Null

        # The downloads come from a web server on this PC (the real installers fetch them from GitHub's releases).
        $python = (Get-Command python -ErrorAction SilentlyContinue).Source
        if (-not $python -or $python -like '*\WindowsApps\*') { throw 'No Python on this runner, for the web server the installers download from.' }
        $port = Get-FreePort
        $web = Start-Process -FilePath $python -ArgumentList '-m', 'http.server', "$port", '--bind', '127.0.0.1', '--directory', "`"$dist`"" `
            -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $work 'web.out.txt') -RedirectStandardError (Join-Path $out 'web-server.log')
        try {
            $ready = $false
            for ($i = 0; $i -lt 30 -and -not $ready; $i++) {
                $ready = (Get-Http "http://127.0.0.1:$port/").Status -eq 200
                if (-not $ready) { Start-Sleep -Milliseconds 500 }
            }
            if (-not $ready) { throw "The local web server didn't start on port $port." }

            $buttonBat = Join-Path $work 'Install-TheButton.bat'
            Write-Bat (Join-Path $root 'installer\Install-TournamentTracker.header.bat') ([IO.File]::ReadAllText((Join-Path $root 'installer\install.ps1'))) $buttonBat
            Test-Installer -Name 'The Button' -Bat $buttonBat -Source $buttonExe -Installed (Join-Path $env:LOCALAPPDATA 'Programs\TheButton\TheButton.exe') `
                -ProcessName 'TheButton' -Title 'The Button' -Ports $buttonPorts -Environment @{ TT_APP_URL = "http://127.0.0.1:$port/app/TheButton.exe" }

            # Red Alert's installer has its release's address written in by the build (here, this PC's).
            $redAlertBat = Join-Path $work 'Install-RedAlert.bat'
            $redAlertScript = [IO.File]::ReadAllText((Join-Path $root 'installer\install-redalert.ps1')).Replace('__TTB_URL__', "http://127.0.0.1:$port/broadcast/RedAlert.exe")
            Write-Bat (Join-Path $root 'installer\Install-RedAlert.header.bat') $redAlertScript $redAlertBat
            Test-Installer -Name 'Red Alert' -Bat $redAlertBat -Source $redAlertExe -Installed (Join-Path $env:LOCALAPPDATA 'Programs\RedAlert\RedAlert.exe') `
                -ProcessName 'RedAlert' -Title 'Red Alert' -Ports $redAlertPorts
        } finally {
            Stop-Tree $web
        }
    }
} catch {
    Write-Host "::error::$($_.Exception.Message)"
    exit 1
}
# Not the exit code of whatever ran last (taskkill says 128 for a process that had already gone).
exit 0
