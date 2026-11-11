# PokeTokenBar for Windows

A Windows port of PokeTokenBar: your Claude Code token usage grows a Pokémon companion in the
notification area (system tray). It reads the same local logs and keeps the same save format as
the macOS app, so a save exported on a Mac can be imported here and vice versa.

**Scope:** Claude Code only. The other providers of the macOS app (Codex, Gemini, Cursor, …) are
not ported.

## Requirements

Windows 10 or 11, x64. The release zip is self-contained; no .NET install is needed.

## Install

1. Download `PokeTokenBar-Windows-x64.zip` from the GitHub release.
2. Unzip and run `PokeTokenBar.exe`. The companion appears in the notification area (it may sit
   in the overflow `^` menu at first — drag it onto the taskbar to keep it visible).
3. Optional: Settings → *Launch at login*.

## Where things live

| What | Path |
|---|---|
| Claude Code logs read | `%USERPROFILE%\.claude\projects`, `%USERPROFILE%\.config\claude\projects`, every `CLAUDE_CONFIG_DIR` entry, Claude Desktop sessions under `%APPDATA%\Claude` |
| Official limits credential | `%USERPROFILE%\.claude\.credentials.json` (read only, never written) |
| App state (save, settings, caches, log) | `%LOCALAPPDATA%\PokeTokenBar` (`PTB_STATE_DIR` overrides it) |
| claude.ai session key (optional) | `%LOCALAPPDATA%\PokeTokenBar\session-key.dat`, encrypted with DPAPI for your Windows account |

## Differences from the macOS app

- **No menu-bar text.** The tray holds an icon only, so the usage / cost / limit line is in the
  tray tooltip. The popover, floating pet and notifications work as on macOS.
- **No Keychain.** Official limits use Claude Code's credentials file; there are no permission
  prompts, so the Keychain settings are gone.
- **Updates** open the release page instead of installing in place.
- Icons are Segoe Fluent glyphs instead of SF Symbols. WPF draws emoji in monochrome, so a few
  item icons use Microsoft Fluent Emoji images, downloaded once and cached.

## Build from source

```powershell
cd windows
dotnet test tests/PokeTokenBar.Core.Tests        # Core: parsing, pricing, limits, companion, saves
dotnet run --project src/PokeTokenBar.App         # tray app
pwsh scripts/publish.ps1 -Version 2.5.7           # release zip in windows/artifacts
```

Needs the .NET 10 SDK.

### Layout

- `src/PokeTokenBar.Core` — platform-neutral port of `Sources/PokeTokenBar/Core`. Types and members
  keep the Swift names (PascalCased) so the two can be read side by side.
- `src/PokeTokenBar.App` — WPF tray app; `Screens/` mirrors `Sources/PokeTokenBar/UI`.
- `tests/PokeTokenBar.Core.Tests` — xUnit ports of the Swift tests, plus parity tests that parse
  the Swift sources (price table, localization table) and fail when the two drift.

### Comparing with the macOS UI

The app can render any screen offscreen with sample data — no logs, credentials or save are read:

```powershell
src/PokeTokenBar.App/bin/Debug/net10.0-windows/PokeTokenBar.exe --render home-full out.png [--light] [--lang ko]
```

Screens: see `RenderHarness.Screens`. Compare the output with `assets/screenshot-*.png`.
