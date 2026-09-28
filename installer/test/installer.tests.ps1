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

# Steam library list
$steam = Join-Path $root 'Steam'
New-Item -ItemType Directory -Path (Join-Path $steam 'steamapps') -Force | Out-Null
Set-Content -LiteralPath (Join-Path (Join-Path $steam 'steamapps') 'libraryfolders.vdf') -Value @'
"libraryfolders"
{
	"0"	{ "path"		"C:\\Program Files (x86)\\Steam" }
	"1"	{ "path"		"D:\\Games\\SteamLibrary" "apps" { "945360" "123" } }
}
'@
$libs = @(Get-SteamLibraries $steam)
Check ($libs -contains 'D:\Games\SteamLibrary') 'reads extra Steam libraries from libraryfolders.vdf'
Check ($libs -contains $steam) 'includes the main Steam folder'
Check (@(Get-SteamLibraries $null).Count -eq 0) 'no Steam installed: no libraries'

# Epic manifests
$epic = Join-Path $root 'Manifests'
New-Item -ItemType Directory -Path $epic | Out-Null
'{"DisplayName":"Among Us","InstallLocation":"E:\\Epic\\AmongUs"}' | Set-Content -LiteralPath (Join-Path $epic 'a.item')
'{"DisplayName":"Fortnite","InstallLocation":"E:\\Epic\\Fortnite"}' | Set-Content -LiteralPath (Join-Path $epic 'b.item')
'not json' | Set-Content -LiteralPath (Join-Path $epic 'c.item')
$installs = @(Get-EpicInstalls $epic)
Check ($installs.Count -eq 1 -and $installs[0] -eq 'E:\Epic\AmongUs') 'finds Among Us among Epic manifests and skips broken ones'

# Copying the bundle keeps existing settings
$game = Join-Path $root 'Among Us'
New-Item -ItemType Directory -Path (Join-Parts @($game, 'BepInEx', 'config')) -Force | Out-Null
New-Item -ItemType File -Path (Join-Path $game 'Among Us.exe') | Out-Null
Set-Content -LiteralPath (Join-Parts @($game, 'BepInEx', 'config', 'com.ljbutton.tournamenttracker.cfg')) -Value 'TournamentName = Mine'
$bundle = Join-Path $root 'bundle'
New-Item -ItemType Directory -Path (Join-Parts @($bundle, 'BepInEx', 'plugins')) -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Parts @($bundle, 'BepInEx', 'core')) -Force | Out-Null
Set-Content -LiteralPath (Join-Parts @($bundle, 'BepInEx', 'plugins', 'TournamentTracker.dll')) -Value 'new'
Set-Content -LiteralPath (Join-Parts @($bundle, 'BepInEx', 'core', 'BepInEx.Core.dll')) -Value 'core'
Set-Content -LiteralPath (Join-Path $bundle 'winhttp.dll') -Value 'doorstop'
Check (Test-AmongUsFolder $game) 'recognises the Among Us folder'
Check (-not (Test-AmongUsFolder $root)) 'rejects a folder without Among Us.exe'
Check (Install-Bundle $bundle $game) 'installs the bundle'
Check ((Get-Content -LiteralPath (Join-Parts @($game, 'BepInEx', 'config', 'com.ljbutton.tournamenttracker.cfg'))) -eq 'TournamentName = Mine') 'keeps the existing settings'
Set-Content -LiteralPath (Join-Parts @($bundle, 'BepInEx', 'plugins', 'TournamentTracker.dll')) -Value 'newer'
Install-Bundle $bundle $game | Out-Null
Check ((Get-Content -LiteralPath (Join-Parts @($game, 'BepInEx', 'plugins', 'TournamentTracker.dll'))) -eq 'newer') 'running it again updates the mod'

# Setup codes
$code = 'TT1-eyJtIjoicHJlbGltIiwiaWQiOiJvY3Qtc3VzIiwibiI6Ik9jdG9iZXIgcHJlbGltcyIsInNydiI6IlN1cyBTcXVhZCIsIndoIjoiaHR0cHM6Ly9kaXNjb3JkLmNvbS9hcGkvd2ViaG9va3MvMS94In0'
$parsed = Read-SetupCode ("  " + $code.Insert(12, "`n ") + "  ")
Check ($parsed -and $parsed.Description -eq 'October prelims (preliminary in Sus Squad)') 'reads a setup code, even with line breaks from copying'
Check ($parsed.Code -eq $code) 'keeps the code without the stray whitespace'
Check ($null -eq (Read-SetupCode 'hello')) 'rejects text that is not a setup code'
Check ($null -eq (Read-SetupCode 'TT1-bm90anNvbg')) 'rejects a damaged code'

Remove-Item -LiteralPath $root -Recurse -Force
if ($failures -gt 0) { Write-Host "$failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'All installer checks passed.'
