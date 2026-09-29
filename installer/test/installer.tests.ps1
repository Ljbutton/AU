# Checks the installer's logic with PowerShell on any OS: pwsh installer/test/installer.tests.ps1
$ErrorActionPreference = 'Stop'
$env:TT_INSTALLER_TEST = '1'
. (Join-Path $PSScriptRoot '..' 'install.ps1')
$failures = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:failures++ }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('tt-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

Check ($AppUrl -like '*/releases/latest/download/TheButton.exe') 'downloads The Button from the latest release'
Check ((Get-InstallDir) -like '*Programs*TheButton') 'installs into the user''s programs folder'

$download = Join-Path $root 'download.exe'
Set-Content -LiteralPath $download -Value 'app v1'
$dir = Join-Path $root 'Programs'
$exe = Install-App $download $dir
Check ((Get-Content -LiteralPath $exe) -eq 'app v1') 'puts the app in place'
Set-Content -LiteralPath $download -Value 'app v2'
Install-App $download $dir | Out-Null
Check ((Get-Content -LiteralPath $exe) -eq 'app v2') 'running it again replaces the app'
Check ((Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..' 'Install-TournamentTracker.header.bat')) -match 'PS-START') 'the .bat runs the PowerShell after #PS-START'

Remove-Item -LiteralPath $root -Recurse -Force
if ($failures -gt 0) { Write-Host "$failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'All installer checks passed.' -ForegroundColor Green
