# Netclaw Windows install script
#
# Usage:
#   iwr -useb https://releases.netclaw.dev/install.ps1 | iex
#   .\install.ps1 -Component cli
#   .\install.ps1 -Component daemon
#   .\install.ps1 -InstallDir C:\tools\netclaw
#   .\install.ps1 -DryRun

param(
    [ValidateSet("all", "cli", "daemon")]
    [string]$Component = "all",

    [string]$InstallDir = "",

    [string]$Version = "",

    # Resolve and report what would be installed, but install nothing.
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

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

Write-Host "Netclaw installer"
Write-Host "  Platform: win-x64"
Write-Host "  Install dir: $InstallDir"
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

# Determine version
if ($Version) {
    $targetVersion = $Version
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

    # Check PATH
    Write-Host ""
    $currentPath = [Environment]::GetEnvironmentVariable("PATH", "User")
    if ($currentPath -split ";" | Where-Object { $_ -eq $InstallDir }) {
        Write-Host "Installation complete! netclaw is already on your PATH."
    } else {
        Write-Host "Installation complete!"
        Write-Host ""
        Write-Host "Add Netclaw to your PATH:"
        Write-Host ""
        Write-Host "  [Environment]::SetEnvironmentVariable('PATH', `"$InstallDir;`$env:PATH`", 'User')"
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
