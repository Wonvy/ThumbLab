@echo off
setlocal DisableDelayedExpansion
set "dll=%~dp0NcTapThumbnail.dll"
if not "%~1"=="" set "dll=%~f1"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Registration.ps1" -Action Install -DllPath "%dll%"
exit /b %errorlevel%
