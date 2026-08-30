@echo off
setlocal
cd /d "%~dp0"

REM Passes all arguments to the guarded PowerShell load runner.
REM Start with -ValidateOnly; actual EC2 runs require local Prometheus and Grafana.
powershell -NoProfile -ExecutionPolicy Bypass ^
  -File "dev\loadtest\Invoke-LoadValidation.ps1" %*
exit /b %ERRORLEVEL%
