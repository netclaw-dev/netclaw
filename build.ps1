. "$PSScriptRoot\scripts\getReleaseNotes.ps1"
. "$PSScriptRoot\scripts\bumpVersion.ps1"

######################################################################
# Step 1: Grab release notes and update solution metadata
######################################################################
$releaseNotes = Get-ReleaseNotes -MarkdownFile (Join-Path -Path $PSScriptRoot -ChildPath "RELEASE_NOTES.md")

if ([string]::IsNullOrWhiteSpace($releaseNotes.Version) -or
    [string]::IsNullOrWhiteSpace($releaseNotes.ReleaseNotes)) {
    throw "Release notes must contain a version and release notes text."
}

# Inject release notes into Directory.Build.props.
UpdateVersionAndReleaseNotes -ReleaseNotesResult $releaseNotes -XmlFilePath (Join-Path -Path $PSScriptRoot -ChildPath "Directory.Build.props")

Write-Output "Updated release metadata for version $($releaseNotes.Version)."
