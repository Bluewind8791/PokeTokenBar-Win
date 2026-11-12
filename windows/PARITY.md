# macOS → Windows parity backlog

What the macOS app has that the Windows port (`windows/`) does not, and the order to port it in.
Update the status column as each item lands.

- **Baseline:** the Windows port was written against `32ebe4c`. This list covers the macOS commits
  from `32ebe4c` to `38e3e55` (origin/main, 2026-11-11).
- **Scope:** Claude Code only (see `README.md`). Other usage providers, crash auto-relaunch and in-place
  update install are out of scope by decision.
- **Parity gate:** `LocalizationParityTests` parses `Sources/PokeTokenBar/Core/Localization.swift` and
  fails while any Swift string is missing in `Localization.cs`. Green again since every item below landed.
  Strings of out-of-scope features (Grok and OpenCode Go limits, tracked providers, the macOS login approval)
  are carried in `Localization.cs` unused, so the gate keeps catching drift. Regenerate with
  `python tools/swift_l_to_cs.py <Localization.swift> <out.cs> tools/Localization.hand.txt`.

## Order

| # | Item | macOS PR | Size | Status |
|---|---|---|---|---|
| 1 | Show the weekly Claude limit when the plan has no 5-hour session limit (Ultra) | #464 | S | done |
| 2 | Show plans as Max / Ultra after the rename | #463 | S | done |
| 3 | Keep the new save fields when a macOS save goes through Windows | #466, #442 | S | done |
| 4 | Selectable daily trend window (7d / 14d / 21d / month) | #430 | M | done |
| 5 | Activity row: bars or a 16-week grid | #449 | M | done |
| 6 | Usage streak badge next to the recap button | #451 | S | done |
| 7 | Companion growth progress in the menu bar (tray tooltip on Windows) | #388 | S | done |
| 8 | Per-generation dex completion badges and notifications | #442 | M | done |
| 9 | Egg prayers, petting hearts, sulking, event pops | #466 | L | done |
| 10 | Agent activity drives the floating pet (`poketokenbar://agent` URLs) | #457 | L | done |

## Notes per item

### 1. Weekly limit fallback (#464)
Plans without a session limit get `five_hour: null`. The menu bar line read the 5-hour window only, so
Claude vanished from it. `LimitStatus.headlineWindow` picks the 5-hour window, else the weekly one, with
the matching span for the pace marker. On Windows the menu bar line is the tray tooltip.

### 2. Plan names (#463)
Prefer `organization.plan_display_name` from the profile endpoint; fall back to the rate-limit tier:
`*_max_5x` → Max, `*_max_20x` → Ultra.

### 3. Save fields (#466, #442)
New fields in the save: `prayers` (per individual), `eggPrayers`, `notifiedDexCompletions`,
`dexCompletionsSeeded`. The C# save model drops fields it does not know, so a macOS save imported on
Windows and exported back loses them. Port the fields (decode, keep, encode) before the features.
Done with the egg lifecycle (reset on a new egg, carried onto the hatchling and the dex entry).
`SaveFieldParityTests` now parses the Swift save structs and fails when the Windows encoder misses a field.

### 4. Daily trend window (#430)
Ported without `UsageStore.trendProviders` / `DailyTrendStack` (Windows has one provider, no stacked bars), and
`MonthDailyTrend.shouldShowSurface` without `isSelectable` (the Home chart is its only, always selectable, caller).
The axis-label / column / surface tests of `MonthDailyTrendTests.swift` test App code; they live in the new
`tests/PokeTokenBar.App.Tests` (net10.0-windows), which later items use for view-logic tests too.
`--render home-trend-7d|21d|month` renders the other windows.

### 5. Activity row (#449)
`ActivityVisualization` lives in Core (the store persists it under `activityVisualization`); `ActivityPanel`,
`PageDots` and `ActivityHeatmap` are in `Screens/`. `--render home-heatmap|home-heatmap-full` renders the grid.
Deviations:
- C# cannot make a switch over an enum exhaustive without a warning, so `Title` and `ActivityPanel.Chart` throw on an
  unknown member and `ActivityVisualizationTests` (Core and App) walk every member through both.
- Days step through `DateOnly`, so the grid takes the first weekday only (no calendar / time zone). The DST test is
  kept as a contiguity check; the 86400-second fault it guards on macOS cannot be written here.
- Weekday letters are .NET's `ShortestDayNames` of the display language (the same narrow symbols); the first weekday
  is `CurrentCulture`'s.
- No `providers` / `providerOrder` (one provider): `testPanelBarsDrawTheSameAsTheTrendItWraps` is not ported.
- The caption's title does not give way before the readout (the shared `Stack` measures in order); all eight
  languages fit at popover width.
- The panel tests compare `RenderTargetBitmap` pixels on an STA thread, with the theme installed on a host element.

### 6. Streak badge (#451)
`UsageLedger.CurrentStreak` / `UsageStreak` in Core, `StreakBadge` in `Screens/`. The `home-content` (today counts,
orange flame) and `home-zero` (no use yet today, gray flame) renders show it. Deviations:
- Days step through local `DateTime` dates, as the recap does. The DST test still runs in America/Los_Angeles and
  fails with the 86400-second step injected (`LocalDate(Midnight(day) - 86400 s)`).
- `flame.fill` is a drawn path: Segoe Fluent Icons has no flame.
- The badge tests (`StreakBadgeTests`) are in App.Tests; the streak tests in Core.Tests.

### 7. Growth progress in the tray tooltip (#388)
`CompanionStore.MenuGrowthText`, `UsageStore.ShowGrowthInMenu`, `MenuBarContent` (Core) and the Settings toggle
(`--render settings-menubar`). Deviations:
- Order: macOS joins the growth into the usage line to stay within two menu-bar lines. The tooltip has no line limit
  but is cut from the end at 127 characters, so the growth is its own line after the usage and limit lines (then the
  tracked-account hint, which goes last as before): if anything is cut, it is the growth, never the lines it was added
  next to. With every item on, all eight languages stay under 50 characters without the account hint.
- Swift's `menuLines(growthText:)` is `MenuLinesWithGrowth`: a C# property and method cannot share a name.
- The app rebuilds the tooltip on every `Changed` of either store (candy, hatch, difficulty and language change it
  without a usage refresh), so the "hidden growth causes no extra redraw" observation check is not ported, nor the
  NSStatusBarButton color test. `MenuGrowthTests` pins the contract instead: those mutations raise `Changed`.
- `AppController.ToolTip(store, companion)` is static so `TrayToolTipTests` can check the tooltip text; the
  subscription in `AppController` itself is not under test (it needs a live tray icon).
- The section title and its hint use Windows wording (`L.WinMenuBarSectionTitle` "Show in tray tooltip",
  `L.WinAllOffHint` "All off shows only the app name in the tooltip" in `LocalizationWindows.cs`): there is no menu bar.

### 8. Dex completion badges (#442)
`PokedexRegion` / `DexProgress` (Core, `PokedexRegion.cs`), the store's completion tracking (seeded at startup and on
import, checked in `Save`, notified through `CompanionNotification`), the region filter of `FilteredDexSpecies`, and the
Pokédex header's generation capsules and medals (`RegionTally` / `DexBadgeMedal` in `Screens/DexRegionProgress.cs`).
`--render collection-regions` renders the macOS screenshot's sample (Kanto/Hoenn complete, Johto shiny-complete,
Sinnoh selected, Unown medal); `--render collection` shows partial generations. Deviations:
- `DexBadge` is a record struct with `Kind` + `Region` (C# has no enum with associated values): `.region(r)` is
  `DexBadge.ForRegion(r)`. `ClosedRange<Int>` is `DexNumberRange`; the region members are extensions in `PokedexRegions`.
- 🏅 🔤 🌍 use the Fluent Emoji artwork of `EmojiImages`; ✨ is drawn (`DexUi.Sparkles`) like the dex cells' shiny mark.
- The capsule strips (generation and rarity) scroll with the mouse wheel: a WPF `ScrollViewer` without a vertical axis
  swallows the wheel, and Unova sits past the popover edge. A filter click rebuilds the strip at offset 0, so the
  selected capsule is scrolled back into view (SwiftUI keeps the offset).
- The store tests also count notifications (Swift checks the recorded keys only): a seed that sets the flag but not the
  keys still ends with the keys recorded, and only shows as a burst of notifications on import.
- `DexBackNavigationTests`' region test is `DexRegionProgressTests` (App.Tests): the click is raised on the capsule found
  by its accessible name. Its OCR click-bounds test is specific to the Vision harness and not ported.
- `swift_l_to_cs.py` now converts `\(member(arg))` interpolations (`regionName(r)` inside `dexBadgeLabel`).

### 9. Egg prayers, petting, sulking, event pops (#466)
Core: `CompanionStore.Pray()`, the prayer hatch line (`NotifPrayerHatchBody`), `CompanionStateKind.Sulk`
(`PokemonBalance.SulkAfterIdle` = 3 h, `SulkIdle`, the observed-usage cap of `EffectiveIdle`), `AlwaysFacesBack` /
`FacesBack`, `LatestEvent` (`CompanionEvent` seq/text/at, `showsOnPet: false` for candy grants), the evolution line
naming both forms, and `UsageStore.AlwaysFacesBack` / `LastUsageKnown` / `TimeSinceLastUsage()` (fed into
`Update` by `UsageStore.UpdateCompanion`). App: clicking the floating egg prays (🙏 particles + one wobble, a usage
refresh every 30 prayers at most once per 30 s, `PetClickRouter`), stroking shows the pixel heart bubble anchored
to the sprite's visible top (`PettingDetector`, `HeartBubbleArt`, `SpriteVisibleTop`), the hover text carries the
sulk duration / event line / prayer quip, new events pop the callout for 6 s, back sprites (`SpriteStore` back
URLs and cache keys) on the floating pet, the Home card and the tray icon, the Settings and both right-click menus'
"Show back view", the prayer lines on the Pokédex detail page. Renders: `--render pet-egg-prayers | pet-hearts |
pet-sulk | pet-event | home-sulk | dexdetail-prayers | settings-floatingpet` (`Screens/PetSamples.cs`; `PetView` is
the pet square the controller shows). Deviations:
- Agent activity came with item 10: the gates take the agent summary (agent speech outranks hearts, reserves the
  headroom and blocks event pops; a plain click acknowledges agents before praying). See item 10.
- The hover callout is the window's WPF tooltip, made chrome-less and centred 6 pt above the pet like the macOS
  callout panel; an event opens it programmatically for 6 s. As before, no tooltip while a limit bubble shows.
- Energy (defect-log "에너지"): in energy saver the heart has no pop-in, the egg does not wobble and particles stand
  still at mid-flight until they expire (macOS animates the particles even in Low Power Mode); with the display off
  the pet is hidden and `Hide` stops the heart / event one-shot timers and clears the particles. No timer repeats.
- 🙏 is the Fluent Emoji "Folded hands" artwork (default tone; `EmojiImages` now knows skin-tone folders): WPF draws
  emoji in monochrome.
- Back sprites: the Windows sprite loader has no retry loop, so it has no 404-vs-transient split either: any back
  sprite that cannot be loaded falls back to the front one until the sprite is rebuilt (`SpriteStore.Variants`: back
  shiny, back, front shiny, front). Views rebuild their `SpriteView` when the facing changes (it is in the floating
  pet's and the tray's keys, `TraySprite.MenuSpriteKey`).
- Strokes are WPF `MouseMove` samples without a pressed button, in screen DIPs with the event timestamp.
- The status menu is now `StatusItemMenu.Entries` (`MenuAction`, `IsChecked`) like Swift, so the back-view row and
  its check mark are testable; `AppController.Perform` runs the actions.
- `L.PrayersPerQuip` / `PrayerQuipCount` / `PrayerQuipIndex` are Swift `static` members, which `swift_l_to_cs.py`
  skips; the hand file's `DOC prayerHoverLine` block carries them. The generator now escapes C# keywords used as
  parameter names (`notifEvolveBody(from old:, to new:)` → `string old, string @new`).
- Tests: store behaviour in Core.Tests (`EggPrayerTests`, `SulkTests`, `PetEventLineTests`, `UsageStoreTests`,
  `LocalizationTests`), view logic in App.Tests (`EggPrayerTests`, `FloatingPetPettingTests` incl. `PetViewEnergyTests`,
  `PetEventAndSulkTests` incl. `BackSpriteTests` / `StatusItemMenuTests`). The disguised-Ditto seed is searched
  (Swift pins 2466). Not ported: `testMissingBackFallsBackToFrontAndBackWinsWhenCached` (the Windows `SpriteView`
  would fetch the missing back sprite from the network; the fallback order is tested on `SpriteStore.Variants`), the
  render-only tests (`EggPrayerRenderTests`, `SulkRenderTests`, the petting renders — the harness screens above
  replace them; the native-menu illustrations are not reproduced) and the persistence tests already ported with
  item 3 (`SaveFieldsTests`).
- Not under test: the controller's own wiring (window click → router → `CompanionStore.Pray`, stroke → hearts,
  `PopEventIfNew`, `Hide` stopping the timers) needs a live window and `PowerMonitor`.

### 10. Agent activity (#457)
Core (`AgentActivity.cs`): `AgentActivityEvent.Parse` (scheme / host / first `state`, percent-decoding like
`URLComponents`, label and message sanitised and capped exactly like Swift: controls → space, format characters but
ZWJ dropped, whitespace collapsed, 4× scalars then grapheme-cluster cap with `…`), `AgentActivityTracker` (per label,
working 10 min, done 6 s, waiting until the next event or a click, unlabeled = one agent, at most 8 labels evicting the
oldest), `AgentActivitySummary`, `UsageStore.FloatingPetAgentActivity` (`floatingPetAgentActivity`, default on), the 8
strings. Pet (`FloatingPet.cs`): `Bubble` / `BubbleFor` (limit > done > waiting), `BubbleContent`, `AgentTitle` and
`FittedBubbleBody` on `MeasureSpeechBubbleLayout`, the agent arguments of the #466 gates (`HeartsShowing`,
`IsShowingBubble`, event pops), `RunPhase` / `ResolveMove` / `RunPanelOrigin` / `RunsToCursor` / `AllowsMotion` /
`ShouldBob`, the click route (agent acknowledgement, then prayer or Open; the context menu's Open acknowledges and always
opens), the expiry timer and the run. Settings: Floating Pet → *React to agent activity*, and under it the
Windows-only *Run to the cursor when an agent finishes* (see Deviations). Renders:
`--render pet-agent-working | pet-agent-waiting | pet-agent-done` and `settings-floatingpet`.

Windows plumbing (no macOS counterpart; `AgentUrlHandler.cs` in Core):
- `AgentUrlProtocol`: `HKCU\Software\Classes\poketokenbar` (`URL Protocol`, `shell\open\command` = `"<exe>" "%1"`),
  refreshed by `AppController` at every start and written only when missing, stale (another exe) or without the
  `URL Protocol` value. The key path is a parameter (`EnsureRegisteredAt`), so tests use a throwaway HKCU key.
- `AgentUrlChannel` / `AgentUrlListener`: a URL launch is a second process. `App.OnStartup` reads only the first
  argument (and only when it has the scheme), loses the single-instance mutex and writes the URL to the named pipe
  `PokeTokenBar.SingleInstance.<session id>` (pipe names are machine-wide, so the session id stands in for the mutex's
  `Local\`; `PipeOptions.CurrentUserOnly` on both ends), then exits without logging. The running instance serves one
  client at a time and treats the bytes as untrusted: at most 4 KB, strict UTF-8, read within 2 s, then
  `AgentActivityEvent.Parse`; anything else is dropped. Accepted events go to the pet on the UI thread
  (`Dispatcher.BeginInvoke`). With no instance running, the URL process becomes the app and applies its own URL once
  the pet exists (`AppController.HandleAgentUrl`, same validation). The listener waits on the pipe with no timer.
- Hook: `scripts/poketokenbar-hook.ps1` (ASCII only, so PowerShell 5.1 needs no BOM) — `Get-Process` in place of
  `pgrep`, `[Uri]::EscapeDataString` in place of perl, `Start-Process <url>` in place of `open -g`. `README.md` has the
  section and the `settings.json` hooks with absolute paths (they work whichever shell runs hooks).

Deviations:
- Types: `AgentActivity` is a record struct with a `Kind` (C# has no enum with associated values); the tracker is a
  class; `Bubble` is an abstract record with nested cases; `BubbleFor` because a method cannot share the nested type's
  name.
- Measuring: WPF has no line limit, so the bubble body is capped by height (2 × 10 pt × 1.35, as before) and
  `MeasureSpeechBubbleLayout` measures with `TextBlock`s like the view's (FormattedText does not break a word longer
  than the column, which the view's `TextWrapping.Wrap` does). It must run on the UI thread; its tests run on STA.
  `AgentBubbleTextNeverTruncates` checks the Windows window layout (bubble + 8 pt gap ≤ 72 pt headroom) rather than
  macOS's `headroom - 2`. The limit bubble keeps `LimitAlert.Body` and the view's trimming: the macOS
  `bubbleBody(for:)` account fallback (#167) was never ported and is still not.
- Squash: `AgentSquash`, a decorator around the sprite slot (sprite, hearts and particles squash together) whose
  `DispatcherTimer` flips one `ScaleTransform` every 0.5 s. It runs only while the pet is shown and motion is allowed;
  `Hide` stops it. `IsBobbing` is the request (like `agentModel.bobs`), `IsSquashTicking` the timer.
- Motion: energy saver (`PowerMonitor.LowPower`) or Windows *Animation effects* off
  (`SystemParameters.ClientAreaAnimation`, watched through `StaticPropertyChanged`) stand in for Low Power Mode /
  Reduce Motion: bubbles only, no squash, no run.
- Run: `Window.Left` / `Top` `DoubleAnimation`s (0.6 s, quadratic ease in-out; the base values are the target, so the
  window stays there). The done bubble appears at home first and the window then moves, and on the way back the bubble
  leaves at the run spot before the walk, so the pet never jumps when the window resizes (AppKit animates the frame,
  size included). The cursor and its monitor's work area come from WinForms (`Cursor.Position`, `Screen.FromPoint`) in
  pixels and are divided by the window's DPI; mixed-DPI monitor setups are not verified.
- Run switch (Windows-only, added on 2026-11-12 at the user's request; macOS always runs):
  `UsageStore.FloatingPetAgentRun` (`floatingPetAgentRun`, default on, so the default behaviour is macOS's) feeds
  `RunsToCursor`'s `runEnabled`. Off, a done shows its bubble at home. The row shows only while *React to agent
  activity* is on; its label is `L.WinFloatingPetAgentRunLabel` (`LocalizationWindows.cs`, outside the Swift string
  parity). Switching it off during a run does not cut the run short: the pet walks home when that done expires.
- Home: Windows saves the origin only when a drag ends, so the run's frames never reach the settings at all;
  `PanelDidMove` / `ResolveMove` are kept for the drop (and tests). A drag that starts during the run or the walk back
  stops the animation where it is, cancels the return, and the drop becomes home.
- `FloatingPetController` takes an `IPowerStatus` (implemented by `PowerMonitor`), a motion override and a clock, so
  the wiring tests run without a power window. Window click tests raise mouse events on the window `MakeWindow` builds
  without showing it (`ScreenPoint` falls back to window coordinates while there is no HWND; `InternalsVisibleTo` the
  App tests).
- Tests: `AgentActivityTests` and `AgentUrlHandlerTests` (registry under
  `HKCU\Software\PokeTokenBarTests.AgentUrl\<guid>`, removed afterwards; pipes with unique names) in Core.Tests;
  `FloatingPetAgentTests` / `FloatingPetAgentWiringTests` (display "off", motion pinned, so no window is shown) in
  App.Tests, with the #466 cases and the window-click tests. Not ported: `AgentURLHandlerTests`' Apple Event test
  (replaced by the pipe and argument tests). Not under test: the view-side gates inside `Show` / `PopEventIfNew`
  (hearts hidden under agent speech, event pop blocked by it — the pure gates are tested), the run animation and the
  cursor / monitor query, `App.OnStartup`'s forward path and the `AppController` wiring (need a live app, the real
  protocol key and the real pipe), and the `SystemParameters` change notification.
- Checked live on 2026-11-12 with the published build: the app registered `HKCU\Software\Classes\poketokenbar` at
  start; opening `waiting` showed the labeled bubble, `done` moved the pet window next to the cursor on the other
  monitor and back home within 10 s, and the installed Claude Code hook script did the same in ~320 ms.
- Follow-up: on a monitor with another scale (150 % next to a 100 % primary) the run-to-cursor window still reports
  96 DPI, so the pet may draw at the primary monitor's scale there. Not verified visually.

## Not ported (out of scope)

| macOS PR | Why |
|---|---|
| #431 omo / senpi, #462 Devin, #459 OpenCode Go limits, #461 Grok limits | Non-Claude providers |
| #460 Choose which providers to track | Windows tracks Claude Code only |
| #434 Codex candy regrant | Codex-only change; the Claude path is unchanged |
| #474 Register at login through System Settings approval | macOS login-item API; Windows uses the Run key |
