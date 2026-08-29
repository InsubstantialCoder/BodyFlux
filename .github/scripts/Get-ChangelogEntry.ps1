#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Reads one entry out of BodyFlux/Windows/ChangelogEntries.cs.

.DESCRIPTION
    ChangelogEntries.cs is the single place a version's user-facing description is written, and it
    feeds three destinations: the in-game "What's New" popup, the GitHub release title, and the
    GitHub release notes. This parser is what lets the last two come from the first, so the text is
    authored exactly once.

    Entries look like this, newest first:

        new ChangelogEntry(new Version(0, 8, 3, 2), "Preset Fixes",
        [
            "a bullet long enough " +
                "to be split across source lines.",
            "another bullet.",
        ]),

    Bullets are C# string literals joined with '+', so a bullet ends at the comma that separates it
    from the next one, not at the end of a literal.

.PARAMETER Version
    Return this exact version (e.g. 0.8.3.2). Omit to return the newest entry in the file.

.PARAMETER Markdown
    Emit the release-notes body (one "- bullet" per line) instead of the parsed object.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Path,
    [switch]$Markdown
)

$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $Path     = Join-Path $repoRoot 'BodyFlux/Windows/ChangelogEntries.cs'
}
if (-not (Test-Path $Path)) { throw "Changelog file not found: $Path" }
$source = Get-Content -Raw -Path $Path

$q = [char]34
$bs = [char]92
# "((?:[^"\\]|\\.)*)"  -- a C# string literal, escaped quotes and backslashes included.
$literalPattern = "$q((?:[^$q$bs$bs]|$bs$bs.)*)$q"
$headerPattern  = "new\s+ChangelogEntry\s*\(\s*new\s+Version\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)\s*,\s*$literalPattern\s*,"

function Convert-CSharpLiteral([string]$raw) {
    # Park escaped backslashes on a character that cannot occur in the source, so the quote
    # unescaping below cannot misread the backslash they were protecting.
    $bs     = [char]92
    $parked = [char]1
    $raw.Replace("$bs$bs", "$parked").Replace("$bs`"", '"').Replace("$bs'", "'").Replace("$parked", "$bs")
}

$entries = @()
foreach ($header in [regex]::Matches($source, $headerPattern)) {
    $ver = [version]("{0}.{1}.{2}.{3}" -f $header.Groups[1].Value, $header.Groups[2].Value,
                                          $header.Groups[3].Value, $header.Groups[4].Value)

    # Body of the highlight list: from the '[' after the header to its matching ']'.
    $open = $source.IndexOf('[', $header.Index + $header.Length)
    if ($open -lt 0) { throw "Malformed changelog entry for ${ver}: no highlight list." }
    $depth = 0; $close = -1
    for ($i = $open; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '[') { $depth++ }
        elseif ($source[$i] -eq ']') { $depth--; if ($depth -eq 0) { $close = $i; break } }
    }
    if ($close -lt 0) { throw "Malformed changelog entry for ${ver}: unterminated highlight list." }
    $body = $source.Substring($open + 1, $close - $open - 1)

    # Walk the literals in order: a ',' between two of them starts a new bullet, a '+' continues the
    # current one.
    $bullets = New-Object System.Collections.Generic.List[string]
    $current = $null
    $cursor  = 0
    foreach ($lit in [regex]::Matches($body, $literalPattern)) {
        $between = $body.Substring($cursor, $lit.Index - $cursor)
        $text    = Convert-CSharpLiteral $lit.Groups[1].Value
        if ($null -eq $current)      { $current = $text }
        elseif ($between -match ',') { [void]$bullets.Add($current); $current = $text }
        else                         { $current += $text }
        $cursor = $lit.Index + $lit.Length
    }
    if ($null -ne $current) { [void]$bullets.Add($current) }

    $entries += [pscustomobject]@{
        Version = $ver
        Title   = Convert-CSharpLiteral $header.Groups[5].Value
        Bullets = $bullets.ToArray()
    }
}

if ($entries.Count -eq 0) { throw "No changelog entries found in $Path" }

if ($Version) {
    $wanted = [version]$Version
    $entry  = $entries | Where-Object { $_.Version -eq $wanted } | Select-Object -First 1
    if (-not $entry) { throw "No changelog entry for version $Version in $Path" }
} else {
    # Newest by version, not by position in the file, so a misfiled entry cannot silently win.
    $entry = $entries | Sort-Object Version -Descending | Select-Object -First 1
}

if ($Markdown) {
    ($entry.Bullets | ForEach-Object { "- $_" }) -join "`n"
} else {
    $entry
}
