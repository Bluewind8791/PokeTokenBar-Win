# PokeTokenBar for Windows (personal port)

[한국어](README.md) · **English**

A personal Windows port of [chattymin/PokeTokenBar](https://github.com/chattymin/PokeTokenBar):
your Claude Code token usage grows a Pokémon companion, here in the Windows notification area
(system tray) instead of the macOS menu bar.

- **The app itself** (features, screenshots, the macOS build): see the
  [original repository](https://github.com/chattymin/PokeTokenBar).
- **The Windows port** (build, where files live, differences from macOS): see
  [`windows/README.md`](../windows/README.md).
- **Scope:** Claude Code only. The other usage providers of the macOS app are not ported.
  The port status per upstream change is in [`windows/PARITY.md`](../windows/PARITY.md).

## Requirements

| What | Details |
|---|---|
| OS | Tested on Windows 11 x64. Windows 10 x64 is untested. |
| .NET | No install needed. The runtime is inside the exe. |
| Install | None. Put the single `PokeTokenBar.exe` in any folder and run it. |
| Claude Code | Usage shows only for Claude Code used on that PC. The app reads its logs, such as `%USERPROFILE%\.claude\projects`. |
| Data | Save, settings and caches live in `%LOCALAPPDATA%\PokeTokenBar`, per PC. To move them, use the save export / import in Settings. |

At the first start the app unpacks its WPF native libraries to `%TEMP%\.net\PokeTokenBar`. *Launch at
login* and the `poketokenbar://` protocol record the exe path, so pick the folder for the exe before
you run it.

## Install

Download `PokeTokenBar.exe` from [Releases](https://github.com/Bluewind8791/PokeTokenBar-Win/releases)
and run it. The app announces a new release; *Update* opens the release page, and you replace the exe
with the new one (it does not install itself). `v2.5.7-win.1` lacks this notice, so get the release after it yourself.

To build from source (needs the .NET 10 SDK):

```powershell
pwsh windows/scripts/publish.ps1
```

The exe lands in `windows/artifacts/publish/PokeTokenBar.exe`.

## How this fork tracks upstream

The Windows code lives only under `windows/`. Everything else is kept identical to upstream, so
the root `README.md` is the original project's README. Upstream changes are merged into `main`
and ported to `windows/` with the `sync-upstream` Claude Code skill
(`.claude/skills/sync-upstream/SKILL.md`).

This fork is not affiliated with the original project. Do not report problems of the Windows port
to the original repository.
