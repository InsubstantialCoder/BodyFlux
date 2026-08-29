#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes the version that is already committed in the working tree.

.DESCRIPTION
    This script writes nothing and commits nothing. The version and its changelog entry are authored
    during development, in the same commit as the work they describe (bump-version.ps1 for the
    former, BodyFlux/Windows/ChangelogEntries.cs for the latter), so by the time you run this the
    repository already says exactly what is going to be released.

    All it does is verify that, prove the build is good, and hand the version to GitHub Actions:

        dev is clean and up to date  ->  csproj version == newest changelog entry
                                     ->  that version has no tag yet
                                     ->  tests pass, Release package carries the right version
                                     ->  push dev, fast-forward master, push the tag

    Pushing the tag is what triggers .github/workflows/release.yml, which builds the artifact again
    from the tagged commit, creates the GitHub release, and only then updates pluginmaster.json.
    Nothing else in the repository publishes anything.

.PARAMETER DryRun
    Run every check and the full build, then stop before pushing anything.

.EXAMPLE
    ./release.ps1
    ./release.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [string]$Remote        = 'origin',
    [string]$DevBranch     = 'dev',
    [string]$ReleaseBranch = 'master'
)

$ErrorActionPreference = 'Stop'
# Exit codes are inspected by hand throughout, so native commands must not throw on their own.
$PSNativeCommandUseErrorActionPreference = $false

Set-Location $PSScriptRoot

# Every failure here is something the user has to act on, so report it as a plain sentence rather
# than a PowerShell stack trace.
trap {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

function Write-Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Write-Ok  ([string]$message) { Write-Host "    $message" -ForegroundColor DarkGray }

# git and dotnet write ordinary progress to stderr ("Switched to branch...", "To github.com..."),
# and Windows PowerShell turns those into terminating errors whenever this script's own output is
# redirected or piped. Every native call goes through here, with the preference relaxed for the
# duration, and its exit code checked explicitly instead.
function Invoke-Native {
    param([scriptblock]$Command)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $previous }
}

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    Invoke-Native { & git @Arguments }
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed (exit $LASTEXITCODE)." }
}

function Get-GitOutput {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    Invoke-Native { & git @Arguments 2> $null }
}

function Test-GitQuiet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    Invoke-Native { & git @Arguments *> $null }
    return ($LASTEXITCODE -eq 0)
}

function Invoke-Tool {
    param([string]$What, [scriptblock]$Command)
    Invoke-Native $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)." }
}

# ── 1. The working copy has to be in a publishable state ─────────────────────────────────────────
Write-Step 'Checking the working copy'

if (-not (Test-GitQuiet rev-parse --git-dir)) { throw 'Not a git repository.' }
$branch = "$(Get-GitOutput rev-parse --abbrev-ref HEAD)".Trim()
if ($branch -ne $DevBranch) {
    throw "You are on '$branch'. Releases are cut from '$DevBranch'."
}
if ((Get-GitOutput status --porcelain)) {
    throw "The working copy has uncommitted changes. Commit or stash them first."
}
Write-Ok "on $branch, working copy clean"

Write-Step 'Syncing with the remote'
Invoke-Git fetch $Remote --tags --prune

if (Test-GitQuiet rev-parse --verify --quiet "$Remote/$DevBranch") {
    Invoke-Native { & git merge --ff-only "$Remote/$DevBranch" *> $null }
    if ($LASTEXITCODE -ne 0) {
        throw "'$DevBranch' and '$Remote/$DevBranch' have diverged. Reconcile them before releasing."
    }
}
# The previous release left a pluginmaster.json commit on master; take it now so this release is
# cut from a branch that already contains it and the fast-forward merge below stays possible.
Invoke-Native { & git merge --ff-only "$Remote/$ReleaseBranch" *> $null }
if ($LASTEXITCODE -ne 0) {
    throw "'$DevBranch' cannot fast-forward '$Remote/$ReleaseBranch'. Merge master into $DevBranch first."
}
Write-Ok "$DevBranch is up to date with $Remote/$DevBranch and $Remote/$ReleaseBranch"

# ── 2. The invariant: csproj version == newest changelog entry, and it is unreleased ─────────────
Write-Step 'Resolving the version to publish'

$csproj = Get-Content -Raw 'BodyFlux/BodyFlux.csproj'
if ($csproj -notmatch '<Version>(\d+\.\d+\.\d+\.\d+)</Version>') {
    throw 'No <Version>X.Y.Z.B</Version> found in BodyFlux/BodyFlux.csproj.'
}
$version = $Matches[1]

$entry = & (Join-Path $PSScriptRoot '.github/scripts/Get-ChangelogEntry.ps1')
if ("$($entry.Version)" -ne $version) {
    throw @"
Version mismatch:
  BodyFlux.csproj      $version
  newest changelog     $($entry.Version)
Bump the csproj (./bump-version.ps1) and write the matching changelog entry, in the same commit.
"@
}
if (($entry.Bullets | Where-Object { $_.Trim() }).Count -eq 0) {
    throw "The changelog entry for $version has no highlights. Write what changed before publishing."
}

$taggedHere   = Test-GitQuiet rev-parse --verify --quiet "refs/tags/$version"
$taggedRemote = [bool](Get-GitOutput ls-remote --tags $Remote "refs/tags/$version")
if ($taggedRemote) {
    throw "$version is already tagged on $Remote, so it has been published. Bump before releasing again."
}
if ($taggedHere) {
    # A tag that exists here but not on the remote means a previous run got as far as tagging and
    # then failed to push it, so this is a resumable state rather than a published version.
    throw @"
$version is tagged locally but was never pushed, so a previous release did not finish.
Push it to publish that build:   git push $Remote $version
Or drop it and start over:       git tag -d $version
"@
}
Write-Ok "$version - $($entry.Title) ($($entry.Bullets.Count) highlights)"

# ── 3. Prove the build before anything leaves the machine ────────────────────────────────────────
Write-Step 'Building and testing'

Invoke-Tool 'Release build' { & dotnet build 'BodyFlux/BodyFlux.csproj' -c Release --nologo }
Invoke-Tool 'Tests'         { & dotnet test 'BodyFlux.Tests/BodyFlux.Tests.csproj' -c Release --nologo }

$package  = 'BodyFlux/bin/Release/BodyFlux/BodyFlux.zip'
$manifest = 'BodyFlux/bin/Release/BodyFlux/BodyFlux.json'
if (-not (Test-Path $package))  { throw "Release package not produced at $package." }
if (-not (Test-Path $manifest)) { throw "Packaged manifest not produced at $manifest." }
$packaged = (Get-Content -Raw $manifest | ConvertFrom-Json).AssemblyVersion
if ($packaged -ne $version) {
    throw "The packaged manifest says $packaged but this release is $version."
}
Write-Ok "package carries $packaged"

# ── 4. Publish ───────────────────────────────────────────────────────────────────────────────────
if ($DryRun) {
    Write-Host ''
    Write-Host "Dry run: everything checks out for $version - $($entry.Title)." -ForegroundColor Green
    Write-Host "Re-run without -DryRun to push $DevBranch, fast-forward $ReleaseBranch and tag $version."
    return
}

Write-Step "Publishing $version"
Invoke-Git push $Remote $DevBranch

$returned = $false
try {
    Invoke-Git checkout $ReleaseBranch
    Invoke-Git merge --ff-only $DevBranch
    Invoke-Git push $Remote $ReleaseBranch
    # The tag goes last and only once master carries the commit: release.yml refuses to publish a
    # tag that is not on master, and the workflow commits pluginmaster.json to master at the end.
    Invoke-Git tag $version
    Invoke-Git push $Remote $version
} finally {
    Invoke-Native { & git checkout $DevBranch *> $null }
    $returned = ($LASTEXITCODE -eq 0)
}
if (-not $returned) { Write-Warning "Could not switch back to '$DevBranch'. Check 'git status'." }

$url = "$(Get-GitOutput remote get-url $Remote)".Trim() -replace '\.git$', '' -replace '^git@github\.com:', 'https://github.com/'
Write-Host ''
Write-Host "$version - $($entry.Title) is on its way." -ForegroundColor Green
Write-Host "GitHub Actions is building it now: $url/actions"
Write-Host "The release and pluginmaster.json update land in about two minutes."
