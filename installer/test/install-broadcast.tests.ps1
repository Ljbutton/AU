# Checks TT Broadcast's installer with PowerShell on any OS: pwsh installer/test/install-broadcast.tests.ps1
$ErrorActionPreference = 'Stop'
$env:TT_INSTALLER_TEST = '1'
. (Join-Path $PSScriptRoot '..' 'install-broadcast.ps1')
$failures = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:failures++ }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('ttb-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

Check ($AppUrl -eq '__TTB_URL__') 'the build fills in its own release'
$AppUrl = 'https://github.com/Ljbutton/AU/releases/download/broadcast-v0.1.0/TTBroadcast.exe'
Check ((Get-AppUrl) -eq $AppUrl) 'downloads the release it came with'
Check ((Get-InstallDir) -like '*Programs*TTBroadcast') 'installs into the user''s programs folder, apart from The Button'

$download = Join-Path $root 'download.exe'
Set-Content -LiteralPath $download -Value 'app v1'
$dir = Join-Path $root 'Programs'
$exe = Install-App $download $dir
Check ((Get-Content -LiteralPath $exe) -eq 'app v1') 'puts the app in place'
Set-Content -LiteralPath $download -Value 'app v2'
Install-App $download $dir | Out-Null
Check ((Get-Content -LiteralPath $exe) -eq 'app v2') 'running it again replaces the app'
Check ((Split-Path -Leaf $exe) -eq 'TTBroadcast.exe') 'as TTBroadcast.exe'
Check ((Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..' 'Install-TTBroadcast.header.bat')) -match 'PS-START') 'the .bat runs the PowerShell after #PS-START'

Remove-Item -LiteralPath $root -Recurse -Force
if ($failures -gt 0) { Write-Host "$failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'All TT Broadcast installer checks passed.' -ForegroundColor Green
