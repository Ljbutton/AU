@echo off
title Among Us Tournament Tracker - installer
rem Runs the PowerShell installer stored at the end of this file.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f = Get-Content -Raw -LiteralPath '%~f0'; $i = $f.LastIndexOf('#' + 'PS-START'); Invoke-Expression $f.Substring($i)"
echo.
pause
exit /b
