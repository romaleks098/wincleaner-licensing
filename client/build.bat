@echo off
chcp 65001 >nul
echo [INFO] Сборка WinCleaner.exe...

set "CSC=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
if not exist "%CSC%" set "CSC=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

echo [INFO] Используется компилятор: %CSC%

"%CSC%" /target:winexe /win32manifest:app.manifest /win32icon:app.ico /optimize+ /debug- /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Numerics.dll,System.Security.dll /out:WinCleaner.exe WinCleaner.cs Licensing.cs

if %ERRORLEVEL% EQU 0 (
    echo [УСПЕХ] WinCleaner.exe успешно скомпилирован!
) else (
    echo [ОШИБКА] Сборка завершилась с ошибкой!
)
