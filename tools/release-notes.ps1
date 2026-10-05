<#
.SYNOPSIS
  Writes the notes of a GitHub release: what changed since the previous version (the subjects of the commits between the two tags),
  then how to install.

.DESCRIPTION
  Used by .github/workflows/release.yml. The commits go straight to main (there are no pull requests), so GitHub's own generated notes
  list nothing; this reads the commit subjects instead. Write commit subjects so that they read well here (see "Commit conventions" in
  the README). Needs the tags in the clone (the workflow checks out with fetch-depth 0).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\release-notes.ps1 -Tag v1.1.1
#>
param(
    [Parameter(Mandatory = $true)] [string] $Tag,
    [string] $OutFile = "release-notes.md",
    [int] $Max = 40
)

$ErrorActionPreference = "Stop"

# The tag just before this one, by version order (v1.10.0 comes after v1.9.0); none for the first release.
$tags = @(& git tag --list "v*" --sort=-version:refname)
$index = [array]::IndexOf($tags, $Tag)
$previous = $null
if ($index -ge 0 -and ($index + 1) -lt $tags.Count) { $previous = $tags[$index + 1] }

$range = if ($previous) { "$previous..$Tag" } else { $Tag }
$subjects = @(& git log $range --no-merges "--pretty=format:%s") | Where-Object { $_ -and $_.Trim() } | Select-Object -First $Max

$version = $Tag.TrimStart("v")
$lines = @()
$lines += "## What's new in $version"
$lines += ""
if ($subjects.Count -gt 0) {
    foreach ($s in $subjects) { $lines += "- $($s.Trim())" }
    if ($previous) { $lines += ""; $lines += "Changes since $previous." }
} else {
    $lines += "- No changes were recorded for this version."
}
$lines += ""
$lines += "**Install:** download the ``Setup`` file below and run it (no administrator rights needed). Or download the zip, unzip it and run ``InterviewCoach.App.exe`` with the ``Prompts`` folder next to it."
$lines += ""
$lines += "Windows may show a SmartScreen warning because the program is not code-signed: choose **More info**, then **Run anyway**."

Set-Content -Path $OutFile -Value $lines -Encoding UTF8
Write-Host "Wrote $OutFile ($($subjects.Count) change(s) since $(if ($previous) { $previous } else { 'the start' }))."
