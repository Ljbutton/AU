@echo off
setlocal
title The Button - installer
rem Runs the PowerShell installer stored at the end of this file. Its own path goes to PowerShell
rem in a variable: inside the command, an apostrophe in a folder name (O'Brien) would end the quotes.
set "TT_INSTALLER_BAT=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f = Get-Content -Raw -LiteralPath $env:TT_INSTALLER_BAT; $i = $f.LastIndexOf('#' + 'PS-START'); Invoke-Expression $f.Substring($i)"
echo.
pause
exit /b
