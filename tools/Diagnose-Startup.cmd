@echo off
setlocal
rem Launch the bundled, reviewed diagnostic script in a NEW PowerShell process.
rem -ExecutionPolicy applies only to this process; no user/machine policy is modified.
rem No elevation is required. Run only from the official SimpleUnlockerNext archive.
set "SCRIPT=%~dp0Diagnose-Startup.ps1"
if not exist "%SCRIPT%" (
  echo ERROR: Diagnose-Startup.ps1 not found next to this file.
  pause
  exit /b 2
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
set "CODE=%ERRORLEVEL%"
echo.
if not "%CODE%"=="0" echo Diagnostic script exited with code %CODE%.
echo The report should appear as startup-diagnostics.txt in this directory.
echo Review the report before sharing; Windows events can include local paths.
pause
exit /b %CODE%
