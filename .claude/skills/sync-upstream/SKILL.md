---
name: sync-upstream
description: Bring new upstream (chattymin/PokeTokenBar) commits into this personal Windows fork, decide which macOS changes the Windows port (windows/) needs, and port them. Use when the user asks to pull or sync the original repo, check upstream for updates, or port new macOS changes to Windows, e.g. "원본 변경 가져와서 업데이트해줘", "원본 업데이트 확인", "sync upstream". Manual only.
---

# Sync upstream and port to Windows

This checkout is a personal fork. Remotes:

- `origin` = upstream `chattymin/PokeTokenBar` (macOS app). Fetch only. Never push, never open a PR.
- `fork` = `Bluewind8791/PokeTokenBar-Win`. Local `main` tracks `fork/main` and holds the Windows port,
  so it diverges from `origin/main` on purpose. A plain `git pull` reads the fork, not upstream.

Run commands from the repository root.

## 1. Find new upstream commits

```bash
git status --short          # must be clean; stop and report if not
git fetch origin
git log --oneline --reverse main..origin/main
```

No commits: report "upstream has nothing new" with the `origin/main` tip, and stop.

Also read the **Baseline** line at the top of `windows/PARITY.md`. It names the last upstream commit
the port was checked against. Commits between that baseline and `origin/main` are the ones to classify.

## 2. Classify each commit, then ask

For each commit, read `git show --stat <sha>` and, where the stat is not enough, the diff of the files
under `Sources/`. Put each commit in one class:

| Class | What it covers |
|---|---|
| Port | `Sources/PokeTokenBar/Core` logic, `Sources/PokeTokenBar/UI` behaviour or layout, new or changed save fields, new or changed strings in `Localization.swift`, pricing table, Claude Code log parsing, Claude limits |
| No port | CI, release scripts, `RELEASE.md`, landing page, screenshots, `docs/`, README translations, Swift-only test fixtures, macOS-only system integration (launchd, Sparkle, AppKit-specific workarounds with no Windows meaning) |
| Out of scope | Non-Claude usage providers and their limits (Codex, Gemini, Cursor, Antigravity, Grok, OpenCode, …), crash auto-relaunch, in-place update install. See `## Not ported (out of scope)` in `windows/PARITY.md` |

Strings of out-of-scope features still go into `Localization.cs`, because `LocalizationParityTests`
needs every Swift string. A commit that only adds such strings is "Port (strings only)".

Show the user one table: sha, upstream PR number, title, class, and for "Port" a one-line plan with
the Windows files to touch and a size (S / M / L). **Stop here and wait for the user's confirmation.**
The user may move commits between classes. Do not merge or port before the answer.

If every commit is "No port" or "Out of scope", still ask: merge only, and bump the baseline.

## 3. Merge

```bash
git merge --no-edit origin/main
```

Conflicts are unexpected, because the fork changes only `windows/`, `docs/reference/defect-log.md` and
`.claude/skills/sync-upstream/`. On a conflict, stop, list the conflicted files, and ask. Do not
resolve a conflict in macOS files by picking the fork's side.

## 4. Port

Rules (the user decided these; do not ask again):

- Change only `windows/`. Never edit macOS Swift sources or tests.
- Keep every upstream feature. When the user does not want one on Windows, add a Windows-only switch
  whose default matches macOS. Do not delete ported code.
- Keep the Swift names (PascalCased) so the two sources read side by side.
  `src/PokeTokenBar.Core` mirrors `Sources/PokeTokenBar/Core`; `src/PokeTokenBar.App/Screens` mirrors
  `Sources/PokeTokenBar/UI`.
- Port the Swift tests of the change to xUnit (`tests/PokeTokenBar.Core.Tests`, or
  `tests/PokeTokenBar.App.Tests` for view logic).
- Strings: regenerate `Localization.cs` with
  `python windows/tools/swift_l_to_cs.py Sources/PokeTokenBar/Core/Localization.swift windows/src/PokeTokenBar.Core/Localization.cs windows/tools/Localization.hand.txt`,
  then check the diff. Windows-only strings go in `LocalizationWindows.cs` with `Win*` names.
- Save fields: a new Swift save field must round-trip through the Windows save. `SaveFieldParityTests`
  fails until it does.
- Follow the defect protocol in `CLAUDE.md` if the port exposes a defect.

## 5. Record in PARITY.md

In `windows/PARITY.md`:

- Move the **Baseline** line to the new `origin/main` sha and date.
- Add a row to `## Order` and a `### n.` note for each ported item (upstream PR, what changed,
  where it lives on Windows, any behaviour difference from macOS).
- Add out-of-scope items to `## Not ported (out of scope)`.

## 6. Verify

```bash
dotnet test windows/tests/PokeTokenBar.Core.Tests
dotnet test windows/tests/PokeTokenBar.App.Tests
```

All tests must pass, the parity tests included (`LocalizationParityTests`, `SaveFieldParityTests`,
the pricing table test). Then rebuild the exe:

```powershell
pwsh windows/scripts/publish.ps1 -Version <upstream version tag>
```

The exe lands in `windows/artifacts/publish`. When a change is visible, render the screen with the
offscreen render harness (see `windows/README.md`, "Comparing with the macOS UI") and look at it.

## 7. Commit

Commit locally in English, Conventional Commits, scope `windows`, for example
`feat(windows): port upstream changes up to <sha>`. List the ported upstream PRs in the body.
No `Co-Authored-By` trailer. The merge commit from step 3 stays as is.

Push only when the user asks, and only to `fork` (`git push fork main`).

## Report

End with: new baseline sha, the commits ported / not ported / out of scope, test result, exe path,
and the commit shas. Say plainly if a step was skipped or failed.
