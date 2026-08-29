# Releasing BodyFlux

Publishing a version is one command. This document explains what that command does, what GitHub
does afterwards, and what to do when something goes wrong.

## The rule everything rests on

> The `<Version>` in `BodyFlux/BodyFlux.csproj` and the newest entry in
> `BodyFlux/Windows/ChangelogEntries.cs` must always name the same version.

Both are authored during development, in the same commit as the work they describe. Nothing is
written at release time. `release.ps1` verifies the rule and refuses to publish if it is broken.

## During development

Work on `dev` as usual. Every push builds and runs the tests on GitHub (`.github/workflows/ci.yml`).
Nothing is ever published from a branch.

When you close a change that users will notice:

```powershell
./bump-version.ps1          # build | patch | minor | major
```

then add its entry at the top of `ChangelogEntries.cs`, newest first:

```csharp
new ChangelogEntry(new Version(0, 8, 3, 3), "Short Title",
[
    "What changed, in plain language, from the user's point of view.",
]),
```

Commit both together. The convention for the commit message is the changelog title followed by the
version, e.g. `Preset Fixes 0.8.3.2`.

That title and those bullets end up in three places: the in-game "What's New" popup, the GitHub
release title, and the GitHub release notes. They are written once, here.

**If you change something else before publishing, add bullets to the entry that is already at the
top instead of creating a new one.** There is at most one unreleased version in flight. Bump again
only if the accumulated work changes the magnitude (a `build` that grew into a `patch`).

You can load the plugin in dev mode and see the popup exactly as users will, because it is driven by
the changelog entry, not by the assembly version.

## Publishing

```powershell
./release.ps1
```

It writes nothing and commits nothing. In order, it:

1. checks you are on `dev` with a clean working copy, and syncs with `origin` (this is where the
   previous release's `pluginmaster.json` commit gets picked up)
2. reads the version from the csproj and the newest changelog entry, and requires them to match
3. refuses to continue if that version is already tagged
4. builds Release, runs the tests, and checks that the packaged manifest carries that exact version
5. pushes `dev`, fast-forwards `master`, and pushes the tag

Use `./release.ps1 -DryRun` to run everything up to step 4 and stop.

Pushing the tag is the only thing in this repository that publishes.

## What GitHub does

`.github/workflows/release.yml` triggers on a four-part version tag and takes about two minutes:

1. validates again that the tag matches the csproj, that the changelog entry exists, and that the
   tagged commit is on `master`
2. builds Release and runs the tests on a clean runner
3. verifies the `BodyFlux.json` **inside the zip** carries the tagged version
4. creates the GitHub release with `BodyFlux.zip`, titled and described from the changelog entry
5. only then, rewrites `pluginmaster.json` on `master` and commits it as `[CI] pluginmaster X.Y.Z.B`

Step 5 is last on purpose. `pluginmaster.json` is what Dalamud polls, so it must never advertise a
download that has not been uploaded yet. If any earlier step fails, there is no release and no
commit: nothing is half-published.

## Ownership

| File | Written by |
| --- | --- |
| `BodyFlux.csproj` `<Version>` | you, via `bump-version.ps1` |
| `ChangelogEntries.cs` | you, by hand |
| `pluginmaster.json` | the release workflow, exclusively. Do not edit it. |
| tags and GitHub releases | the release workflow |

## When something goes wrong

**"Version mismatch" from `release.ps1`.** You bumped without writing the changelog entry, or wrote
the entry without bumping. Fix whichever is missing and commit it.

**"Already tagged".** That version is published. Bump before releasing again.

**The workflow fails after the tag was pushed.** No release was created and `pluginmaster.json` was
not touched, so users see nothing. Fix the problem on `dev`, then either delete the tag and release
the same version again:

```powershell
git push origin :0.8.3.3
git tag -d 0.8.3.3
```

or just bump to the next version and move on. `master` is left carrying a version with no release
behind it, which is harmless and corrects itself on the next release.

**Withdrawing a published version.** Delete or mark the release on GitHub and revert the
`[CI] pluginmaster` commit on `master`. This is deliberately manual.

## Dalamud API level

The workflows build against the stable Dalamud distribution
(`https://goatcorp.github.io/dalamud-distrib/latest.zip`), which is what
`Dalamud.NET.Sdk` expects locally. When Dalamud moves to a new API level and you need to stay on the
old one, pass the pinned folder to the composite action instead:

```yaml
- uses: ./.github/actions/prepare-environment
  with:
    dalamud-distrib: 'v15/'
```
