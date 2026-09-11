@echo off
rem ============================================================================
rem  build.bat - build ClaudeCodeSetup.exe, the one-click Claude Code installer.
rem  ASCII-only on purpose: cmd.exe mangles non-ASCII batch files.
rem
rem  Produces:  dist\ClaudeCodeSetup.exe   (stub + official payload + trailer)
rem  Requires:  payload\claude-win32-x64.zip  and  payload\SHASUMS256.txt
rem  Toolchain: in-box csc.exe (.NET Framework 4.x) - no third-party deps.
rem ============================================================================
setlocal EnableDelayedExpansion

pushd "%~dp0.."
set "ROOT=%CD%"
popd

set "SRC=%ROOT%\src"
set "BLD=%ROOT%\build"
set "PAY=%ROOT%\payload"
set "DIST=%ROOT%\dist"
set "WORK=%BLD%\work"
set "PS1=%BLD%\build.ps1"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%WORK%" mkdir "%WORK%"
if not exist "%DIST%" mkdir "%DIST%"

echo.
echo ============================================================
echo  Claude Code one-click installer - build
echo ============================================================
echo  ROOT : %ROOT%
echo.

rem ---------------------------------------------------------------- step 1 ----
echo [1/6] Checking toolchain...
if not exist "%CSC%" (
  echo   ERROR: csc.exe not found - .NET Framework 4.x is required.
  exit /b 1
)
if not exist "%SRC%\app.manifest" (
  echo   ERROR: %SRC%\app.manifest missing.
  exit /b 1
)
echo   csc: %CSC%

rem ---------------------------------------------------------------- step 2 ----
echo.
echo [2/6] Verifying official payload and generating BuildInfo.cs...
if not exist "%PAY%\SHASUMS256.txt" (
  echo   ERROR: %PAY%\SHASUMS256.txt missing.
  echo   Fetch it from the official release page.
  exit /b 1
)

set "ASSET=claude-win32-x64.zip"
if not exist "%PAY%\%ASSET%" (
  echo   ERROR: %PAY%\%ASSET% missing.
  echo   Download: https://github.com/anthropics/claude-code/releases
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -Action prep ^
  -Root "%ROOT%" -Payload "%PAY%\%ASSET%" -ShaFile "%PAY%\SHASUMS256.txt" -WorkDir "%WORK%"
if errorlevel 1 (
  echo   ERROR: prep failed - payload integrity check did not pass.
  exit /b 1
)
call "%WORK%\build.vars.bat"
echo   release: %RELEASE_TAG%   binary: %BINARY_BYTES% bytes
echo   payload SHA256: %PAYLOAD_SHA256%
echo   binary  SHA256: %BINARY_SHA256%

rem ---------------------------------------------------------------- step 3 ----
echo.
echo [3/6] Compiling Uninstall.exe...
"%CSC%" /nologo /target:winexe /platform:anycpu /codepage:65001 ^
  /out:"%WORK%\Uninstall.exe" ^
  /win32manifest:"%SRC%\app.manifest" ^
  /nowarn:0649 /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  "%SRC%\Uninstaller.cs" "%SRC%\Common.cs" "%SRC%\Installer.cs" "%SRC%\BuildInfo.cs"
if errorlevel 1 (
  echo   ERROR: Uninstall.exe compilation failed.
  exit /b 1
)
echo   ok: %WORK%\Uninstall.exe

rem ---------------------------------------------------------------- step 4 ----
echo.
echo [4/6] Compiling installer stub...
"%CSC%" /nologo /target:winexe /platform:anycpu /codepage:65001 ^
  /out:"%WORK%\stub.exe" ^
  /win32manifest:"%SRC%\app.manifest" ^
  /resource:"%WORK%\Uninstall.exe",Uninstall.exe ^
  /nowarn:0649 /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  "%SRC%\Program.cs" "%SRC%\MainForm.cs" "%SRC%\Checks.cs" "%SRC%\Installer.cs" ^
  "%SRC%\Common.cs" "%SRC%\BuildInfo.cs"
if errorlevel 1 (
  echo   ERROR: installer stub compilation failed.
  exit /b 1
)
echo   ok: %WORK%\stub.exe

rem ---------------------------------------------------------------- step 5 ----
echo.
echo [5/6] Packing stub + payload + trailer...
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -Action pack ^
  -Stub "%WORK%\stub.exe" -Payload "%PAY%\%ASSET%" -Out "%DIST%\ClaudeCodeSetup.exe"
if errorlevel 1 (
  echo   ERROR: packing failed.
  exit /b 1
)

rem ---------------------------------------------------------------- step 6 ----
echo.
echo [6/6] Self-verifying packed installer...
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -Action verify ^
  -Out "%DIST%\ClaudeCodeSetup.exe" -ExpectPayloadSha "%PAYLOAD_SHA256%"
if errorlevel 1 (
  echo   ERROR: self-verification failed.
  exit /b 1
)

for %%F in ("%DIST%\ClaudeCodeSetup.exe") do set "OUTSIZE=%%~zF"
echo.
echo ============================================================
echo  BUILD OK
echo ============================================================
echo  artifact : %DIST%\ClaudeCodeSetup.exe
echo  size     : %OUTSIZE% bytes
echo  version  : %RELEASE_TAG%
echo.
echo  Next: run the end-to-end test
echo        build\test.bat
echo.
exit /b 0
