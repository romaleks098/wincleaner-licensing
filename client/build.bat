@echo off
setlocal
cd /d "%~dp0"
title WinCleaner - Auto Compiler and Obfuscation Pipeline

echo ========================================================
echo   WinCleaner - Compiler and Obfuscation Pipeline
echo   Maximum Protection [Ed25519 + HWID + ConfuserEx]
echo ========================================================

set "CSC="
if exist "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe" set "CSC=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
if not defined CSC if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe" set "CSC=C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
if not defined CSC if exist "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe" set "CSC=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if not defined CSC if exist "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not defined CSC (
    echo [ERROR] C# compiler csc.exe not found on this system.
    pause
    exit /b 1
)

if not exist "Program.cs" (
    echo [ERROR] Program.cs not found in %cd%
    pause
    exit /b 1
)

if not exist "Licensing.cs" (
    echo [ERROR] Licensing.cs not found in %cd%
    pause
    exit /b 1
)

set "EXTRA_FLAGS="
if exist "app.manifest" set "EXTRA_FLAGS=%EXTRA_FLAGS% /win32manifest:app.manifest"
if exist "app.ico" set "EXTRA_FLAGS=%EXTRA_FLAGS% /win32icon:app.ico"

taskkill /F /IM WinCleaner.exe >nul 2>&1
taskkill /F /IM WinCleaner_raw.exe >nul 2>&1

echo [*] Compiler: "%CSC%"
echo [1/3] Compiling source code (Program.cs + Licensing.cs)...

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /utf8output %EXTRA_FLAGS% /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Numerics.dll,System.Security.dll /out:"WinCleaner_raw.exe" "Program.cs" "Licensing.cs"

if errorlevel 1 (
    echo [ERROR] Compilation failed. Check compiler errors above.
    pause
    exit /b 1
)

echo [+] Stage 1 complete: WinCleaner_raw.exe built successfully.

set "CONFUSER_CLI=%~dp0tools\confuser\Confuser.CLI.exe"
if not exist "%CONFUSER_CLI%" goto NO_OBFUSCATION
if not exist "%~dp0obfuscate.crproj" goto NO_OBFUSCATION

echo [2/3] Applying maximum ConfuserEx obfuscation...
echo       Protections: Anti-Tamper, Constants Encrypt, Ctrl Flow, Anti-Debug, Anti-Dump, Rename

if exist "%~dp0Obfuscated" rmdir /S /Q "%~dp0Obfuscated" >nul 2>&1
"%CONFUSER_CLI%" -n "%~dp0obfuscate.crproj"

if not exist "%~dp0Obfuscated\WinCleaner_raw.exe" goto OBF_FAILED

echo [3/3] Finalizing protected executable...
if exist "%~dp0WinCleaner.exe" del /F /Q "%~dp0WinCleaner.exe" >nul 2>&1
move /Y "%~dp0Obfuscated\WinCleaner_raw.exe" "%~dp0WinCleaner.exe" >nul
if exist "%~dp0WinCleaner_raw.exe" del /F /Q "%~dp0WinCleaner_raw.exe" >nul 2>&1
if exist "%~dp0Obfuscated" rmdir /S /Q "%~dp0Obfuscated" >nul 2>&1

echo.
echo ========================================================
echo  [+] SUCCESS: WinCleaner.exe is fully compiled and obfuscated!
echo  [+] Anti-Crack Protections Active:
echo      - Ed25519 Elliptic Curve Signature Verification
echo      - Hardware-bound ID [Volume + MachineGuid + CPU]
echo      - Windows DPAPI Protected Token Storage
echo      - LicenseGuard Core Feature Assertions
echo      - Anti-Debug Watchdog and Analysis Tools Blocker
echo      - Anti-Clock Rollback Tamper Detection
echo      - ConfuserEx Control Flow Flattening and Method Encryption
echo ========================================================
goto END

:OBF_FAILED
echo [!] Warning: Obfuscator finished without expected output.
move /Y "%~dp0WinCleaner_raw.exe" "%~dp0WinCleaner.exe" >nul
echo [+] Kept un-obfuscated build as WinCleaner.exe
goto END

:NO_OBFUSCATION
echo [!] Confuser CLI not found. Skipping obfuscation step.
move /Y "%~dp0WinCleaner_raw.exe" "%~dp0WinCleaner.exe" >nul
echo [+] Output: %~dp0WinCleaner.exe

:END
echo.
echo Ready for distribution: %~dp0WinCleaner.exe
pause