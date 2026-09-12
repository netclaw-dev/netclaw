function Get-ReleaseNotes {
    param (
        [Parameter(Mandatory=$true)]
        [string]$MarkdownFile
    )

    $content = Get-Content -Path $MarkdownFile -Raw
    $pattern = '(?ms)^##\s+(?<version>\S+)\s+\((?<date>\d{4}-\d{2}-\d{2})\)\s*\r?\n(?<notes>.*?)(?=^##\s+|\z)'
    $match = [regex]::Match($content, $pattern)

    if (-not $match.Success) {
        throw "Could not find a release header in '$MarkdownFile'. Expected '## <version> (<yyyy-MM-dd>)'."
    }

    $releaseNotes = $match.Groups['notes'].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($releaseNotes)) {
        throw "Release '$($match.Groups['version'].Value)' in '$MarkdownFile' has no release notes."
    }

    return [PSCustomObject]@{
        Version      = $match.Groups['version'].Value
        Date         = $match.Groups['date'].Value
        ReleaseNotes = $releaseNotes
    }
}

# Call function example:
#$result = Get-ReleaseNotes -MarkdownFile "$PSScriptRoot\RELEASE_NOTES.md"
#Write-Output "Version: $($result.Version)"
#Write-Output "Date: $($result.Date)"
#Write-Output "Release Notes:"
#Write-Output $result.ReleaseNotes
