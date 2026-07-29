@echo off
REM Runs the key-drop load test: seeds a fresh drop, then fires k6 right away.
REM The app must already be running in another terminal on http://localhost:5258
REM (start it with `dotnet run --launch-profile http`). Docker must be running.
REM
REM Optional args are passed straight to the seeder, e.g.:
REM   loadtest.bat --users 500 --late-users 200 --keys 100 --opens-in 90
setlocal
cd /d "%~dp0"

docker start indiefi-db

REM The app is NOT started here - it must already be running in its own terminal.
REM Check it now, before seeding, so a dead app fails loudly instead of showing up
REM as a wall of k6 "connection refused".
curl -s -o NUL --max-time 3 http://localhost:5258/
if errorlevel 1 (
    echo.
    echo [app is not answering on http://localhost:5258]
    echo   Start it first in another terminal:
    echo     cd dev\src\IndieFi.Web ^&^& dotnet run --launch-profile http
    echo   Wait for "Now listening on: http://localhost:5258", then run this again.
    pause
    exit /b 1
)
echo [app is up on 5258]

set SEED_ARGS=%*
if "%SEED_ARGS%"=="" set SEED_ARGS=--users 500 --late-users 200 --keys 100 --opens-in 90

echo.
echo [seeding a fresh drop]
dotnet run --project dev\tools\IndieFi.Seed -- %SEED_ARGS%
if errorlevel 1 (
    echo.
    echo [seed failed - is the database up? is the app running on 5258?]
    pause
    exit /b 1
)

echo.
echo [running k6 - claims fire when the drop opens]
k6 run dev\loadtest\keydrop.js

echo.
echo [load test finished] see dev\loadtest\results\last-run.json
pause
