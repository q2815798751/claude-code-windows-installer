@echo off
rem ============================================================================
rem  e2e-with-backup.bat - run build\test.bat behind an environment guard.
rem  ASCII-only on purpose: cmd.exe mangles non-ASCII batch files.
rem
rem  Why: build\test.bat really installs and really uninstalls. Until AW-1 is
rem  fixed the uninstall path unconditionally deletes the user-level
rem  CLAUDE_CODE_GIT_BASH_PATH variable. This wrapper makes the mandatory
rem  "back up HKCU\Environment first" step from the hard rules impossible to
rem  forget:
rem
rem    1. reg export HKCU\Environment  -> a timestamped .reg backup
rem    2. run build\test.bat (any args are forwarded, e.g. /full)
rem    3. reg export HKCU\Environment again and byte-compare against the backup
rem    4. if it changed, reg import the backup to restore it, then verify
rem    5. print the environment verdict as a conclusion line
rem
rem  Exit code is the exit code of build\test.bat, so callers still see the E2E
rem  result. A failed backup aborts before the test runs.
rem
rem  Limitation: reg import restores values that were deleted or overwritten,
rem  but it cannot remove values that were newly added during the run. The
rem  current test flow only deletes, so this is sufficient; a future test that
rem  adds keys would need a mirror-restore.
rem
rem  Usage:  build\e2e-with-backup.bat
rem          build\e2e-with-backup.bat /full
rem ============================================================================
rem  No delayed expansion: this script never needs !var! and literal "!!" in
rem  echo lines would otherwise be eaten by the delayed-expansion parser.
setlocal DisableDelayedExpansion

pushd "%~dp0.."
set "ROOT=%CD%"
popd

set "TEST=%ROOT%\build\test.bat"
set "STAMP=%RANDOM%%RANDOM%"
set "BACKUP=%TEMP%\env-backup-%STAMP%.reg"
set "AFTER=%TEMP%\env-after-%STAMP%.reg"

echo.
echo ============================================================
echo  E2E with HKCU\Environment backup guard
echo ============================================================
echo  root   : %ROOT%
echo  backup : %BACKUP%
echo.

if not exist "%TEST%" (
  echo  ERROR: %TEST% not found.
  exit /b 1
)

rem ---------------------------------------------------------------- backup ----
reg export "HKCU\Environment" "%BACKUP%" /y >nul 2>&1
if errorlevel 1 (
  echo  ERROR: failed to back up HKCU\Environment.
  echo         Refusing to run the E2E test without a backup.
  exit /b 1
)
echo  OK: HKCU\Environment backed up.

rem ---------------------------------------------------------------- run test ---
echo.
call "%TEST%" %*
set "TESTRC=%ERRORLEVEL%"

rem ---------------------------------------------------------------- compare ---
echo.
echo ----- checking whether HKCU\Environment changed -----
reg export "HKCU\Environment" "%AFTER%" /y >nul 2>&1
if errorlevel 1 (
  echo  ERROR: failed to re-export HKCU\Environment after the test.
  echo         Restoring from backup to be safe...
  reg import "%BACKUP%" >nul 2>&1
  echo  ENV RESULT: UNKNOWN-RESTORED-FROM-BACKUP
  echo  backup    : %BACKUP%
  exit /b %TESTRC%
)

set "ENVSTATE=UNCHANGED"
fc /b "%BACKUP%" "%AFTER%" >nul 2>&1
if errorlevel 2 (
  set "ENVSTATE=COMPARE-ERROR"
) else if errorlevel 1 (
  echo.
  echo  !! HKCU\Environment CHANGED during the E2E run !!
  echo  Restoring from backup: %BACKUP%
  reg import "%BACKUP%" >nul 2>&1
  if errorlevel 1 (
    set "ENVSTATE=CHANGED-RESTORE-FAILED"
    echo  !! RESTORE FAILED - manual recovery needed from the backup above.
  ) else (
    echo  Environment restored. Verifying against the backup...
    reg export "HKCU\Environment" "%AFTER%" /y >nul 2>&1
    fc /b "%BACKUP%" "%AFTER%" >nul 2>&1
    if errorlevel 1 (
      set "ENVSTATE=CHANGED-RESTORE-UNVERIFIED"
      echo  !! Restore verification FAILED - inspect the backup above.
    ) else (
      set "ENVSTATE=CHANGED-RESTORED"
      echo  Restore verified: current HKCU\Environment matches the backup.
    )
  )
) else (
  echo  HKCU\Environment is byte-identical to the pre-run backup.
)

rem ---------------------------------------------------------------- summary ---
echo.
echo ============================================================
echo  E2E exit code      : %TESTRC%
echo  HKCU\Environment   : %ENVSTATE%
echo  backup file        : %BACKUP%
echo ============================================================

if exist "%AFTER%" del "%AFTER%" >nul 2>&1
exit /b %TESTRC%
