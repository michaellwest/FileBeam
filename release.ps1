<#
.SYNOPSIS
    Build FileBeam release binaries for all platforms and create a git tag.

.DESCRIPTION
    Runs tests, publishes self-contained binaries for win-x64, linux-x64, and
    osx-arm64, generates release notes from git history, and creates + pushes
    a version tag.

.PARAMETER Version
    Semantic version string (e.g. 1.0.0). Used for the assembly version and
    the git tag (v1.0.0).

.PARAMETER SkipTests
    Skip running tests before building.

.PARAMETER SkipTag
    Skip creating and pushing the git tag.

.EXAMPLE
    .\release.ps1 -Version 1.0.0
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipTests,

    [switch]$SkipTag
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$releaseDir = Join-Path $repoRoot 'release'
$srcProject = Join-Path $repoRoot 'src'
$testProject = Join-Path $repoRoot 'tests' 'FileBeam.Tests'

$profiles = @(
    @{ Name = 'win-x64';    Rid = 'win-x64';    Exe = 'filebeam.exe'; Output = "filebeam-$Version-win-x64.exe" }
    @{ Name = 'linux-x64';  Rid = 'linux-x64';  Exe = 'filebeam';     Output = "filebeam-$Version-linux-x64" }
    @{ Name = 'osx-arm64';  Rid = 'osx-arm64';  Exe = 'filebeam';     Output = "filebeam-$Version-osx-arm64" }
)

Write-Host "`n=== FileBeam Release v$Version ===" -ForegroundColor Cyan

# --- Tests ---
if (-not $SkipTests) {
    Write-Host "`n--- Running tests ---" -ForegroundColor Yellow
    dotnet test $testProject
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Tests failed. Aborting release."
    }
}

# --- Clean release directory ---
if (Test-Path $releaseDir) {
    Remove-Item $releaseDir -Recurse -Force
}
New-Item $releaseDir -ItemType Directory | Out-Null

# --- Publish each platform ---
foreach ($p in $profiles) {
    Write-Host "`n--- Publishing $($p.Name) ---" -ForegroundColor Yellow
    dotnet publish $srcProject -p:PublishProfile=$($p.Name) /p:Version=$Version
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed for $($p.Name)."
    }

    $sourcePath = Join-Path $srcProject 'bin' 'Release' 'net10.0' $p.Rid 'publish' $p.Exe
    $destPath = Join-Path $releaseDir $p.Output
    Copy-Item $sourcePath $destPath
    $size = (Get-Item $destPath).Length / 1MB
    Write-Host ("  -> {0} ({1:N1} MB)" -f $p.Output, $size) -ForegroundColor Green
}

# --- Release notes ---
Write-Host "`n--- Generating release notes ---" -ForegroundColor Yellow
$lastTag = git describe --tags --abbrev=0 2>$null
if ($lastTag) {
    $range = "$lastTag..HEAD"
    $header = "Changes since $lastTag"
} else {
    $range = $null
    $header = "Initial release"
}

$notes = @("# FileBeam v$Version", "", "## $header", "")
if ($range) {
    $commits = git log --oneline $range
} else {
    $commits = git log --oneline
}
foreach ($c in $commits) {
    $notes += "- $c"
}

$notesPath = Join-Path $releaseDir 'RELEASE-NOTES.md'
$notes | Out-File -FilePath $notesPath -Encoding utf8
Write-Host "  -> RELEASE-NOTES.md ($($commits.Count) commits)"

# --- Git tag ---
if (-not $SkipTag) {
    $tagName = "v$Version"
    Write-Host "`n--- Creating tag $tagName ---" -ForegroundColor Yellow
    git tag $tagName
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to create tag $tagName. Does it already exist?"
    }
    git push origin $tagName
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to push tag $tagName."
    }
    Write-Host "  -> Tag $tagName pushed to origin" -ForegroundColor Green
}

# --- Summary ---
Write-Host "`n=== Release v$Version complete ===" -ForegroundColor Cyan
Write-Host "Output directory: $releaseDir"
Get-ChildItem $releaseDir | ForEach-Object {
    $size = if ($_.Length -gt 1MB) { "{0:N1} MB" -f ($_.Length / 1MB) } else { "{0:N0} KB" -f ($_.Length / 1KB) }
    Write-Host "  $($_.Name)  ($size)"
}
