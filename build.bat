@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist app.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1

"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 ^
  /out:StickyNotes.exe ^
  /win32icon:app.ico /win32manifest:app.manifest ^
  /resource:app.ico,StickyNotes.app.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Xml.dll /r:System.Xml.Linq.dll ^
  src\*.cs
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Built StickyNotes.exe
