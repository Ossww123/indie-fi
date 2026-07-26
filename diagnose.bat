@echo off
REM Runs the key-drop load test WITH two read-only diagnostic pollers (step 4-b).
REM Opens a summary poller and a detail poller in their own windows, then runs the
REM normal load test. The app must ALREADY be running in its own terminal on
REM http://localhost:5258 (see dev\loadtest\diagnose\README.md).
REM
REM Args pass straight through to the seeder. With no args it uses the same shape as
REM the 4-a run so results stay comparable:
REM   diagnose.bat
REM   diagnose.bat --users 100 --late-users 50 --keys 30 --opens-in 60
setlocal
cd /d "%~dp0"

set DIAG=dev\loadtest\diagnose

set SEED_ARGS=%*
if "%SEED_ARGS%"=="" set SEED_ARGS=--users 100 --late-users 50 --keys 30 --opens-in 60

echo [starting pollers in separate windows]
start "summary poller" powershell -NoProfile -ExecutionPolicy Bypass -File "%DIAG%\sample.ps1" -Count 1500
start "detail poller"  powershell -NoProfile -ExecutionPolicy Bypass -File "%DIAG%\sample.ps1" -Query detail -Count 1500

call "%~dp0loadtest.bat" %SEED_ARGS%

echo.
echo [diagnose] load finished. Close the two poller windows when done, then tell Claude.
echo            dumps: %DIAG%\dump-summary.txt  and  %DIAG%\dump-detail.txt
endlocal
