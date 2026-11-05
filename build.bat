@echo off
setlocal
cd /d "%~dp0"

"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize+ /target:winexe /out:BtAudioSwitcher.exe /win32icon:app.ico /resource:app.ico,app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll Program.cs
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built BtAudioSwitcher.exe

rem Signing is optional: set SIGN_THUMBPRINT to the certificate's thumbprint.
rem SIGN_TIMESTAMP_URL overrides the timestamp server, usually your CA's own.
if not defined SIGN_THUMBPRINT (
    echo Not signed. Set SIGN_THUMBPRINT to sign.
    exit /b 0
)
if not defined SIGN_TIMESTAMP_URL set "SIGN_TIMESTAMP_URL=http://timestamp.digicert.com"

set "SIGNTOOL="
for /f "delims=" %%i in ('where signtool.exe 2^>nul') do set "SIGNTOOL=%%i"
if not defined SIGNTOOL for /f "delims=" %%i in ('dir /b /s "%ProgramFiles(x86)%\Windows Kits\10\bin\signtool.exe" 2^>nul ^| findstr /i "\\x64\\"') do set "SIGNTOOL=%%i"
if not defined SIGNTOOL (
    echo signtool.exe not found. Install the Windows SDK signing tools.
    exit /b 1
)

"%SIGNTOOL%" sign /sha1 %SIGN_THUMBPRINT% /fd SHA256 /tr %SIGN_TIMESTAMP_URL% /td SHA256 BtAudioSwitcher.exe
if errorlevel 1 (
    echo Signing failed.
    exit /b 1
)
echo Signed BtAudioSwitcher.exe
