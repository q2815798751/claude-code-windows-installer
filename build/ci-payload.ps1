# ci-payload.ps1 - create a tiny synthetic payload so CI can exercise the full
# build pipeline (build\build.bat) and the installer's --selftest on a clean
# Windows runner without downloading the real ~95 MB claude-win32-x64.zip.
#
# Why: fetching the real payload is a network dependency (slow, flaky) and CI
# must stay deterministic. The synthetic payload is a valid zip that contains a
# single small "claude.exe" entry, plus a matching SHASUMS256.txt, so
# build.ps1 prep/pack/verify and the installer's payload check (trailer magic +
# zip entry + region SHA256) all run for real. Real payload integrity is still
# enforced by build\build.bat whenever the real payload is present - it refuses
# to build on a SHA256 mismatch against payload\SHASUMS256.txt.
#
# Safety: if a payload already exists it is left untouched unless -Force is
# given, so running this by hand on a dev box cannot clobber the real payload.
#
# ASCII-only on purpose: Windows PowerShell 5.1 mis-decodes non-BOM UTF-8.
param([switch]$Force)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path $PSScriptRoot -Parent
$payDir = Join-Path $root 'payload'
$zipPath = Join-Path $payDir 'claude-win32-x64.zip'
$shaPath = Join-Path $payDir 'SHASUMS256.txt'

New-Item -ItemType Directory -Path $payDir -Force | Out-Null

if ((Test-Path $zipPath) -and (-not $Force)) {
    Write-Host "payload already present: $zipPath - leaving it untouched (use -Force to replace)"
    exit 0
}

# Build a minimal zip whose only entry is named exactly claude.exe (build.ps1
# extracts the entry whose Name is 'claude.exe').
$stage = Join-Path $env:TEMP ("ci-synth-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $stage 'claude.exe'),
    "synthetic payload for CI - not a real claude.exe`n",
    (New-Object Text.UTF8Encoding $false))

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath)
Remove-Item $stage -Recurse -Force

# SHASUMS256.txt in the same format build.ps1's Read-Shasums expects.
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($shaPath, "$hash  claude-win32-x64.zip`n",
    (New-Object Text.UTF8Encoding $false))

Write-Host ("synthetic payload: {0} ({1} bytes)" -f $zipPath, (Get-Item $zipPath).Length)
Write-Host ("shasums          : {0}  ({1})" -f $shaPath, $hash)
