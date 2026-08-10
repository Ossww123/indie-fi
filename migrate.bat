@echo off
REM The app now connects as the restricted indiefi_app role, which cannot create
REM or alter tables. Migrations therefore run as the indiefi owner role.
REM User Secrets contains the application-role connection string, so this script
REM overrides it through the environment (environment variables take precedence).
REM Optional args are passed straight to dotnet ef.
setlocal
cd /d "%~dp0"

REM The database password is not kept in the repository. dev\.env holds it locally
REM and is not committed, so read it here and build the owner connection string.
if not exist "dev\.env" (
    echo.
    echo [dev\.env is missing]
    echo   Copy dev\.env.example to dev\.env and fill in POSTGRES_PASSWORD.
    pause
    exit /b 1
)
for /f "usebackq eol=# tokens=1,* delims==" %%a in ("dev\.env") do set "%%a=%%b"
set "ConnectionStrings__Database=Host=localhost;Port=5432;Database=indiefi;Username=indiefi;Password=%POSTGRES_PASSWORD%"

docker start indiefi-db

echo.
echo [applying database migrations as indiefi]
dotnet ef database update --project dev\src\IndieFi.Web %*
if errorlevel 1 (
    echo.
    echo [migration failed - is Docker running and is POSTGRES_PASSWORD correct?]
    pause
    exit /b 1
)

echo.
echo [database migrations finished]
pause
