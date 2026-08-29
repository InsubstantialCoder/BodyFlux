#!/usr/bin/env pwsh
# Single source of truth for the plugin version is <Version> in BodyFlux/BodyFlux.csproj.
# This script bumps it, and that is all it does.
#
# Bump it in the same commit as the work it covers, together with the matching entry in
# BodyFlux/Windows/ChangelogEntries.cs. release.ps1 later refuses to publish unless those two agree,
# so a version and its description can never drift apart.
#
# (BodyFlux/BodyFlux.json has no version on purpose: DalamudPackager stamps AssemblyVersion from the
#  csproj <Version> at build time. pluginmaster.json is not touched here either, it is owned by
#  .github/workflows/release.yml, which rewrites it only after the matching GitHub release exists.
#  Editing it by hand would advertise a download that is not uploaded yet.)
#
#   ./bump-version.ps1            # build:  0.5.0.0 -> 0.5.0.1
#   ./bump-version.ps1 patch      # patch:  0.5.0.4 -> 0.5.1.0
#   ./bump-version.ps1 minor      # minor:  0.5.3.2 -> 0.6.0.0   (e.g. 0.5 -> 0.6)
#   ./bump-version.ps1 major      # major:  0.6.4.1 -> 1.0.0.0
#   ./bump-version.ps1 show       # don't bump; just print the current version
param([ValidateSet('build', 'patch', 'minor', 'major', 'show')][string]$Part = 'build')

$ErrorActionPreference = 'Stop'
$csproj = Join-Path $PSScriptRoot 'BodyFlux/BodyFlux.csproj'

# ── read current version from the csproj (source of truth) ──────────────────
$text = Get-Content -Raw -Path $csproj
if ($text -notmatch '<Version>(\d+)\.(\d+)\.(\d+)\.(\d+)</Version>') {
    throw "No <Version>X.Y.Z.B</Version> found in $csproj"
}
$maj = [int]$Matches[1]; $min = [int]$Matches[2]; $pat = [int]$Matches[3]; $bld = [int]$Matches[4]

if ($Part -eq 'show') {
    Write-Host "BodyFlux version  -> $maj.$min.$pat.$bld"
    return
}

switch ($Part) {
    'major' { $maj++; $min = 0; $pat = 0; $bld = 0 }
    'minor' { $min++; $pat = 0; $bld = 0 }
    'patch' { $pat++; $bld = 0 }
    'build' { $bld++ }
}
$new = "$maj.$min.$pat.$bld"

# ── write the csproj ────────────────────────────────────────────────────────
$text = [regex]::Replace($text, '<Version>\d+\.\d+\.\d+\.\d+</Version>', "<Version>$new</Version>", 1)
Set-Content -Path $csproj -Value $text -NoNewline -Encoding utf8

Write-Host "BodyFlux version  -> $new"
Write-Host "Next: add the ChangelogEntry for $new in BodyFlux/Windows/ChangelogEntries.cs."
