@echo off
rem ============================================================================
rem  e2e-with-backup.bat - run build\test.bat behind an environment guard.
rem  ASCII-only on purpose: cmd.exe mangles non-ASCII batch files.
rem
rem  Why: build\test.bat really installs and really uninstalls, and both write
rem  user-level environment variables. This wrapper makes the mandatory
rem  "back up HKCU\Environment first" step from the hard rules impossible to
rem  forget:
rem
rem    1. reg export HKCU\Environment  -> a timestamped .reg backup
rem    2. run build\test.bat (any other args are forwarded, e.g. /full)
rem    3. reg export HKCU\Environment again and byte-compare against the backup
rem    4. if it changed, reg import the backup to restore it, then verify
rem    5. print the environment verdict as a conclusion line
rem
rem  CREDENTIAL SAFETY: HKCU\Environment holds real secrets in cleartext
rem  (e.g. ANTHROPIC_AUTH_TOKEN). A retained backup therefore is a plaintext
rem  credential file. So the backup exists ONLY for the duration of the run and
rem  is DELETED when the run finishes. Pass --keep-backup to retain it; that
rem  prints a plaintext-secret warning and lists the sensitive value names the
rem  file contains.
rem
rem  The change/no-change verdict does NOT need the plaintext: next to each
rem  .reg export a plaintext-free "value name + sha256" manifest is written
rem  (build\env-hash.ps1) and the manifests are compared too, so the decision
rem  can be reproduced without keeping any secret. The .reg files are still
rem  compared byte-for-byte with fc /b and are still what reg import restores
rem  from - those safety guarantees are unchanged.
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
rem          build\e2e-with-backup.bat /full --keep-backup
rem ============================================================================
rem  No delayed expansion: this script never needs !var! and literal "!!" in
rem  echo lines would otherwise be eaten by the delayed-expansion parser.
setlocal DisableDelayedExpansion

pushd "%~dp0.."
set "ROOT=%CD%"
popd

set "TEST=%ROOT%\build\test.bat"
set "HASHPS=%ROOT%\build\env-hash.ps1"
set "STAMP=%RANDOM%%RANDOM%"
set "BACKUP=%TEMP%\env-backup-%STAMP%.reg"
set "AFTER=%TEMP%\env-after-%STAMP%.reg"
set "BACKUPH=%TEMP%\env-backup-%STAMP%.hashes"
set "AFTERH=%TEMP%\env-after-%STAMP%.hashes"

rem ---- split our own --keep-backup switch out of the forwarded args ---------
rem  Each set is on its own line (no parenthesised block) so %FORWARD% re-expands
rem  every iteration even though delayed expansion is off.
set "KEEP="
set "FORWARD="
:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--keep-backup" goto parse_keep
set "FORWARD=%FORWARD% "%~1""
goto parse_next
:parse_keep
set "KEEP=1"
:parse_next
shift
goto parse
:parsed

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
call :make_manifest "%BACKUP%" "%BACKUPH%" BACKUPH_OK

rem ---------------------------------------------------------------- run test ---
echo.
call "%TEST%" %FORWARD%
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
  call :cleanup
  exit /b %TESTRC%
)
call :make_manifest "%AFTER%" "%AFTERH%" AFTERH_OK

rem Byte-exact comparison of the two exports (the authoritative check).
set "ENVSTATE=UNCHANGED"
set "DIFFERS=0"
fc /b "%BACKUP%" "%AFTER%" >nul 2>&1
if errorlevel 2 (
  set "ENVSTATE=COMPARE-ERROR"
  set "DIFFERS=1"
) else if errorlevel 1 (
  set "DIFFERS=1"
)

rem Plaintext-free cross-check: the name+sha256 manifests must agree too. This
rem proves the verdict does not depend on the retained plaintext.
if "%DIFFERS%"=="0" if "%BACKUPH_OK%%AFTERH_OK%"=="11" (
  fc /b "%BACKUPH%" "%AFTERH%" >nul 2>&1
  if errorlevel 1 set "DIFFERS=1"
)

if "%DIFFERS%"=="1" (
  if not "%ENVSTATE%"=="COMPARE-ERROR" (
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
  )
) else (
  echo  HKCU\Environment is byte-identical to the pre-run backup.
  echo  Plaintext-free manifests agree as well.
)

rem ---------------------------------------------------------------- summary ---
echo.
echo ============================================================
echo  E2E exit code      : %TESTRC%
echo  HKCU\Environment   : %ENVSTATE%
if "%KEEP%"=="1" (
  echo  backup file        : %BACKUP%  ^(KEPT^)
) else (
  echo  backup file        : ^(deleted^)
)
echo ============================================================

call :cleanup
exit /b %TESTRC%

rem ===========================================================================
rem  subroutines
rem ===========================================================================

rem make_manifest <reg-file> <manifest-out> <result-var>
rem  Writes a plaintext-free "name<TAB>sha256" manifest via build\env-hash.ps1.
rem  Non-fatal: if it cannot run, the byte-exact fc /b check still stands.
:make_manifest
set "%~3="
if not exist "%HASHPS%" (
  echo  NOTE: %HASHPS% missing - no plaintext-free manifest for %~nx1.
  exit /b 0
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%HASHPS%" -Reg %1 -Out %2 >nul 2>&1
if errorlevel 1 (
  echo  NOTE: manifest generation failed for %~nx1 - byte compare only.
  exit /b 0
)
set "%~3=1"
exit /b 0

rem cleanup
rem  Always removes the post-run export. The backup itself is removed unless
rem  --keep-backup was given; when kept, it is called out as a plaintext secret.
:cleanup
if exist "%AFTER%"  del "%AFTER%"  >nul 2>&1
if exist "%AFTERH%" del "%AFTERH%" >nul 2>&1
if "%KEEP%"=="1" goto cleanup_keep
if exist "%BACKUP%"  del "%BACKUP%"  >nul 2>&1
if exist "%BACKUPH%" del "%BACKUPH%" >nul 2>&1
echo  Backup deleted ^(pass --keep-backup to retain it^).
exit /b 0

:cleanup_keep
echo.
echo  !! WARNING: the kept backup holds PLAINTEXT SECRETS !!
echo  file: %BACKUP%
echo  HKCU\Environment is stored unencrypted, so this file exposes every
echo  token / key / credential kept there. Sensitive value names present:
if not exist "%BACKUPH%" (
  echo    ^(manifest unavailable - names not listed; inspect the file carefully^)
  goto cleanup_keep_foot
)
findstr /I /R "TOKEN SECRET PASSWORD PASSWD CREDENTIAL APIKEY _KEY AUTH PROXY" "%BACKUPH%" 2>nul
if errorlevel 1 echo    (none of the usual token/key/secret names were found)
:cleanup_keep_foot
echo  Delete it as soon as you are done. Do NOT commit, copy or share it.
exit /b 0
