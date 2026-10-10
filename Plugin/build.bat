@echo off
echo Building TGYHTurkishPatch...
echo.
cd /d "%~dp0"
dotnet build TGYHTurkishPatch.csproj -c Release
echo.
if %ERRORLEVEL% EQU 0 (
    echo Build successful!
    echo DLL location: ..\..\BepInEx\plugins\TGYHTurkishPatch\TGYHTurkishPatch.dll
) else (
    echo Build failed!
)
echo.
pause
