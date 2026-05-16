# install-smoke.ps1 - hermetic smoke test for scripts/install.ps1
#
# The Windows counterpart of scripts/smoke/install-smoke.sh. It serves a
# generated manifest and stand-in archives from localhost - no network, no
# dotnet build - and verifies install.ps1's manifest parsing and the
# download -> checksum -> extract -> install path, plus -DryRun.
#
# Usage:    pwsh -File scripts/smoke/install-smoke.ps1
# Requires: PowerShell 7+, python (for the local HTTP server).

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot ".." "..")).Path
$InstallPs1 = Join-Path $RepoRoot "scripts" "install.ps1"
$Version = "0.0.0"
$Rid = "win-x64"

$script:Pass = 0
$script:Fail = 0
function Pass([string]$m) { Write-Host "PASS: $m"; $script:Pass++ }
function Fail([string]$m) { Write-Host "FAIL: $m"; $script:Fail++ }

$Work = Join-Path ([System.IO.Path]::GetTempPath()) ("netclaw-install-smoke-" + [Guid]::NewGuid().ToString('N'))
$Serve = Join-Path $Work "serve"
$VersionDir = Join-Path $Serve $Version
$BinDir = Join-Path $Work "bin"
New-Item -ItemType Directory -Path $VersionDir, $BinDir -Force | Out-Null

$ServerProc = $null
try {
    # 1. Stand-in binaries - install.ps1 only needs a file named <component>.exe
    foreach ($name in @("netclaw", "netclawd")) {
        Set-Content -Path (Join-Path $BinDir "$name.exe") -Value "stand-in $name $Version" -NoNewline
    }

    # 2. Package zip archives + collect asset metadata
    $assets = @()
    foreach ($comp in @("netclaw", "netclawd")) {
        $archiveName = "$comp-$Version-$Rid.zip"
        $archivePath = Join-Path $VersionDir $archiveName
        Compress-Archive -Path (Join-Path $BinDir "$comp.exe") -DestinationPath $archivePath -Force
        $hash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $assets += [ordered]@{
            component = $comp
            rid       = $Rid
            url       = "PLACEHOLDER/$Version/$archiveName"
            sha256    = $hash
            sizeBytes = (Get-Item $archivePath).Length
        }
    }

    # 3. Pick a free port, then write the manifest with localhost URLs
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $Port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $BaseUrl = "http://127.0.0.1:$Port"
    foreach ($a in $assets) { $a.url = $a.url.Replace("PLACEHOLDER", $BaseUrl) }

    $manifest = [ordered]@{
        schemaVersion = 1
        feedType      = "releases"
        latest        = $Version
        releases      = @(
            [ordered]@{
                version = $Version
                assets  = $assets
            }
        )
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $Serve "manifest.json") -Encoding utf8

    # 4. Serve the manifest + archives from localhost
    $python = Get-Command python3 -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command python -ErrorAction SilentlyContinue }
    if (-not $python) { throw "python is required to run the local manifest server" }

    $ServerProc = Start-Process -FilePath $python.Source -PassThru -NoNewWindow `
        -ArgumentList @("-m", "http.server", "$Port", "--bind", "127.0.0.1", "--directory", $Serve) `
        -RedirectStandardOutput (Join-Path $Work "http.out") `
        -RedirectStandardError (Join-Path $Work "http.err")

    $ready = $false
    for ($i = 0; $i -lt 50; $i++) {
        try {
            Invoke-WebRequest "$BaseUrl/manifest.json" -UseBasicParsing -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 200
        }
    }
    if (-not $ready) { throw "local manifest server did not come up on $BaseUrl" }

    $env:MANIFEST_URL = "$BaseUrl/manifest.json"

    # 5. Dry-run check - resolves assets, installs nothing
    Write-Host "=== dry run ==="
    $dryDir = Join-Path $Work "dryrun-none"
    $dryOut = & pwsh -NoProfile -File $InstallPs1 -InstallDir $dryDir -DryRun 2>&1 | Out-String
    Write-Host ($dryOut.TrimEnd())
    if ($LASTEXITCODE -eq 0 `
        -and $dryOut -match 'DRY RUN: would install netclaw ' `
        -and $dryOut -match 'DRY RUN: would install netclawd ') {
        Pass "dry-run: resolved both components"
    } else {
        Fail "dry-run: expected DRY RUN lines for netclaw and netclawd (exit=$LASTEXITCODE)"
    }
    if (Test-Path $dryDir) {
        Fail "dry-run: created an install directory (should install nothing)"
    } else {
        Pass "dry-run: installed nothing"
    }

    # 6. Real install of the stand-in archives
    Write-Host ""
    Write-Host "=== real install ==="
    $installDir = Join-Path $Work "installed"
    $installOut = & pwsh -NoProfile -File $InstallPs1 -InstallDir $installDir 2>&1 | Out-String
    Write-Host ($installOut.TrimEnd())
    if ($LASTEXITCODE -eq 0) {
        Pass "install: exited 0"
    } else {
        Fail "install: exited $LASTEXITCODE"
    }
    foreach ($name in @("netclaw", "netclawd")) {
        $exe = Join-Path $installDir "$name.exe"
        if ((Test-Path $exe) -and ((Get-Item $exe).Length -gt 0)) {
            Pass "install: $name.exe installed"
        } else {
            Fail "install: $name.exe missing or empty"
        }
    }
}
finally {
    if ($ServerProc -and -not $ServerProc.HasExited) { $ServerProc.Kill() }
    $env:MANIFEST_URL = $null
    Remove-Item -Path $Work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Results: $script:Pass passed, $script:Fail failed"
if ($script:Fail -gt 0) {
    Write-Host "install smoke (ps1): FAILED"
    exit 1
}
Write-Host "install smoke (ps1): PASSED"
