[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [Guid] $AppId,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [ValidateLength(1, 32)]
    [string] $DeveloperName,

    [Parameter(Mandatory)]
    [Uri] $PrivacyUrl,

    [Parameter(Mandatory)]
    [Uri] $TermsOfUseUrl,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.0.0',

    [string] $OutputPath = (Join-Path $PSScriptRoot 'netclaw-teams.zip'),

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($policyUrl in @($PrivacyUrl, $TermsOfUseUrl)) {
    if (-not $policyUrl.IsAbsoluteUri -or $policyUrl.Scheme -ne 'https') {
        throw "Policy URLs must use absolute HTTPS addresses: $policyUrl"
    }
}

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $outputFullPath -PathType Container) {
    throw "OutputPath must name a ZIP file, not a directory: $outputFullPath"
}

if ((Test-Path -LiteralPath $outputFullPath -PathType Leaf) -and -not $Force) {
    throw "The output package already exists: $outputFullPath. Use -Force to replace it."
}

$outputDirectory = Split-Path -Parent $outputFullPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$stagingPath = Join-Path ([IO.Path]::GetTempPath()) ("netclaw-teams-package-{0}" -f [Guid]::NewGuid())

try {
    New-Item -ItemType Directory -Path $stagingPath | Out-Null

    $templatePath = Join-Path $PSScriptRoot 'manifest.template.json'
    $manifest = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json
    $manifest.id = $AppId.ToString()
    $manifest.version = $Version
    $manifest.bots[0].botId = $AppId.ToString()
    $manifest.webApplicationInfo.id = $AppId.ToString()
    $manifest.developer.name = $DeveloperName
    $manifest.developer.privacyUrl = $PrivacyUrl.AbsoluteUri
    $manifest.developer.termsOfUseUrl = $TermsOfUseUrl.AbsoluteUri

    $manifestPath = Join-Path $stagingPath 'manifest.json'
    $manifestJson = $manifest | ConvertTo-Json -Depth 20
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'color.png') -Destination $stagingPath
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'outline.png') -Destination $stagingPath

    Compress-Archive -LiteralPath @(
        $manifestPath,
        (Join-Path $stagingPath 'color.png'),
        (Join-Path $stagingPath 'outline.png')
    ) -DestinationPath $outputFullPath -Force

    Write-Output $outputFullPath
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}
