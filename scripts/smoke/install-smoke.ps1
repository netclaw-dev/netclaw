# install-smoke.ps1 - hermetic smoke test for scripts/install.ps1
#
# The Windows counterpart of scripts/smoke/install-smoke.sh. It serves a
# generated manifest and stand-in archives from localhost - no network, no
# dotnet build - and verifies install.ps1's manifest parsing and the
# download -> checksum -> extract -> install path, plus -DryRun.
#
# Usage:    pwsh -File scripts/smoke/install-smoke.ps1
#           powershell.exe -File scripts/smoke/install-smoke.ps1
# Requires: PowerShell 5.1+ and python (for the local HTTP server).

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$PowerShellExecutable = if ($PSVersionTable.PSEdition -eq "Desktop") {
    (Get-Command powershell.exe).Source
} else {
    (Get-Command pwsh).Source
}

$RepoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot "..") "..")).Path
$InstallPs1 = Join-Path (Join-Path $RepoRoot "scripts") "install.ps1"
$Version = "0.0.0"            # stable -> manifest.latest
$BetaVersion = "0.0.1-beta1"  # prerelease -> manifest.latestPrerelease
$Rid = "win-x64"

$script:Pass = 0
$script:Fail = 0
function Pass([string]$m) { Write-Host "PASS: $m"; $script:Pass++ }
function Fail([string]$m) { Write-Host "FAIL: $m"; $script:Fail++ }

function Invoke-CapturedPowerShell {
    param([string[]]$Arguments)

    # Windows PowerShell 5.1 promotes a native child's stderr to an error record.
    # Rejection tests need to inspect that output and exit code without aborting.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $PowerShellExecutable @Arguments 2>&1 | Out-String
        [PSCustomObject]@{ Output = $output; ExitCode = $LASTEXITCODE }
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

function Get-UserPathRegistryState {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Environment", $false)
    if ($null -eq $key) { throw "Cannot open the current user's Environment registry key." }
    try {
        $exists = $key.GetValueNames() -contains "Path"
        [PSCustomObject]@{
            Exists = $exists
            Value = if ($exists) {
                $key.GetValue(
                    "Path",
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            } else {
                $null
            }
            Kind = if ($exists) { $key.GetValueKind("Path") } else { $null }
        }
    } finally {
        $key.Dispose()
    }
}

function Set-UserPathRegistryState {
    param(
        [bool]$Exists,
        [AllowNull()][string]$Value,
        [AllowNull()][Microsoft.Win32.RegistryValueKind]$Kind
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Environment")
    if ($null -eq $key) { throw "Cannot create or open the current user's Environment registry key for update." }
    try {
        if ($Exists) {
            $key.SetValue("Path", $Value, $Kind)
        } else {
            $key.DeleteValue("Path", $false)
        }
    } finally {
        $key.Dispose()
    }
}

$Work = Join-Path ([System.IO.Path]::GetTempPath()) ("netclaw-install-smoke-" + [Guid]::NewGuid().ToString('N'))
$Serve = Join-Path $Work "serve"
$BinDir = Join-Path $Work "bin"
$OriginalUserPath = Get-UserPathRegistryState
$OriginalProcessPath = $env:PATH
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
    # The fixture is ASCII-only. Windows PowerShell 5.1 writes a BOM for
    # -Encoding UTF8 while PowerShell 7 does not, so use an identical encoding.
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $Serve "manifest.json") -Encoding ascii

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
    $dryOut = & $PowerShellExecutable -NoProfile -File $InstallPs1 -InstallDir $dryDir -DryRun 2>&1 | Out-String
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

    $invalidResult = Invoke-CapturedPowerShell -Arguments @(
        "-NoProfile", "-File", $InstallPs1,
        "-InstallDir", (Join-Path $Work "invalid;path"), "-DryRun")
    if ($invalidResult.ExitCode -ne 0 -and $invalidResult.Output -match "cannot contain semicolons") {
        Pass "PATH: unrepresentable Windows install directory rejected"
    } else {
        Fail "PATH: Windows install directory containing ';' was accepted"
    }

    $installDir = Join-Path $Work "installed"
    $existingUserEntry = Join-Path $Work "existing-user-bin"
    Set-UserPathRegistryState $true $existingUserEntry ([Microsoft.Win32.RegistryValueKind]::String)
    $installOut = & $InstallPs1 -InstallDir $installDir *>&1 | Out-String
    Write-Host ($installOut.TrimEnd())
    foreach ($name in @("netclaw", "netclawd")) {
        $exe = Join-Path $installDir "$name.exe"
        if ((Test-Path $exe) -and ((Get-Item $exe).Length -gt 0)) {
            Pass "install: $name.exe installed"
        } else {
            Fail "install: $name.exe missing or empty"
        }
    }

    # 7. Verify the real installer changed User PATH without replacing the
    # current process's inherited Machine PATH entries.
    Write-Host ""
    Write-Host "=== PATH automation ==="
    $persistedPath = Get-UserPathRegistryState
    $persistedEntries = @($persistedPath.Value -split ';')
    if ($persistedEntries[0] -eq $installDir `
        -and $persistedEntries -contains $existingUserEntry `
        -and @($persistedEntries | Where-Object { $_ -eq $installDir }).Count -eq 1) {
        Pass "PATH: install directory prepended once and existing User PATH preserved"
    } else {
        Fail "PATH: persisted User PATH has unexpected contents"
    }

    $originalProcessEntries = @($OriginalProcessPath -split ';' | Where-Object { $_ })
    $currentProcessEntries = @($env:PATH -split ';' | Where-Object { $_ })
    $missingProcessEntries = @($originalProcessEntries | Where-Object { $currentProcessEntries -notcontains $_ })
    if ($currentProcessEntries[0] -eq $installDir `
        -and @($currentProcessEntries | Where-Object { $_ -eq $installDir }).Count -eq 1 `
        -and $missingProcessEntries.Count -eq 0) {
        Pass "PATH: current process prepended once and inherited entries preserved"
    } else {
        Fail "PATH: current process lost inherited entries or contains duplicates"
    }

    # A persisted entry must still repair a stale current process, and a
    # trailing separator must not create a duplicate User PATH entry.
    $env:PATH = $OriginalProcessPath
    $userPathBeforeRepeat = Get-UserPathRegistryState
    & $InstallPs1 -InstallDir "$installDir\" *>&1 | Out-Null
    $userPathAfterRepeat = Get-UserPathRegistryState
    $repeatProcessEntries = @($env:PATH -split ';' | Where-Object { $_ })
    if ($userPathAfterRepeat.Value -eq $userPathBeforeRepeat.Value `
        -and $userPathAfterRepeat.Kind -eq $userPathBeforeRepeat.Kind `
        -and $repeatProcessEntries[0] -eq $installDir `
        -and @($repeatProcessEntries | Where-Object { $_ -eq $installDir }).Count -eq 1) {
        Pass "PATH: repeat install is idempotent and repairs current process"
    } else {
        Fail "PATH: repeat install changed User PATH or duplicated process entry"
    }

    $env:NETCLAW_SMOKE_INSTALL_DIR = $installDir
    $expandedUserPath = "%NETCLAW_SMOKE_INSTALL_DIR%;$existingUserEntry"
    Set-UserPathRegistryState $true $expandedUserPath ([Microsoft.Win32.RegistryValueKind]::ExpandString)
    $env:PATH = $OriginalProcessPath
    & $InstallPs1 -InstallDir $installDir *>&1 | Out-Null
    $expandedUserPathAfter = Get-UserPathRegistryState
    if ($expandedUserPathAfter.Value -eq $expandedUserPath `
        -and $expandedUserPathAfter.Kind -eq [Microsoft.Win32.RegistryValueKind]::ExpandString) {
        Pass "PATH: expandable User entry keeps its raw text and REG_EXPAND_SZ type"
    } else {
        Fail "PATH: expandable User entry or its registry type was rewritten"
    }

    $literalPercentInstall = Join-Path $Work "%NETCLAW_LITERAL%\bin"
    Set-UserPathRegistryState $true $existingUserEntry ([Microsoft.Win32.RegistryValueKind]::String)
    $literalPercentOut = & $PowerShellExecutable -NoProfile -File $InstallPs1 `
        -InstallDir $literalPercentInstall 2>&1 | Out-String
    $literalPercentState = Get-UserPathRegistryState
    if ($LASTEXITCODE -eq 0 `
        -and $literalPercentState.Kind -eq [Microsoft.Win32.RegistryValueKind]::String `
        -and @($literalPercentState.Value -split ';')[0] -eq $literalPercentInstall) {
        Pass "PATH: literal percent is preserved in a non-expanding User PATH"
    } else {
        Fail "PATH: literal percent was not preserved in a non-expanding User PATH"
        Write-Host ($literalPercentOut.TrimEnd())
    }

    $expandablePath = "%SystemRoot%\System32"
    $unsafePercentInstall = Join-Path $Work "%TEMP%\netclaw"
    Set-UserPathRegistryState $true $expandablePath ([Microsoft.Win32.RegistryValueKind]::ExpandString)
    $expandableStateBefore = Get-UserPathRegistryState
    $unsafePercentResult = Invoke-CapturedPowerShell -Arguments @(
        "-NoProfile", "-File", $InstallPs1, "-InstallDir", $unsafePercentInstall)
    $expandableStateAfter = Get-UserPathRegistryState
    if ($unsafePercentResult.ExitCode -ne 0 `
        -and $unsafePercentResult.Output -match "cannot be safely added to an expandable User PATH" `
        -and $expandableStateAfter.Value -eq $expandableStateBefore.Value `
        -and $expandableStateAfter.Kind -eq $expandableStateBefore.Kind) {
        Pass "PATH: literal percent is rejected before mutating an expandable User PATH"
    } else {
        Fail "PATH: literal percent corrupted or changed an expandable User PATH"
    }

    $env:PATH = ""
    & $InstallPs1 -InstallDir $installDir *>&1 | Out-Null
    if ($env:PATH -eq $installDir) {
        Pass "PATH: empty process PATH does not create an empty entry"
    } else {
        Fail "PATH: empty process PATH produced unexpected contents"
    }
    $env:PATH = $OriginalProcessPath

    # 7c. Test -SkipShell flag
    Write-Host ""
    Write-Host "=== -SkipShell flag ==="
    $skipDir = Join-Path $Work "skip-install's"
    $skipUserPathBefore = Get-UserPathRegistryState
    $skipProcessPathBefore = $env:PATH
    $skipOut = & $PowerShellExecutable -NoProfile -File $InstallPs1 -InstallDir $skipDir -SkipShell 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) {
        Pass "-SkipShell: install completes without error"
    } else {
        Fail "-SkipShell: install failed (exit=$LASTEXITCODE)"
    }
    if ($skipOut -match "PATH modification skipped") {
        Pass "-SkipShell: output mentions PATH modification skipped"
    } else {
        Fail "-SkipShell: missing 'skipped' message"
    }
    $skipUserPathAfter = Get-UserPathRegistryState
    if ($skipUserPathAfter.Value -eq $skipUserPathBefore.Value `
        -and $skipUserPathAfter.Kind -eq $skipUserPathBefore.Kind `
        -and $env:PATH -eq $skipProcessPathBefore `
        -and $skipOut -match [regex]::Escape("Add this directory to your User PATH") `
        -and $skipOut -match [regex]::Escape($skipDir)) {
        Pass "-SkipShell: PATH is unchanged and manual guidance handles a literal install directory"
    } else {
        Fail "-SkipShell: changed PATH or printed an unusable manual command"
    }

    # 8. Release channel resolution (dry-run)
    Write-Host ""
    Write-Host "=== release channel resolution ==="
    $shouldNotExist = Join-Path $Work "should-not-exist"

    function Assert-Resolves([string]$desc, [string]$want, [string[]]$extraArgs) {
        $out = & $PowerShellExecutable -NoProfile -File $InstallPs1 -InstallDir $shouldNotExist -DryRun @extraArgs 2>&1 | Out-String
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
    $invalidChannelResult = Invoke-CapturedPowerShell -Arguments @(
        "-NoProfile", "-File", $InstallPs1, "-InstallDir", $shouldNotExist,
        "-DryRun", "-Channel", "bogus")
    if ($invalidChannelResult.ExitCode -ne 0) {
        Pass "channel: unknown value rejected"
    } else {
        Fail "channel: unknown value should fail (exit=$LASTEXITCODE)"
    }
    # 9. Config channel persistence
    Write-Host ""
    Write-Host "=== config channel persistence ==="

    # 9a. Fresh install with -Channel beta seeds a config file
    $freshDir = Join-Path $Work "fresh-beta"
    $freshConfigDir = Join-Path $Work "fresh-beta-config"
    $env:NETCLAW_CONFIG_DIR = $freshConfigDir
    & $PowerShellExecutable -NoProfile -File $InstallPs1 -InstallDir $freshDir -Channel beta -SkipShell 2>&1 | Out-Null
    $freshConfig = Join-Path $freshConfigDir "netclaw.json"
    if ((Test-Path $freshConfig)) {
        $c = Get-Content -Raw $freshConfig | ConvertFrom-Json
        if ($c.Daemon.UpdateChannel -eq "beta") {
            Pass "config: fresh -Channel beta seeds config with UpdateChannel=beta"
        } else {
            Fail "config: fresh -Channel beta wrote UpdateChannel='$($c.Daemon.UpdateChannel)'"
        }
    } else {
        Fail "config: fresh -Channel beta did not create config file"
    }

    # 9b. -Channel beta on existing config patches UpdateChannel
    $existDir = Join-Path $Work "exist-beta"
    $existConfigDir = Join-Path $Work "exist-beta-config"
    New-Item -ItemType Directory -Path $existConfigDir -Force | Out-Null
    '{"configVersion":1,"Daemon":{"ExposureMode":"local"}}' | Set-Content -Path (Join-Path $existConfigDir "netclaw.json") -Encoding UTF8
    $env:NETCLAW_CONFIG_DIR = $existConfigDir
    & $PowerShellExecutable -NoProfile -File $InstallPs1 -InstallDir $existDir -Channel beta -SkipShell 2>&1 | Out-Null
    $c = Get-Content -Raw (Join-Path $existConfigDir "netclaw.json") | ConvertFrom-Json
    if ($c.Daemon.UpdateChannel -eq "beta" -and $c.Daemon.ExposureMode -eq "local") {
        Pass "config: -Channel beta patches existing config, preserves other Daemon keys"
    } else {
        Fail "config: -Channel beta patch (UpdateChannel='$($c.Daemon.UpdateChannel)', ExposureMode='$($c.Daemon.ExposureMode)')"
    }

    # 9c. Plain upgrade (no -Channel) leaves existing beta config alone
    $noflagDir = Join-Path $Work "noflag"
    $noflagConfigDir = Join-Path $Work "noflag-config"
    New-Item -ItemType Directory -Path $noflagConfigDir -Force | Out-Null
    '{"configVersion":1,"Daemon":{"UpdateChannel":"beta"}}' | Set-Content -Path (Join-Path $noflagConfigDir "netclaw.json") -Encoding UTF8
    $env:NETCLAW_CONFIG_DIR = $noflagConfigDir
    & $PowerShellExecutable -NoProfile -File $InstallPs1 -InstallDir $noflagDir -SkipShell 2>&1 | Out-Null
    $c = Get-Content -Raw (Join-Path $noflagConfigDir "netclaw.json") | ConvertFrom-Json
    if ($c.Daemon.UpdateChannel -eq "beta") {
        Pass "config: plain upgrade preserves existing beta channel"
    } else {
        Fail "config: plain upgrade changed UpdateChannel to '$($c.Daemon.UpdateChannel)'"
    }

    # 9d. -Channel stable on existing beta overwrites to stable
    $downDir = Join-Path $Work "downgrade"
    $downConfigDir = Join-Path $Work "downgrade-config"
    New-Item -ItemType Directory -Path $downConfigDir -Force | Out-Null
    '{"configVersion":1,"Daemon":{"UpdateChannel":"beta"}}' | Set-Content -Path (Join-Path $downConfigDir "netclaw.json") -Encoding UTF8
    $env:NETCLAW_CONFIG_DIR = $downConfigDir
    & $PowerShellExecutable -NoProfile -File $InstallPs1 -InstallDir $downDir -Channel stable -SkipShell 2>&1 | Out-Null
    $c = Get-Content -Raw (Join-Path $downConfigDir "netclaw.json") | ConvertFrom-Json
    if ($c.Daemon.UpdateChannel -eq "stable") {
        Pass "config: -Channel stable overwrites existing beta"
    } else {
        Fail "config: -Channel stable wrote UpdateChannel='$($c.Daemon.UpdateChannel)'"
    }
}
finally {
    if ($ServerProc -and -not $ServerProc.HasExited) { $ServerProc.Kill() }
    Set-UserPathRegistryState $OriginalUserPath.Exists $OriginalUserPath.Value $OriginalUserPath.Kind
    $env:PATH = $OriginalProcessPath
    $env:MANIFEST_URL = $null
    $env:NETCLAW_CONFIG_DIR = $null
    $env:NETCLAW_SMOKE_INSTALL_DIR = $null
    Remove-Item -Path $Work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Results: $script:Pass passed, $script:Fail failed"
if ($script:Fail -gt 0) {
    Write-Host "install smoke (ps1): FAILED"
    exit 1
}
Write-Host "install smoke (ps1): PASSED"
# Deliberate rejection checks run child processes that exit nonzero, so return
# the assertion result explicitly instead of inheriting a child's exit code.
exit 0
