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

## Build

This fork publishes no releases. Build the exe from source (needs the .NET 10 SDK):

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
