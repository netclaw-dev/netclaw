# Validates release-note parsing without modifying repository files.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\getReleaseNotes.ps1")
. (Join-Path $PSScriptRoot "..\bumpVersion.ps1")

$propsPath = [System.IO.Path]::GetTempFileName()
try {
    @"
<Project>
  <PropertyGroup>
    <VersionPrefix>0.0.0</VersionPrefix>
    <VersionSuffix>old</VersionSuffix>
    <PackageReleaseNotes>old notes</PackageReleaseNotes>
  </PropertyGroup>
</Project>
"@ | Set-Content -Path $propsPath -NoNewline

    $updates = @(
        @{ Version = "0.28.0"; Prefix = "0.28.0"; Suffix = ""; Notes = "Stable notes." },
        @{ Version = "0.28.0-beta.1"; Prefix = "0.28.0"; Suffix = "beta.1"; Notes = "First beta notes." },
        @{ Version = "0.28.0-beta.10"; Prefix = "0.28.0"; Suffix = "beta.10"; Notes = "Tenth beta notes." },
        @{ Version = "0.28.0"; Prefix = "0.28.0"; Suffix = ""; Notes = "Stable after beta." }
    )

    foreach ($update in $updates) {
        $releaseNotes = [PSCustomObject]@{
            Version = $update.Version
            ReleaseNotes = $update.Notes
        }
        UpdateVersionAndReleaseNotes -ReleaseNotesResult $releaseNotes -XmlFilePath $propsPath
        [xml]$props = Get-Content -Path $propsPath
        if ($props.Project.PropertyGroup.VersionPrefix -ne $update.Prefix) {
            throw "Version $($update.Version) wrote the wrong VersionPrefix."
        }
        if ($props.Project.PropertyGroup.VersionSuffix -ne $update.Suffix) {
            throw "Version $($update.Version) wrote the wrong VersionSuffix."
        }
        if ($props.Project.PropertyGroup.PackageReleaseNotes -ne $update.Notes) {
            throw "Version $($update.Version) changed the wrong release notes."
        }
    }
} finally {
    Remove-Item -LiteralPath $propsPath -Force -ErrorAction SilentlyContinue
}

$fixturePath = [System.IO.Path]::GetTempFileName()
try {
    @"
# Release Notes

## 1.2.3 (2026-01-02)

Summary text.

### Features
- First feature.

## 1.2.2 (2025-12-01)

Older release.
"@ | Set-Content -Path $fixturePath -NoNewline

    $result = Get-ReleaseNotes -MarkdownFile $fixturePath
    if ($result.Version -ne "1.2.3") { throw "The parser returned the wrong version." }
    if ($result.Date -ne "2026-01-02") { throw "The parser returned the wrong date." }
    if ($result.ReleaseNotes -notmatch "First feature" -or $result.ReleaseNotes -match "Older release") {
        throw "The parser returned the wrong release section."
    }
} finally {
    Remove-Item -LiteralPath $fixturePath -Force -ErrorAction SilentlyContinue
}

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$actual = Get-ReleaseNotes -MarkdownFile (Join-Path $repositoryRoot "RELEASE_NOTES.md")
if ([string]::IsNullOrWhiteSpace($actual.Version) -or [string]::IsNullOrWhiteSpace($actual.ReleaseNotes)) {
    throw "The repository release notes did not produce complete metadata."
}

$invalidPath = [System.IO.Path]::GetTempFileName()
try {
    "# No release header" | Set-Content -Path $invalidPath
    try {
        Get-ReleaseNotes -MarkdownFile $invalidPath | Out-Null
        throw "The parser accepted release notes without a release header."
    } catch {
        if ($_.Exception.Message -notmatch "Could not find a release header") { throw }
    }
} finally {
    Remove-Item -LiteralPath $invalidPath -Force -ErrorAction SilentlyContinue
}

Write-Output "Release-note parser smoke test passed."
