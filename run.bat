@echo off
REM Runs the web app. Works no matter which folder you start it from.
cd /d "%~dp0dev\src\IndieFi.Web"
dotnet run
echo.
echo [app stopped]
pause
