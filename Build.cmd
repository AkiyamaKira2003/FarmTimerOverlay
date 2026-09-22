@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF"

"%CSC%" /nologo /target:winexe /optimize+ /win32icon:FarmTimerOverlay.ico /out:FarmTimerOverlay.exe ^
 /r:"%WPF%\PresentationCore.dll" ^
 /r:"%WPF%\PresentationFramework.dll" ^
 /r:"%WPF%\WindowsBase.dll" ^
 /r:System.Xaml.dll ^
 /r:System.Windows.Forms.dll ^
 /r:System.Drawing.dll ^
 FarmTimerOverlay.cs

if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)

echo Build complete: FarmTimerOverlay.exe
