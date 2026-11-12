@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist app.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1

rem Optional: the OAuth client JSON from Google Cloud Console, built in so other PCs need no setup.
set "CLIENT_RES="
if exist google-client.json (
  set "CLIENT_RES=/resource:google-client.json,StickyNotes.google-client.json"
  echo Embedding google-client.json
)

"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 ^
  /out:StickyNotes.exe ^
  /win32icon:app.ico /win32manifest:app.manifest ^
  /resource:app.ico,StickyNotes.app.ico %CLIENT_RES% ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Xml.dll /r:System.Xml.Linq.dll ^
  /r:System.Web.Extensions.dll /r:System.Security.dll ^
  src\*.cs
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Built StickyNotes.exe
