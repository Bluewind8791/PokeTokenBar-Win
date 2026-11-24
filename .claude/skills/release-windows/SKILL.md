---
name: release-windows
description: Release the Windows port of this personal fork as a GitHub Release on Bluewind8791/PokeTokenBar-Win (single PokeTokenBar.exe, tag v<macOS version>-win.<n>) and update the app running on this PC. In this fork, every release request means this skill, e.g. "배포해줘", "릴리스 올려줘", "배포", "release", "win.3 배포". Never run scripts/release.sh here.
---

# Release the Windows port

This checkout is a personal fork. `CLAUDE.md` maps "배포 / 릴리스" to `scripts/release.sh`, the macOS release of the
original repository. **Do not follow that here.** `release.sh` pushes to `origin` (upstream), creates an upstream
GitHub Release and bumps the Homebrew cask. In this fork a release request means this skill.

- `fork` = `Bluewind8791/PokeTokenBar-Win`: push and release here only. Pass `-R Bluewind8791/PokeTokenBar-Win` to
  every `gh release` command.
- `origin` = upstream: never push, never release.

Run commands from the repository root. `$S` below is the session scratchpad directory.

## 1. Check the state

```bash
git status --short --untracked-files=no   # must be empty; stop and report if not
git fetch fork --tags
git status -sb                            # main must not be behind fork/main
```

## 2. Choose the version

Windows releases are tagged `v<macOS version>-win.<n>`. The macOS version is the newest upstream release tag
merged into `main`:

```bash
git describe --tags --abbrev=0 --match 'v[0-9]*' --exclude '*-win.*' main   # e.g. v2.5.7
gh release list -R Bluewind8791/PokeTokenBar-Win --limit 5                  # last Windows release
```

- Same macOS version as the last Windows release: `-win.<n+1>` (`v2.5.7-win.2` → `v2.5.7-win.3`).
- Newer macOS version (after a `sync-upstream` merge): `-win.1` (`v2.5.8-win.1`).
- The user names a version: use it as given.

The tag must not exist yet (`git tag -l <tag>`). Plain `vX.Y.Z` tags are upstream tags copied into the fork. Never
reuse or move them.

## 3. Test

```bash
dotnet test windows/tests/PokeTokenBar.Core.Tests
dotnet test windows/tests/PokeTokenBar.App.Tests
```

Both must pass. Stop on a failure.

## 4. Bump the version and push

Set `<Version>` in `windows/src/PokeTokenBar.App/PokeTokenBar.App.csproj` to the new version without the `v`
(`2.5.7-win.3`). The update check compares the `-win.<n>` part, so the build version must carry it.

```bash
git add windows/src/PokeTokenBar.App/PokeTokenBar.App.csproj
git diff --cached --stat                  # only the csproj
git commit -m "chore(windows): bump version to 2.5.7-win.3"
git push fork main
```

Stage files by name. Never `git add -f` a directory: it adds `bin/`, `obj/` and `artifacts/`, and GitHub rejects
the 166 MB exe. No `Co-Authored-By` trailer.

## 5. Build

Build into the scratchpad from the committed HEAD. Do not use `windows/scripts/publish.ps1`: it deletes
`windows/artifacts`, which fails while the app runs from there, and it builds a zip the release does not use.

```powershell
dotnet publish windows\src\PokeTokenBar.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=2.5.7-win.3 -o "$S\release"
(Get-Item "$S\release\PokeTokenBar.exe").VersionInfo.ProductVersion   # 2.5.7-win.3+<HEAD sha>
(Get-FileHash "$S\release\PokeTokenBar.exe").Hash
```

The product version must be the new version plus the HEAD sha. Otherwise stop.

## 6. Write the notes and confirm

Write the notes in English to `$S\release-notes.md`. Base them on the `windows/` commits since the last Windows
release tag (`git log --oneline <last tag>..HEAD -- windows`). Use this shape:

```markdown
Personal Windows build of [PokeTokenBar vX.Y.Z](https://github.com/chattymin/PokeTokenBar/releases/tag/vX.Y.Z) (Claude Code only).

## New
- ...            (user-visible additions; drop the section when empty)

## Fixed
- ...            (user-visible fixes; drop the section when empty)

## Install

Download `PokeTokenBar.exe`, put it in the folder you keep it in, and run it. No installer and no .NET install needed.
Requirements: Windows 11 x64 (Windows 10 is untested). See the [README](https://github.com/Bluewind8791/PokeTokenBar-Win#readme).

SHA-256 of `PokeTokenBar.exe`: `<hash>`
```

Add an `## Upgrade` section only when upgrading needs a manual step.

A release is public. **Before creating it, show the user the tag and a short summary of the notes, and wait for the
go-ahead.**

## 7. Release

```bash
gh release create <tag> "$S/release/PokeTokenBar.exe" -R Bluewind8791/PokeTokenBar-Win \
  --target <HEAD sha> --title "PokeTokenBar for Windows <version>" --notes-file "$S/release-notes.md"
gh api repos/Bluewind8791/PokeTokenBar-Win/releases/latest -q '.tag_name, (.assets[] | "\(.name) \(.digest)")'
```

The latest tag must be the new one, and the asset digest must equal the local SHA-256.

## 8. Update the app on this PC

The app runs from `windows\artifacts\publish\PokeTokenBar.exe`. Replace it with the released exe and start it again.
Do it in one command: the Claude Code agent hook can start the app between two tool calls.

```powershell
$dst = 'D:\claude\PokeTokenBar\windows\artifacts\publish'
Get-Process PokeTokenBar -ErrorAction SilentlyContinue | Where-Object SessionId -eq (Get-Process -Id $PID).SessionId | Stop-Process -Force
Start-Sleep -Milliseconds 700
Copy-Item "$S\release\*" $dst -Force
Start-Process "$dst\PokeTokenBar.exe"
```

If the copy fails because the file is locked, rename the old exe to `PokeTokenBar.old.exe` (a running exe can be
renamed) and copy again. Confirm the hash of the new exe and the `launch: PokeTokenBar <version>` line at the end of
`%LOCALAPPDATA%\PokeTokenBar\PokeTokenBar.log`.

## Report

Give: the release URL, the tag and the exe version, the SHA-256 check, the commit pushed, and the version the app on
this PC now runs. Say plainly if a step was skipped or failed.
