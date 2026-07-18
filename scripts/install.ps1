# Netclaw Windows install script
#
# Usage:
#   iwr -useb https://releases.netclaw.dev/install.ps1 | iex
#   .\install.ps1 -Component cli
#   .\install.ps1 -Component daemon
#   .\install.ps1 -InstallDir C:\tools\netclaw
#   .\install.ps1 -Channel beta      # Opt into prereleases
#   .\install.ps1 -DryRun
#   .\install.ps1 -SkipShell         # Don't modify PATH
#
# -Channel beta installs the newest prerelease (or latest stable if no prerelease
# exists). -Version pins an exact version and overrides -Channel (e.g. 0.19.0-beta.1).

param(
    [ValidateSet("all", "cli", "daemon")]
    [string]$Component = "all",

    [string]$InstallDir = "",

    [string]$Version = "",

    # Release channel: "stable" (default) or "beta" (opt into prereleases).
    [ValidateSet("stable", "beta")]
    [string]$Channel = "stable",

    # Resolve and report what would be installed, but install nothing.
    [switch]$DryRun,

    # Skip automatic PATH modification.
    [switch]$SkipShell
)

$ErrorActionPreference = "Stop"

function Remove-TrailingDirectorySeparators {
    param([string]$Path)

    if ([string]::IsNullOrEmpty($Path)) {
        return $Path
    }

    $root = [System.IO.Path]::GetPathRoot($Path)
    if ($Path -eq $root) {
        return $Path
    }

    $separators = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $trimmed = $Path.TrimEnd($separators)
    if ([string]::IsNullOrEmpty($trimmed) -and -not [string]::IsNullOrEmpty($root)) {
        return $root
    }

    return $trimmed
}

function Invoke-DownloadWithProgress {
    param(
        [string]$Uri,
        [string]$OutFile,
        [string]$Label
    )

    $isInteractive = [Environment]::UserInteractive -and $Host.UI.RawUI -ne $null

    if (-not $isInteractive) {
        Write-Host "  Downloading $Label..."
        $oldPref = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing
        } finally {
            $ProgressPreference = $oldPref
        }
        return
    }

    # Interactive: download in background runspace, spinner in foreground
    $runspace = [runspacefactory]::CreateRunspace()
    $runspace.Open()

    $ps = [powershell]::Create().AddScript({
        param($uri, $outFile)
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $uri -OutFile $outFile -UseBasicParsing
    }).AddArgument($Uri).AddArgument($OutFile)

    $ps.Runspace = $runspace
    $handle = $ps.BeginInvoke()

    $spinChars = @('|', '/', '-', '\')
    $i = 0
    try {
        while (-not $handle.IsCompleted) {
            $char = $spinChars[$i % $spinChars.Length]
            Write-Host -NoNewline "`r  Downloading $Label... $char"
            Start-Sleep -Milliseconds 120
            $i++
        }
        Write-Host -NoNewline "`r  Downloading $Label... done"
        Write-Host ""

        $ps.EndInvoke($handle)
        if ($ps.HadErrors) {
            throw $ps.Streams.Error[0]
        }
    } finally {
        $ps.Dispose()
        $runspace.Close()
    }
}

# MANIFEST_URL is overridable so the script can be pointed at a local manifest
# (smoke tests) or a private mirror.
$ManifestUrl = if ($env:MANIFEST_URL) { $env:MANIFEST_URL } else { "https://releases.netclaw.dev/manifest.json" }
$DefaultInstallDir = Join-Path $env:LOCALAPPDATA "Programs\netclaw"

if (-not $InstallDir) {
    $InstallDir = $DefaultInstallDir
}

$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
if ($InstallDir.Contains(';') -or $InstallDir.Contains("`r") -or $InstallDir.Contains("`n")) {
    throw "InstallDir cannot contain semicolons, carriage returns, or newlines when used on PATH."
}

Write-Host "Netclaw installer"
Write-Host "  Platform: win-x64"
Write-Host "  Install dir: $InstallDir"
Write-Host "  Channel: $Channel"
if ($DryRun) {
    Write-Host "  Mode: dry run (no changes will be made)"
}
Write-Host ""

# Fetch manifest
Write-Host "Fetching release manifest..."
try {
    $manifest = Invoke-RestMethod -Uri $ManifestUrl -UseBasicParsing
} catch {
    Write-Error "Failed to fetch manifest from $ManifestUrl : $_"
    exit 1
}

# Determine version. Precedence: explicit pin > channel selection > stable latest.
if ($Version) {
    $targetVersion = $Version
} elseif ($Channel -eq "beta") {
    # Beta channel resolves to latestPrerelease (the newest of {stable, prerelease}).
    $targetVersion = $manifest.latestPrerelease
    if (-not $targetVersion) {
        # Manifest predates the prerelease channel — use latest stable and say so
        # loudly. This is the newest known version, not a silent default.
        Write-Host "  Note: manifest has no prerelease channel; using latest stable."
        $targetVersion = $manifest.latest
    }
} else {
    $targetVersion = $manifest.latest
}

if (-not $targetVersion) {
    Write-Error "Could not determine latest version from manifest"
    exit 1
}

Write-Host "  Version: $targetVersion"
Write-Host ""

$rid = "win-x64"
$release = $manifest.releases | Where-Object { $_.version -eq $targetVersion } | Select-Object -First 1

if (-not $release) {
    Write-Error "Version $targetVersion not found in manifest"
    exit 1
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "netclaw-install-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    function Install-Component {
        param([string]$ComponentName)

        $asset = $release.assets | Where-Object { $_.component -eq $ComponentName -and $_.rid -eq $rid } | Select-Object -First 1

        if (-not $asset) {
            Write-Warning "No $ComponentName binary found for $rid in version $targetVersion"
            return $false
        }

        if ($DryRun) {
            Write-Host "  DRY RUN: would install $ComponentName from $($asset.url)"
            return $true
        }

        $fileName = [System.IO.Path]::GetFileName([Uri]::new($asset.url).AbsolutePath)
        $downloadPath = Join-Path $tempDir $fileName

        try {
            Invoke-DownloadWithProgress -Uri $asset.url -OutFile $downloadPath -Label $ComponentName
        } catch {
            Write-Error "Failed to download $($asset.url): $_"
            return $false
        }

        # Verify checksum
        Write-Host "  Verifying checksum..."
        $hash = (Get-FileHash -Path $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $asset.sha256) {
            Write-Error "Checksum mismatch for $fileName`n  Expected: $($asset.sha256)`n  Got:      $hash"
            return $false
        }

        # Extract
        Write-Host "  Extracting..."
        $extractDir = Join-Path $tempDir $ComponentName
        Expand-Archive -Path $downloadPath -DestinationPath $extractDir -Force

        # Find and install binary
        $binaryName = "$ComponentName.exe"
        $binaryPath = Get-ChildItem -Path $extractDir -Recurse -Filter $binaryName | Select-Object -First 1

        if (-not $binaryPath) {
            Write-Error "Could not find $binaryName in archive"
            return $false
        }

        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Copy-Item -Path $binaryPath.FullName -Destination (Join-Path $InstallDir $binaryName) -Force
        Write-Host "  Installed $binaryName to $InstallDir\"

        return $true
    }

    $success = $true
    if ($Component -eq "all" -or $Component -eq "cli") {
        if (-not (Install-Component "netclaw")) { $success = $false }
    }
    if ($Component -eq "all" -or $Component -eq "daemon") {
        if (-not (Install-Component "netclawd")) { $success = $false }
    }

    if (-not $success) {
        Write-Host ""
        Write-Error "Some components failed to install."
        exit 1
    }

    if ($DryRun) {
        Write-Host ""
        Write-Host "Dry run complete - nothing was installed."
        return
    }

    # ── Persist UpdateChannel into config ──
    # Only runs when -Channel was explicitly passed. Without this guard a plain
    # upgrade would silently overwrite an existing beta channel to stable.
    if ($PSBoundParameters.ContainsKey('Channel')) {
        $configDir = if ($env:NETCLAW_CONFIG_DIR) { $env:NETCLAW_CONFIG_DIR } else { Join-Path $env:USERPROFILE ".netclaw\config" }
        $configFile = Join-Path $configDir "netclaw.json"
        if (Test-Path $configFile) {
            try {
                $existingConfig = Get-Content -Raw $configFile | ConvertFrom-Json
                if (-not $existingConfig.Daemon) {
                    $existingConfig | Add-Member -NotePropertyName "Daemon" -NotePropertyValue ([PSCustomObject]@{ UpdateChannel = $Channel })
                } else {
                    if ($existingConfig.Daemon.PSObject.Properties["UpdateChannel"]) {
                        $existingConfig.Daemon.UpdateChannel = $Channel
                    } else {
                        $existingConfig.Daemon | Add-Member -NotePropertyName "UpdateChannel" -NotePropertyValue $Channel
                    }
                }
                $existingConfig | ConvertTo-Json -Depth 10 | Set-Content -Path $configFile -Encoding UTF8
                Write-Host "  Set Daemon.UpdateChannel to '$Channel' in $configFile"
            } catch {
                Write-Host "  Note: could not update Daemon.UpdateChannel in config: $_"
                Write-Host "  To receive $Channel updates, set Daemon.UpdateChannel to '$Channel' in $configFile"
            }
        } elseif ($Channel -ne "stable") {
            # Fresh install: config doesn't exist yet. Write a minimal seed so
            # `netclaw init` can discover the channel preference.
            New-Item -ItemType Directory -Path $configDir -Force | Out-Null
            $seed = @{ configVersion = 1; Daemon = @{ UpdateChannel = $Channel } }
            $seed | ConvertTo-Json -Depth 5 | Set-Content -Path $configFile -Encoding UTF8
            Write-Host "  Created $configFile with UpdateChannel '$Channel'"
        }
    }

    # ── Add to PATH ──
    Write-Host ""

    if (-not $SkipShell) {
        $installDirNormalized = Remove-TrailingDirectorySeparators $InstallDir

        # Read the raw registry value so an existing REG_EXPAND_SZ PATH keeps both
        # its %VAR% references and its registry type when we prepend Netclaw.
        # HKCU is writable by the current user and does not require elevation.
        # CreateSubKey opens the normal existing key and also supports minimal
        # profiles where the per-user Environment key has not been created yet.
        $userEnvironmentKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Environment")
        if ($null -eq $userEnvironmentKey) {
            throw "Cannot create or open the current user's Environment registry key for PATH update."
        }

        try {
            $pathValueExists = $userEnvironmentKey.GetValueNames() -contains "Path"
            $userPath = if ($pathValueExists) {
                $userEnvironmentKey.GetValue(
                    "Path",
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            } else {
                ""
            }

            if ($null -ne $userPath -and $userPath -isnot [string]) {
                throw "The current user's PATH registry value is not a string."
            }

            $userPathKind = if ($pathValueExists) {
                $userEnvironmentKey.GetValueKind("Path")
            } else {
                [Microsoft.Win32.RegistryValueKind]::String
            }
            if ($userPathKind -notin @(
                    [Microsoft.Win32.RegistryValueKind]::String,
                    [Microsoft.Win32.RegistryValueKind]::ExpandString)) {
                throw "The current user's PATH registry value has unsupported type $userPathKind."
            }

            if ($userPathKind -eq [Microsoft.Win32.RegistryValueKind]::ExpandString `
                -and $installDirNormalized.Contains('%')) {
                throw "InstallDir containing '%' cannot be safely added to an expandable User PATH. Choose a directory without '%' or rerun with -SkipShell."
            }

            $userPathEntries = if ([string]::IsNullOrEmpty($userPath)) { @() } else {
                $userPath -split ';' |
                    Where-Object { -not [string]::IsNullOrEmpty($_) } |
                    ForEach-Object {
                        $expandedEntry = [Environment]::ExpandEnvironmentVariables($_)
                        Remove-TrailingDirectorySeparators $expandedEntry
                    }
            }

            $userPathChanged = $false
            if ($userPathEntries -notcontains $installDirNormalized) {
                $newUserPath = if ([string]::IsNullOrEmpty($userPath)) {
                    $installDirNormalized
                } else {
                    "$installDirNormalized;$userPath"
                }

                if ($newUserPath.Length -gt 32700) {
                    Write-Warning "User PATH is near its 32,767 character limit ($($newUserPath.Length) chars)."
                    Write-Host "Please manually add $InstallDir to your User PATH."
                } else {
                    $userEnvironmentKey.SetValue("Path", $newUserPath, $userPathKind)
                    $userPathChanged = $true
                }
            }
        } finally {
            $userEnvironmentKey.Dispose()
        }

        $processPath = $env:PATH
        $processPathEntries = if ([string]::IsNullOrEmpty($processPath)) { @() } else {
            $processPath -split ';' |
                Where-Object { -not [string]::IsNullOrEmpty($_) } |
                ForEach-Object { Remove-TrailingDirectorySeparators $_ }
        }
        if ($processPathEntries -notcontains $installDirNormalized) {
            $env:PATH = if ([string]::IsNullOrEmpty($processPath)) {
                $installDirNormalized
            } else {
                "$installDirNormalized;$processPath"
            }
        }

        if ($userPathChanged) {
            if (-not ("NetclawInstaller.NativeMethods" -as [type])) {
                Add-Type -Namespace NetclawInstaller -Name NativeMethods -MemberDefinition @'
                    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)]
                    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg,
                        UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
            }

            $broadcastOutput = [UIntPtr]::Zero
            $broadcastResult = [NetclawInstaller.NativeMethods]::SendMessageTimeout(
                [IntPtr]0xFFFF, 0x001A, [UIntPtr]::Zero, "Environment", 2, 1000, [ref]$broadcastOutput)
            if ($broadcastResult -eq [IntPtr]::Zero) {
                Write-Warning "User PATH was updated, but Windows did not acknowledge the environment-change notification. New terminals may require sign-out or restart."
            }
        }

        if ($userPathEntries -contains $installDirNormalized) {
            Write-Host "Installation complete! netclaw is already on your User PATH."
        } elseif ($userPathChanged) {
            Write-Host "Installation complete! netclaw was added to your User PATH."
        } else {
            Write-Host "Installation complete! netclaw is on PATH for this terminal only."
        }
    } else {
        Write-Host "Installation complete! (PATH modification skipped)"
        Write-Host ""
        Write-Host "Add this directory to your User PATH using Windows Environment Variables settings:"
        Write-Host ""
        Write-Host "  $InstallDir"
        Write-Host ""
        Write-Host "Then restart your terminal."
    }

    Write-Host ""
    Write-Host "Get started:"
    Write-Host "  netclaw init      # First-run setup wizard"
    Write-Host "  netclaw doctor    # Verify configuration"

} finally {
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
