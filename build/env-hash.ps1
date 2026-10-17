# ============================================================================
#  env-hash.ps1 - turn a `reg export` dump into a plaintext-free manifest.
#
#  Why: HKCU\Environment holds real secrets in cleartext (e.g.
#  ANTHROPIC_AUTH_TOKEN). To decide whether the environment changed we only
#  need "which value names exist and what are their values' sha256" - never the
#  values themselves - so the guard can compare state without leaving secrets
#  on disk.
#
#  IMPORTANT: `reg export` writes UTF-16LE. Read it as Unicode; a plain text
#  tool scanning the bytes would report a false negative.
#
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File env-hash.ps1 `
#               -Reg <path.reg> -Out <path.hashes>
#
#  Manifest format: one line per value,  <name><TAB><sha256 of raw right side>
#  (the right side is the text after '=' exactly as reg.exe wrote it).
#  Exit code 0 on success, non-zero otherwise.
#  ASCII-only on purpose: PowerShell 5.1 mis-reads BOM-less non-ASCII scripts.
# ============================================================================
param(
  [Parameter(Mandatory=$true)][string]$Reg,
  [Parameter(Mandatory=$true)][string]$Out
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Reg)) {
  [Console]::Error.WriteLine("env-hash.ps1: input not found: $Reg")
  exit 2
}

$text = [System.IO.File]::ReadAllText($Reg, [System.Text.Encoding]::Unicode)
$rx   = [regex]'^"([^"]+)"=(.*)$'
$sha  = [System.Security.Cryptography.SHA256]::Create()
$lines = New-Object System.Collections.Generic.List[string]

foreach ($line in ($text -split "`r`n|`n")) {
  $m = $rx.Match($line)
  if (-not $m.Success) { continue }
  $name  = $m.Groups[1].Value
  $raw   = $m.Groups[2].Value
  $bytes = [System.Text.Encoding]::UTF8.GetBytes($raw)
  $hex   = [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
  $lines.Add($name + "`t" + $hex)
}

$enc = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($Out, $lines, $enc)

Write-Output ("HASHED " + $lines.Count + " values -> " + $Out)
exit 0
