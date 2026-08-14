@echo off
REM Runs the transitional email protection backfill as the database owner.
REM Encryption and lookup keys come from the web project's shared User Secrets.
setlocal
cd /d "%~dp0\..\..\.."

if not exist "dev\.env" (
    echo.
    echo [dev\.env is missing]
    echo   Copy dev\.env.example to dev\.env and fill in POSTGRES_PASSWORD.
    exit /b 1
)

for /f "usebackq eol=# tokens=1,* delims==" %%a in ("dev\.env") do set "%%a=%%b"
set "ConnectionStrings__MigrationDatabase=Host=localhost;Port=5432;Database=indiefi;Username=indiefi;Password=%POSTGRES_PASSWORD%"

docker start indiefi-db >NUL
if errorlevel 1 (
    echo.
    echo [could not start the indiefi-db container]
    exit /b 1
)

dotnet run --project dev\tools\IndieFi.AccountMigration -- %*
