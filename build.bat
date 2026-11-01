@echo off
setlocal
rem Rebuilds the plugin and copies it to Pulsar as plugin.dll.
rem The copy is done by the DeployToPulsar target in WheelFrictionFix.csproj.

cd /d "%~dp0"

dotnet build -c Release --no-incremental
if errorlevel 1 (
    echo.
    echo BUILD FAILED.
    pause
    exit /b 1
)

echo.
echo Done: %APPDATA%\Pulsar\Legacy\Local\WheelFrictionFix\plugin.dll
pause
