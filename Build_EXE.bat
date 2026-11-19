@echo off
chcp 65001 >nul
setlocal
set "DIR=%~dp0"
set "CSC64=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "CSC32=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if exist "%CSC64%" (
    set "CSC=%CSC64%"
    set "PLAT=x64"
) else if exist "%CSC32%" (
    set "CSC=%CSC32%"
    set "PLAT=x86"
) else (
    echo [Error] .NET Framework 4.x csc.exe not found on this Windows machine.
    pause
    exit /b 1
)

echo Compiling GameMuteHelper.exe (%PLAT%) via Windows built-in csc.exe ...
"%CSC%" /nologo /target:winexe /optimize+ /platform:%PLAT% /utf8output ^
    /win32manifest:"%DIR%app.manifest" ^
    /out:"%DIR%GameMuteHelper.exe" ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Runtime.Serialization.dll ^
    "%DIR%GameMuteHelper.cs"

if errorlevel 1 (
    echo.
    echo [Failed] Compilation error occurred.
    pause
    exit /b 1
)

echo.
echo [Success] Built %DIR%GameMuteHelper.exe
start "" "%DIR%GameMuteHelper.exe"
endlocal
