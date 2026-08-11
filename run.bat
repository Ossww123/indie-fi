@echo off
REM Runs the web app together with the database it needs, so one script brings the
REM whole local stack up. Works no matter which folder you start it from.
setlocal
cd /d "%~dp0"

set "DOCKER_DESKTOP=%ProgramFiles%\Docker\Docker\Docker Desktop.exe"

REM Every container command fails while the Docker daemon is down, so deal with
REM that first instead of letting `docker start` report a confusing error.
docker info >NUL 2>&1
if not errorlevel 1 goto daemon_ready

if not exist "%DOCKER_DESKTOP%" (
    echo.
    echo [Docker Desktop was not found]
    echo   Looked for: %DOCKER_DESKTOP%
    echo   Start Docker yourself, then run this again.
    pause
    exit /b 1
)

echo [starting Docker Desktop - this takes a moment]
start "" "%DOCKER_DESKTOP%"

REM The daemon keeps refusing commands for a while after the process appears, so
REM poll it rather than guessing a fixed wait.
REM `ping` is the sleep here because `timeout` reads the keyboard: it aborts with
REM "Input redirection is not supported" whenever this script is piped or scripted.
set /a WAITED=0
:wait_daemon
ping -n 3 127.0.0.1 >NUL
docker info >NUL 2>&1
if not errorlevel 1 goto daemon_ready
set /a WAITED+=2
if %WAITED% LSS 120 goto wait_daemon
echo.
echo [Docker did not start within 2 minutes]
pause
exit /b 1

:daemon_ready
echo [docker is up]

docker start indiefi-db >NUL
if errorlevel 1 (
    echo.
    echo [could not start the indiefi-db container]
    echo   If it was never created, run: docker compose -f dev\docker-compose.yml up -d
    pause
    exit /b 1
)

REM The container turns "healthy" only once PostgreSQL accepts connections, so
REM waiting on that keeps the app's first query from hitting a closed port.
set /a WAITED=0
:wait_database
for /f "usebackq" %%h in (`docker inspect -f "{{.State.Health.Status}}" indiefi-db 2^>NUL`) do set "DBHEALTH=%%h"
if /i "%DBHEALTH%"=="healthy" goto database_ready
ping -n 2 127.0.0.1 >NUL
set /a WAITED+=1
if %WAITED% LSS 60 goto wait_database
echo.
echo [the database did not become healthy within 60 seconds]
pause
exit /b 1

:database_ready
echo [database is up]

cd /d "%~dp0dev\src\IndieFi.Web"
dotnet run
echo.
echo [app stopped]
pause
