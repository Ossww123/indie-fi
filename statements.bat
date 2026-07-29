@echo off
REM Runs the load test under pg_stat_statements (step 4-b (2)): resets the accumulated
REM stats, runs the load, then prints the cumulative top-statements report.
REM No live poller needed - the stats are cumulative and read once at the end.
REM The app must ALREADY be running on http://localhost:5258.
REM
REM Args pass through to the seeder; with none it uses the same shape as 4-a:
REM   statements.bat
REM   statements.bat --users 100 --late-users 50 --keys 30 --opens-in 60
setlocal
cd /d "%~dp0"

echo [reset pg_stat_statements]
docker exec -i indiefi-db psql -U indiefi -d indiefi -qAt < dev\loadtest\diagnose\reset.sql

set SEED_ARGS=%*
if "%SEED_ARGS%"=="" set SEED_ARGS=--users 100 --late-users 50 --keys 30 --opens-in 60

call "%~dp0loadtest.bat" %SEED_ARGS%

echo.
echo [statements] load done - top statements by total DB time:
echo.
docker exec -i indiefi-db psql -U indiefi -d indiefi -P pager=off < dev\loadtest\diagnose\top.sql
endlocal
