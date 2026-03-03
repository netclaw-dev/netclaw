# Netclaw Windows install script
#
# Usage:
#   iwr -useb https://feeds.netclaw.dev/install.ps1 | iex
#   .\install.ps1 -Component cli
#   .\install.ps1 -Component daemon
#   .\install.ps1 -InstallDir C:\tools\netclaw

param(
    [ValidateSet("all", "cli", "daemon")]
    [string]$Component = "all",

    [string]$InstallDir = "",

    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$ManifestUrl = "https://feeds.netclaw.dev/releases/manifest.json"
$DefaultInstallDir = Join-Path $env:LOCALAPPDATA "Programs\netclaw"

if (-not $InstallDir) {
    $InstallDir = $DefaultInstallDir
}

Write-Host "Netclaw installer"
Write-Host "  Platform: win-x64"
Write-Host "  Install dir: $InstallDir"
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

        $fileName = [System.IO.Path]::GetFileName([Uri]::new($asset.url).AbsolutePath)
        $downloadPath = Join-Path $tempDir $fileName

        Write-Host "  Downloading $ComponentName..."
        try {
            Invoke-WebRequest -Uri $asset.url -OutFile $downloadPath -UseBasicParsing
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
