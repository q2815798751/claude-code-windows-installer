# fetch-payload.ps1 - download the official Claude Code Windows payload for building.
# ASCII-only on purpose: Windows PowerShell 5.1 mis-decodes non-BOM UTF-8 scripts.
#
# Downloads claude-win32-x64.zip and SHASUMS256.txt for the version in ..\VERSION
# into ..\payload\, then verifies the SHA256 against the official manifest.
param(
    [string]$Root,
    [switch]$Arm64
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }

$verFile = Join-Path $Root 'VERSION'
if (-not (Test-Path $verFile)) { throw "VERSION file not found: $verFile" }
$version = (Get-Content -LiteralPath $verFile -Raw).Trim()
$tag = "v$version"

$payloadDir = Join-Path $Root 'payload'
if (-not (Test-Path $payloadDir)) { New-Item -ItemType Directory -Path $payloadDir | Out-Null }

$base = "https://github.com/anthropics/claude-code/releases/download/$tag"
$assets = @('claude-win32-x64.zip', 'SHASUMS256.txt')
if ($Arm64) { $assets += 'claude-win32-arm64.zip' }

# TLS 1.2: the default on older PowerShell can negotiate down to TLS 1.0 and fail.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

foreach ($asset in $assets) {
    $dest = Join-Path $payloadDir $asset
    if (Test-Path $dest) {
        Write-Host "  already present, skipping: $asset" -ForegroundColor DarkGray
        continue
    }
    $url = "$base/$asset"
    Write-Host "  downloading $asset"
    Write-Host "    from $url"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
    $sw.Stop()
    $mb = (Get-Item $dest).Length / 1MB
    Write-Host ("    done: {0:N1} MB in {1:N0}s ({2:N2} MB/s)" -f $mb, $sw.Elapsed.TotalSeconds,
                ($mb / [Math]::Max($sw.Elapsed.TotalSeconds, 0.001)))
}

# Verify against the official manifest.
$shasums = @{}
foreach ($line in (Get-Content -LiteralPath (Join-Path $payloadDir 'SHASUMS256.txt'))) {
    if ($line -match '^\s*([0-9a-fA-F]{64})\s+[*]?\s*(.+?)\s*$') {
        $shasums[$matches[2]] = $matches[1].ToLowerInvariant()
    }
}

Write-Host ''
Write-Host '  verifying against official SHASUMS256.txt'
$bad = 0
foreach ($asset in $shasums.Keys) {
    $path = Join-Path $payloadDir $asset
    if (-not (Test-Path $path)) { continue }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -eq $shasums[$asset]) {
        Write-Host "    OK   $asset" -ForegroundColor Green
    } else {
        Write-Host "    FAIL $asset" -ForegroundColor Red
        Write-Host "         expected $($shasums[$asset])"
        Write-Host "         actual   $actual"
        $bad++
    }
}

Write-Host ''
if ($bad -eq 0) {
    Write-Host '  payload ready. Run build\build.bat' -ForegroundColor Green
} else {
    throw "$bad payload file(s) failed verification"
}
