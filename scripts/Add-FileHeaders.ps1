<#
.SYNOPSIS
    Adds Petabridge LLC copyright headers to C# files that don't have them.

.DESCRIPTION
    Scans all .cs files in src/ directories and adds the standard
    Petabridge copyright header to files that are missing it.

.PARAMETER WhatIf
    Shows what files would be modified without making changes.

.PARAMETER Verify
    Returns exit code 1 if any files are missing headers (for CI validation).

.EXAMPLE
    ./scripts/Add-FileHeaders.ps1
    Adds headers to all files missing them.

.EXAMPLE
    ./scripts/Add-FileHeaders.ps1 -WhatIf
    Shows which files would be modified.

.EXAMPLE
    ./scripts/Add-FileHeaders.ps1 -Verify
    Checks if all files have headers (for CI).
#>

param(
    [switch]$WhatIf,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Parent $PSScriptRoot
$currentYear = (Get-Date).Year

function Get-FileHeader {
    param([string]$FileName, [int]$CreatedYear)

    return @"
// -----------------------------------------------------------------------
// <copyright file="$FileName" company="Petabridge, LLC">
//      Copyright (C) $CreatedYear - $currentYear Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

"@
}

function Test-HasHeader {
    param([string]$Content)
    return $Content -match "^\s*(//|/\*)\s*-{5,}" -or $Content -match "<copyright.*Petabridge"
}

function Get-FileCreatedYear {
    param([string]$FilePath)

    # Try to get the year from git
    $gitYear = git log --follow --format="%ai" --diff-filter=A -- $FilePath 2>$null | Select-Object -First 1
    if ($gitYear) {
        return [int]($gitYear.Substring(0, 4))
    }

    # Fall back to current year for new files
    return $currentYear
}

# Find all C# files in src/
$searchPaths = @(
    (Join-Path $rootDir "src")
)

$csFiles = @()
foreach ($path in $searchPaths) {
    if (Test-Path $path) {
        $csFiles += Get-ChildItem -Path $path -Filter "*.cs" -Recurse |
            Where-Object { $_.FullName -notmatch "[\\/](obj|bin)[\\/]" }
    }
}

$missingHeaders = @()
$processedCount = 0

foreach ($file in $csFiles) {
    $content = Get-Content -Path $file.FullName -Raw -ErrorAction SilentlyContinue

    if (-not $content) {
        continue
    }

    if (-not (Test-HasHeader -Content $content)) {
        $missingHeaders += $file

        if (-not $WhatIf -and -not $Verify) {
            $createdYear = Get-FileCreatedYear -FilePath $file.FullName
            $header = Get-FileHeader -FileName $file.Name -CreatedYear $createdYear

            # Handle BOM if present
            $encoding = [System.Text.UTF8Encoding]::new($true)
            $newContent = $header + $content.TrimStart()

            [System.IO.File]::WriteAllText($file.FullName, $newContent, $encoding)
            $processedCount++
            Write-Host "Added header to: $($file.FullName -replace [regex]::Escape($rootDir), '')"
        }
    }
}

if ($WhatIf) {
    if ($missingHeaders.Count -eq 0) {
        Write-Host "All files have headers." -ForegroundColor Green
    } else {
        Write-Host "Files missing headers:" -ForegroundColor Yellow
        foreach ($file in $missingHeaders) {
            Write-Host "  $($file.FullName -replace [regex]::Escape($rootDir), '')"
        }
        Write-Host "`n$($missingHeaders.Count) file(s) would be modified." -ForegroundColor Yellow
    }
} elseif ($Verify) {
    if ($missingHeaders.Count -eq 0) {
        Write-Host "All files have headers." -ForegroundColor Green
        exit 0
    } else {
        Write-Host "Files missing headers:" -ForegroundColor Red
        foreach ($file in $missingHeaders) {
            Write-Host "  $($file.FullName -replace [regex]::Escape($rootDir), '')"
        }
        Write-Host "`n$($missingHeaders.Count) file(s) missing headers. Run './scripts/Add-FileHeaders.ps1' to fix." -ForegroundColor Red
        exit 1
    }
} else {
    if ($processedCount -eq 0) {
        Write-Host "All files already have headers." -ForegroundColor Green
    } else {
        Write-Host "`nAdded headers to $processedCount file(s)." -ForegroundColor Green
    }
}
