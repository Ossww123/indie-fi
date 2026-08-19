@echo off
REM Starts the complete local development stack from any working directory:
REM PostgreSQL, Mailpit, Redis, RabbitMQ, MinIO, migrations, Worker, and Web.
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"

set "ENV_FILE=dev\.env"
set "COMPOSE_FILE=dev\docker-compose.yml"
set "DOCKER_DESKTOP=%ProgramFiles%\Docker\Docker\Docker Desktop.exe"
set "CHECK_ONLY=false"

if /i "%~1"=="--check" set "CHECK_ONLY=true"
if not "%~1"=="" if /i not "%~1"=="--check" (
    echo [unknown option: %~1]
    echo   Usage: run.bat [--check]
    exit /b 2
)

REM Compose needs all four values before it can even resolve the local stack.
if not exist "%ENV_FILE%" (
    echo.
    echo [dev\.env is missing]
    echo   Copy dev\.env.example to dev\.env and fill in every password.
    pause
    exit /b 1
)

call :require_env_value POSTGRES_PASSWORD
if errorlevel 1 exit /b 1
call :require_env_value RABBITMQ_PASSWORD
if errorlevel 1 exit /b 1
call :require_env_value MINIO_ROOT_PASSWORD
if errorlevel 1 exit /b 1
call :require_env_value MINIO_APP_PASSWORD
if errorlevel 1 exit /b 1

where dotnet >NUL 2>&1
if errorlevel 1 (
    echo.
    echo [the .NET SDK was not found on PATH]
    pause
    exit /b 1
)

echo [checking Web and Worker user secrets]
powershell -NoProfile -ExecutionPolicy Bypass -File "dev\scripts\Test-LocalUserSecrets.ps1" -EnvironmentFile "%ENV_FILE%"
if errorlevel 1 (
    echo   Follow the local setup commands in dev\.env.example.
    pause
    exit /b 1
)

if /i "%CHECK_ONLY%"=="true" (
    echo [local configuration is ready]
    exit /b 0
)

REM Every container command fails while the Docker daemon is down, so start Docker
REM Desktop first and poll the daemon instead of guessing a fixed delay.
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

REM `docker compose up` creates missing services and also applies changed images,
REM ports, health checks, and loopback bindings to existing containers. `docker
REM start indiefi-db` would keep stale container settings indefinitely.
docker compose --env-file "%ENV_FILE%" -f "%COMPOSE_FILE%" up -d db mailpit redis rabbitmq minio
if errorlevel 1 (
    echo.
    echo [could not create or start the local infrastructure]
    echo   Check Docker output and the required values in dev\.env.
    pause
    exit /b 1
)

call :wait_container indiefi-db PostgreSQL
if errorlevel 1 exit /b 1
call :wait_container indiefi-mailpit Mailpit
if errorlevel 1 exit /b 1
call :wait_container indiefi-redis Redis
if errorlevel 1 exit /b 1
call :wait_container indiefi-rabbitmq RabbitMQ
if errorlevel 1 exit /b 1
call :wait_container indiefi-minio MinIO
if errorlevel 1 exit /b 1

REM Re-run the idempotent one-shot setup after MinIO is healthy. This verifies the
REM private bucket and least-privilege application user on every local startup.
echo [initializing the local MinIO bucket]
docker compose --env-file "%ENV_FILE%" -f "%COMPOSE_FILE%" run --rm --no-deps minio-init
if errorlevel 1 (
    echo.
    echo [MinIO bucket initialization failed]
    pause
    exit /b 1
)

REM Build once so migration, Worker, and Web can all use the same checked output.
echo [building the solution]
dotnet build dev\IndieFi.slnx
if errorlevel 1 (
    echo.
    echo [build failed]
    pause
    exit /b 1
)

REM Migrations must use the PostgreSQL owner. Preserve any inherited application
REM connection string, override it only for EF, then restore it before app startup.
set "LOCAL_POSTGRES_PASSWORD="
for /f "usebackq eol=# tokens=1,* delims==" %%a in ("%ENV_FILE%") do if /i "%%a"=="POSTGRES_PASSWORD" set "LOCAL_POSTGRES_PASSWORD=%%b"
set "ORIGINAL_DATABASE_CONNECTION=%ConnectionStrings__Database%"
set "ConnectionStrings__Database=Host=localhost;Port=5432;Database=indiefi;Username=indiefi;Password=%LOCAL_POSTGRES_PASSWORD%"

echo [applying database migrations as indiefi]
dotnet ef database update --no-build --project dev\src\IndieFi.Web
set "MIGRATION_EXIT=%ERRORLEVEL%"
set "ConnectionStrings__Database=%ORIGINAL_DATABASE_CONNECTION%"
set "ORIGINAL_DATABASE_CONNECTION="
set "LOCAL_POSTGRES_PASSWORD="
if not "%MIGRATION_EXIT%"=="0" (
    echo.
    echo [migration failed]
    echo   Check POSTGRES_PASSWORD and the database migration output above.
    pause
    exit /b 1
)
set "MIGRATION_EXIT="

REM Worker owns RabbitMQ consumers, notification delivery, and media processing.
REM Keep its logs visible in a separate development terminal.
echo [starting Worker in a separate window]
start "IndieFi Worker" /D "%~dp0dev\src\IndieFi.Worker" cmd /k "dotnet run --no-build --launch-profile IndieFi.Worker"
if errorlevel 1 (
    echo.
    echo [could not open the Worker window]
    pause
    exit /b 1
)

echo [starting Web at http://localhost:5258]
cd /d "%~dp0dev\src\IndieFi.Web"
dotnet run --no-build --launch-profile http
set "APP_EXIT=%ERRORLEVEL%"

echo.
if not "%APP_EXIT%"=="0" echo [Web stopped with exit code %APP_EXIT%]
if "%APP_EXIT%"=="0" echo [Web stopped]
echo [close the IndieFi Worker window when local development is finished]
pause
exit /b %APP_EXIT%

:require_env_value
findstr /r /b /c:"%~1=..*" "%ENV_FILE%" >NUL
if not errorlevel 1 exit /b 0
echo.
echo [dev\.env is missing a value for %~1]
echo   Copy the corresponding local secret into dev\.env and run this again.
pause
exit /b 1

:wait_container
set "WAIT_CONTAINER=%~1"
set "WAIT_LABEL=%~2"
set "CONTAINER_STATE="
set /a WAITED=0
:wait_container_loop
for /f "usebackq" %%h in (`docker inspect -f "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}" "%WAIT_CONTAINER%" 2^>NUL`) do set "CONTAINER_STATE=%%h"
if /i "%CONTAINER_STATE%"=="healthy" goto container_ready
if /i "%CONTAINER_STATE%"=="running" goto container_ready
ping -n 2 127.0.0.1 >NUL
set /a WAITED+=1
if %WAITED% LSS 120 goto wait_container_loop
echo.
echo [%WAIT_LABEL% did not become ready within 2 minutes]
docker logs --tail 30 "%WAIT_CONTAINER%"
pause
exit /b 1

:container_ready
echo [%WAIT_LABEL% is up]
exit /b 0
