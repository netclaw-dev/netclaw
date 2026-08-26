# Validates release-note parsing without modifying repository files.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\getReleaseNotes.ps1")

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
