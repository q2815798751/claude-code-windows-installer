@echo off
rem ============================================================================
rem  test.bat - end-to-end test for ClaudeCodeSetup.exe
rem  ASCII-only on purpose: cmd.exe mangles non-ASCII batch files.
rem
rem  Phase 1  sandbox install   : install into a throwaway dir, no system changes
rem  Phase 2  sandbox uninstall : prove removal is complete
rem  Phase 3  arm64 path        : force the online-download branch (needs github.com)
rem
rem  Usage:  test.bat            (phases 1-2)
rem          test.bat /full      (phases 1-3)
rem ============================================================================
setlocal EnableDelayedExpansion

pushd "%~dp0.."
set "ROOT=%CD%"
popd

set "EXE=%ROOT%\dist\ClaudeCodeSetup.exe"
set "WORK=%ROOT%\build\work"
set "SANDBOX=%WORK%\e2e-install"
set "OUT=%WORK%\e2e"

if not exist "%WORK%" mkdir "%WORK%"
if not exist "%OUT%" mkdir "%OUT%"

set "FAILED=0"

echo.
echo ============================================================
echo  Claude Code installer - end-to-end test
echo ============================================================

if not exist "%EXE%" (
  echo  ERROR: %EXE% not found. Run build.bat first.
  exit /b 1
)

rem ---------------------------------------------------------------- phase 1 ---
echo.
echo ----- Phase 1: sandbox install (no system changes) -----
if exist "%SANDBOX%" rmdir /s /q "%SANDBOX%"

"%EXE%" --silent-install --target-dir="%SANDBOX%" --no-system --report="%OUT%\phase1.txt"
set "RC=%ERRORLEVEL%"
echo    exit code: %RC%
if not "%RC%"=="0" set "FAILED=1"

echo.
echo    --- key result lines ---
findstr /C:"安装耗时" /C:"claude --version" /C:"验证结论" /C:"[FAIL]" /C:"安装失败" "%OUT%\phase1.txt"

echo.
echo    --- artifacts on disk ---
if exist "%SANDBOX%\claude.exe"     (echo    OK   claude.exe)     else (echo    MISS claude.exe & set "FAILED=1")
if exist "%SANDBOX%\launch.cmd"     (echo    OK   launch.cmd)     else (echo    MISS launch.cmd & set "FAILED=1")
if exist "%SANDBOX%\Uninstall.exe"  (echo    OK   Uninstall.exe)  else (echo    MISS Uninstall.exe & set "FAILED=1")
if exist "%SANDBOX%\manifest.json"  (echo    OK   manifest.json)  else (echo    MISS manifest.json & set "FAILED=1")
if exist "%SANDBOX%\install.log"    (echo    OK   install.log)    else (echo    MISS install.log & set "FAILED=1")

echo.
echo    --- manifest ---
if exist "%SANDBOX%\manifest.json" type "%SANDBOX%\manifest.json"

rem ---------------------------------------------------------------- phase 2 ---
echo.
echo ----- Phase 2: uninstall from sandbox -----
"%EXE%" --silent-uninstall --target-dir="%SANDBOX%" --keep-config --report="%OUT%\phase2.txt"
set "RC=%ERRORLEVEL%"
echo    exit code: %RC%

rem The exe under test lives outside the sandbox, so the uninstaller's
rem deferred self-delete is not involved here - expect the dir gone.
if exist "%SANDBOX%\claude.exe" (
  echo    FAIL claude.exe still present after uninstall
  set "FAILED=1"
) else (
  echo    OK   claude.exe removed
)
if exist "%SANDBOX%\Uninstall.exe" (
  echo    NOTE Uninstall.exe still present ^(expected: it was the running binary^)
) else (
  echo    OK   Uninstall.exe removed
)

rem ---------------------------------------------------------------- phase 3 ---
if /i "%~1"=="/full" (
  echo.
  echo ----- Phase 3: force the online-download branch ^(ARM64 code path^) -----
  echo    NOTE: this downloads ~96 MB from github.com and will be slow or fail
  echo          outright if github.com is unreachable from this network.
  if exist "%SANDBOX%" rmdir /s /q "%SANDBOX%"
  "%EXE%" --silent-install --target-dir="%SANDBOX%" --no-system --force-download --report="%OUT%\phase3.txt"
  set "RC=!ERRORLEVEL!"
  echo    exit code: !RC!
  echo.
  findstr /C:"下载" /C:"ARM64" /C:"SHA256" /C:"安装耗时" /C:"验证结论" /C:"失败" "%OUT%\phase3.txt"
  if exist "%SANDBOX%\claude.exe" (
    echo    OK   download branch produced claude.exe
    rmdir /s /q "%SANDBOX%"
  ) else (
    echo    NOTE download branch did not produce claude.exe ^(network-dependent^)
  )
)

rem --------------------------------------------------------------- summary ---
echo.
echo ============================================================
if "%FAILED%"=="0" (
  echo  E2E RESULT: PASS
) else (
  echo  E2E RESULT: FAIL
)
echo  reports: %OUT%
echo ============================================================
exit /b %FAILED%
