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

- **No menu-bar text.** The tray holds an icon only, so the usage / cost / limit line (and the
  optional Pokémon growth %) is in the tray tooltip. The popover, floating pet and notifications work as on macOS.
- **No Keychain.** Official limits use Claude Code's credentials file; there are no permission
  prompts, so the Keychain settings are gone.
- **Updates** open the release page instead of installing in place.
- Icons are Segoe Fluent glyphs instead of SF Symbols. WPF draws emoji in monochrome, so a few
  item icons use Microsoft Fluent Emoji images, downloaded once and cached.

## Agent activity

As on macOS, coding agents can drive the floating pet: it squashes while an agent works, asks for you in a speech
bubble when it needs input, and runs next to your cursor when it finishes. Windows only: **Settings → Floating Pet →
Run to the cursor when an agent finishes** turns the run off, and the pet then shows its done bubble where it stands.
The agent reports through a URL:

```text
poketokenbar://agent?state=<working|waiting|done|idle>[&label=<name>][&message=<text>]
```

The format, the several-agents rules and the setting (**Settings → Floating Pet → React to agent activity**) are the
same as in the [main README](../README.md#agent-activity). With energy saver on, or Windows animation effects off
(Settings → Accessibility → Visual effects), the pet shows bubbles without moving.

**How the URL reaches the app.** At every start the app registers the `poketokenbar` protocol for your Windows account
(`HKCU\Software\Classes\poketokenbar`, opening `"<path>\PokeTokenBar.exe" "%1"`); it rewrites the key only when it is
missing or points at another copy of the app. Opening a URL starts a second `PokeTokenBar.exe`, which hands the URL to
the running app over a named pipe that only your account in your session can open, and exits. The running app accepts
at most 4 KB of UTF-8 and ignores anything that is not an agent URL. If the app is not running, the URL starts it.
Moving or deleting the app leaves the protocol key behind; the next start of the app at its new place updates it.

**Claude Code** — save [`scripts/poketokenbar-hook.ps1`](scripts/poketokenbar-hook.ps1) as
`%USERPROFILE%\.claude\poketokenbar.ps1`. It labels each session with its project folder name, so two sessions in the
same folder share one label. It does nothing while PokeTokenBar is not running (so a hook never starts the app after
you quit it), and the app takes the URL without taking the focus from your terminal.

```powershell
param([string]$State = 'working')
try {
    if (-not (Get-Process -Name PokeTokenBar -ErrorAction SilentlyContinue)) { exit 0 }
    $folder = ''
    if ($env:CLAUDE_PROJECT_DIR) { $folder = Split-Path -Leaf $env:CLAUDE_PROJECT_DIR.TrimEnd('\', '/') }
    Start-Process ('poketokenbar://agent?state=' + [Uri]::EscapeDataString($State) + '&label=' + [Uri]::EscapeDataString($folder))
} catch { }
exit 0
```

Then add the hooks to `%USERPROFILE%\.claude\settings.json`, with your user name in the path. The `Notification`
matcher skips Claude Code's idle reminder, which would otherwise leave a "waiting" bubble after every turn. `Stop` does
not fire when you interrupt a turn (Esc), so the pet keeps working until the 10-minute timeout or an `idle` event.

```json
{
  "hooks": {
    "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\Users\<you>\.claude\poketokenbar.ps1\" working" }] }],
    "PostToolUse": [{ "matcher": "*", "hooks": [{ "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\Users\<you>\.claude\poketokenbar.ps1\" working" }] }],
    "PreToolUse": [{ "matcher": "AskUserQuestion", "hooks": [{ "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\Users\<you>\.claude\poketokenbar.ps1\" waiting" }] }],
    "Notification": [{ "matcher": "permission_prompt|elicitation_dialog", "hooks": [{ "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\Users\<you>\.claude\poketokenbar.ps1\" waiting" }] }],
    "Stop": [{ "hooks": [{ "type": "command", "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\Users\<you>\.claude\poketokenbar.ps1\" done" }] }]
  }
}
```

The absolute path works whichever shell Claude Code runs hooks with. Each hook starts PowerShell (and the hand-over
process), which takes a few hundred milliseconds.

**Other agents** — open the URL from any hook or script, e.g. in PowerShell:
`if (Get-Process PokeTokenBar -ErrorAction SilentlyContinue) { Start-Process 'poketokenbar://agent?state=done&label=my-app&message=Build%20passed' }`.

## Build from source

```powershell
cd windows
dotnet test tests/PokeTokenBar.Core.Tests        # Core: parsing, pricing, limits, companion, saves
dotnet test tests/PokeTokenBar.App.Tests         # App: view logic (chart axes, …)
dotnet run --project src/PokeTokenBar.App         # tray app
pwsh scripts/publish.ps1 -Version 2.5.7           # release zip in windows/artifacts
```

Needs the .NET 10 SDK.

### Layout

- `src/PokeTokenBar.Core` — platform-neutral port of `Sources/PokeTokenBar/Core`. Types and members
  keep the Swift names (PascalCased) so the two can be read side by side.
- `src/PokeTokenBar.App` — WPF tray app; `Screens/` mirrors `Sources/PokeTokenBar/UI`.
- `tests/PokeTokenBar.Core.Tests` — xUnit ports of the Swift tests, plus parity tests that parse
  the Swift sources (price table, localization table, save fields) and fail when the two drift.
- `tests/PokeTokenBar.App.Tests` — ports of the Swift tests that cover view logic (WPF, `net10.0-windows`).

### Comparing with the macOS UI

The app can render any screen offscreen with sample data — no logs, credentials or save are read:

```powershell
src/PokeTokenBar.App/bin/Debug/net10.0-windows/PokeTokenBar.exe --render home-full out.png [--light] [--lang ko]
```

Screens: see `RenderHarness.Screens`. Compare the output with `assets/screenshot-*.png`.
