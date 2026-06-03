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
$Version = "0.0.0"            # stable -> manifest.latest
$BetaVersion = "0.0.1-beta1"  # prerelease -> manifest.latestPrerelease
$Rid = "win-x64"

$script:Pass = 0
$script:Fail = 0
function Pass([string]$m) { Write-Host "PASS: $m"; $script:Pass++ }
function Fail([string]$m) { Write-Host "FAIL: $m"; $script:Fail++ }

$Work = Join-Path ([System.IO.Path]::GetTempPath()) ("netclaw-install-smoke-" + [Guid]::NewGuid().ToString('N'))
$Serve = Join-Path $Work "serve"
$BinDir = Join-Path $Work "bin"
New-Item -ItemType Directory -Path $Serve, $BinDir -Force | Out-Null

$ServerProc = $null
try {
    # 1. Stand-in binaries - install.ps1 only needs a file named <component>.exe
    foreach ($name in @("netclaw", "netclawd")) {
        Set-Content -Path (Join-Path $BinDir "$name.exe") -Value "stand-in $name" -NoNewline
    }

    # 2. Pick a free port (asset URLs embed it)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $Port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $BaseUrl = "http://127.0.0.1:$Port"

    # 3. Package zip archives for a stable AND a prerelease, and write a manifest with
    #    latest (stable) + latestPrerelease (prerelease). Two versions let us prove
    #    channel selection: default -> latest, -Channel beta -> latestPrerelease.
    function New-ReleaseEntry([string]$ver) {
        $verDir = Join-Path $Serve $ver
        New-Item -ItemType Directory -Path $verDir -Force | Out-Null
        $entryAssets = @()
        foreach ($comp in @("netclaw", "netclawd")) {
            $archiveName = "$comp-$ver-$Rid.zip"
            $archivePath = Join-Path $verDir $archiveName
            Compress-Archive -Path (Join-Path $BinDir "$comp.exe") -DestinationPath $archivePath -Force
            $hash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
            $entryAssets += [ordered]@{
                component = $comp
                rid       = $Rid
                url       = "$BaseUrl/$ver/$archiveName"
                sha256    = $hash
                sizeBytes = (Get-Item $archivePath).Length
            }
        }
        return [ordered]@{ version = $ver; assets = $entryAssets }
    }

    $stableEntry = New-ReleaseEntry $Version
    $betaEntry = New-ReleaseEntry $BetaVersion

    $manifest = [ordered]@{
        schemaVersion    = 1
        feedType         = "releases"
        latest           = $Version
        latestPrerelease = $BetaVersion
        releases         = @($betaEntry, $stableEntry)
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

    # 7. Verify PATH instruction uses User scope correctly (issue #1072)
    # The printed instruction must NOT use $env:PATH (which merges Machine+User
    # and corrupts the User PATH when written back). It must read User scope.
    Write-Host ""
    Write-Host "=== PATH instruction check ==="
    if ($installOut -match '\$env:PATH') {
        Fail "PATH instruction: uses `$env:PATH (corrupts User PATH by merging Machine entries)"
    } else {
        Pass "PATH instruction: does not use `$env:PATH"
    }
    if ($installOut -match "GetEnvironmentVariable\('PATH',\s*'User'\)") {
        Pass "PATH instruction: reads from User scope"
    } else {
        Fail "PATH instruction: should read from User scope with GetEnvironmentVariable('PATH', 'User')"
    }

    # 8. Release channel resolution (dry-run)
    Write-Host ""
    Write-Host "=== release channel resolution ==="
    $shouldNotExist = Join-Path $Work "should-not-exist"

    function Assert-Resolves([string]$desc, [string]$want, [string[]]$extraArgs) {
        $out = & pwsh -NoProfile -File $InstallPs1 -InstallDir $shouldNotExist -DryRun @extraArgs 2>&1 | Out-String
        $pattern = "(?m)^\s+Version:\s+$([regex]::Escape($want))\s*$"
        if ($LASTEXITCODE -eq 0 -and $out -match $pattern) {
            Pass "channel: $desc -> $want"
        } else {
            Fail "channel: $desc (exit=$LASTEXITCODE, expected Version: $want)"
            Write-Host ($out.TrimEnd())
        }
    }

    Assert-Resolves "default install -> latest stable"    $Version     @()
    Assert-Resolves "-Channel stable -> latest stable"    $Version     @("-Channel", "stable")
    Assert-Resolves "-Channel beta -> latest prerelease"  $BetaVersion @("-Channel", "beta")
    Assert-Resolves "-Version pin overrides -Channel"     $BetaVersion @("-Channel", "stable", "-Version", $BetaVersion)

    # An unknown channel must be rejected by the ValidateSet, not silently default.
    & pwsh -NoProfile -File $InstallPs1 -InstallDir $shouldNotExist -DryRun -Channel bogus 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Pass "channel: unknown value rejected"
    } else {
        Fail "channel: unknown value should fail (exit=$LASTEXITCODE)"
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
# Exit explicitly on the result, not on $LASTEXITCODE — the channel checks above run
# `pwsh -Channel bogus` (which exits non-zero by design), and without this the script
# would fall off the end and inherit that non-zero code despite all assertions passing.
exit 0
