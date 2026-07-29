@echo off
REM Runs the tests. Needs Docker running: the tests start a throwaway PostgreSQL.
cd /d "%~dp0dev\tests\IndieFi.Web.Tests"
dotnet test
echo.
echo [tests finished]
pause
