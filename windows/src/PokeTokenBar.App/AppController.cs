using System.Windows;
using Microsoft.Win32;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// The rest of <c>AppDelegate</c> (PokeTokenBarApp.swift): owns the stores, keeps the tray icon,
/// tooltip and companion in sync with them, shows notifications, and pauses polling while the
/// machine sleeps or the display is off (macOS pauses it while the displays sleep).
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly UsageStore _store;
    private readonly CompanionStore _companion;
    private readonly UpdateChecker _updater;
    private readonly PopoverNavigation _nav = new();
    private readonly TrayController _tray;
    private readonly TraySprite _sprite;
    private readonly AppServices _services;
    private readonly FloatingPetController _pet;
    private readonly PowerMonitor _power = new();
    /// <summary>Last display state acted on. Power notifications arrive after construction, so it starts on.</summary>
    private bool _displayOn = true;

    public AppController()
    {
        _store = new UsageStore(displaysAsleep: () => !_power.DisplayOn);
        // Form/type/ability/move names come from PokéAPI via DexNames (Swift PokemonNameClient).
        _companion = new CompanionStore(formNameLoader: DexNames.Fetch);
        _updater = new UpdateChecker();
        _store.LocalizationLanguage = _companion.Language;
        _tray = new TrayController(this);
        _services = new AppServices
        {
            Store = _store, Companion = _companion, Updater = _updater, Nav = _nav,
            ClosePopover = _tray.ClosePopover,
            Quit = () => Application.Current.Shutdown(),
        };
        _sprite = new TraySprite(_tray, _store, _companion, _power);
        _pet = new FloatingPetController(_store, _companion, _power, () => _tray.ShowPopover(n => n.Reset()));

        _store.OnRefresh = () => _store.FeedCompanionAfterRefresh(_companion);
        _store.Changed += OnStoreChanged;
        _companion.Changed += () => _store.LocalizationLanguage = _companion.Language;
        _store.LimitNotificationPosted += n => _tray.Notify(n.Title, n.Body);
        _companion.CompanionNotification += (title, body) => _tray.Notify(title, body);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _power.Changed += OnDisplayChanged;

        _ = _companion.PreparePokemonProfiles();
        _ = _updater.Check();
        OnStoreChanged();
    }

    public AppServices Services => _services;
    public UsageStore Store => _store;
    public CompanionStore Companion => _companion;
    public PopoverNavigation Nav => _nav;

    private void OnStoreChanged()
    {
        _store.UpdateCompanion(_companion);
        _tray.SetToolTip(ToolTip());
    }

    /// <summary>
    /// The tray cannot show text next to the icon the way the macOS menu bar does, so the menu-bar
    /// lines (tokens · cost · limits) become the tooltip, followed by the tracked-account hint.
    /// </summary>
    private string ToolTip()
    {
        var lines = new List<string> { "PokeTokenBar" };
        lines.AddRange(_store.MenuLines.Where(x => x.Length > 0));
        if (_store.MenuToolTip is { Length: > 0 } extra) lines.Add(extra);
        return string.Join("\n", lines);
    }

    public IReadOnlyList<(string Title, Action? Action)?> MenuEntries()
    {
        // Port of StatusItemMenu.entries.
        var l = _services.L;
        var summary = $"{l.TodayTokens} {TokenFormatter.Compact(_store.TodayTotalTokens)}";
        if (_store.ShowsCost) summary += $" · {_store.TodayUsageCost.Text(l)}";
        return
        [
            (summary, null),
            null,
            (l.RefreshNow, () => _ = _store.Refresh()),
            (l.DexTitle, () => _tray.ShowPopover(n => { n.Reset(); n.OpenRepresentativeDex(); })),
            (l.Settings + "…", () => _tray.ShowPopover(n => { n.Reset(); n.ShowSettings = true; })),
            (_store.FloatingPetEnabled ? l.FloatingPetHideLabel : l.FloatingPetEnableLabel,
                () => _store.FloatingPetEnabled = !_store.FloatingPetEnabled),
            null,
            (l.Quit, () => Application.Current.Shutdown()),
        ];
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (e.Mode == PowerModes.Suspend) _store.SuspendPolling();
            else if (e.Mode == PowerModes.Resume) _ = _store.HandleSystemWake();
        });
    }

    /// <summary>Display off → polling and tray animation stop (macOS screensDidSleep); on → both resume.</summary>
    private void OnDisplayChanged()
    {
        if (_power.DisplayOn == _displayOn) return;   // an energy-saver change
        _displayOn = _power.DisplayOn;
        _tray.SetDisplayOn(_displayOn);
        if (_displayOn) _store.ResumePolling();
        else _store.SuspendPolling();
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _power.Changed -= OnDisplayChanged;
        _pet.Dispose();
        _sprite.Dispose();
        _tray.Dispose();
        _store.Dispose();
        _power.Dispose();
    }
}

/// <summary>
/// Keeps the tray icon animating the representative Pokémon (AppDelegate.ensureMenuAnimation):
/// egg frames while there is none, a static bob while the GIF loads, then the GIF capped to the
/// animation-quality frame floor.
/// </summary>
public sealed class TraySprite : IDisposable
{
    private readonly TrayController _tray;
    private readonly UsageStore _store;
    private readonly CompanionStore _companion;
    private readonly PowerMonitor _power;
    private string? _key;
    private int _generation;

    public TraySprite(TrayController tray, UsageStore store, CompanionStore companion, PowerMonitor power)
    {
        _tray = tray;
        _store = store;
        _companion = companion;
        _power = power;
        _companion.Changed += Ensure;
        _store.Changed += Ensure;
        _power.Changed += Ensure;
        Ensure();
    }

    private void Ensure()
    {
        var subject = _companion.RepresentativeSubject;
        // Energy saver raises the floor to powerSaver, like Low Power Mode on macOS (the floor is in the key).
        var floor = _store.AnimationQuality.EffectiveFrameFloor(lowPower: _power.LowPower);
        var form = subject.SpeciesID is { } sid ? PokemonForm.ResolvedForSpecies(sid, subject.Form) : null;
        var key = subject.SpeciesID is { } id ? $"{id}-{subject.IsShiny}-{floor}-{form?.RawValue ?? ""}" : "egg";
        if (key == _key) return;
        _key = key;
        var gen = ++_generation;
        if (subject.SpeciesID is not { } speciesID)
        {
            _tray.SetFrames(TrayFrames.Egg());
            _ = LoadEgg(gen);
            return;
        }
        _ = Load(gen, speciesID, subject.IsShiny, form, TimeSpan.FromSeconds(floor));
    }

    private async Task LoadEgg(int gen)
    {
        var egg = SpriteImages.Decode(await SpriteStore.Shared.EggData());
        if (gen == _generation && egg != null) _tray.SetFrames(TrayFrames.Bob(SpriteImages.CropToContent(egg)));
    }

    private async Task Load(int gen, int id, bool shiny, PokemonForm? form, TimeSpan floor)
    {
        var still = SpriteImages.Decode(await SpriteStore.Shared.Data(id, animated: false, shiny: shiny, form: form))
            ?? (shiny ? SpriteImages.Decode(await SpriteStore.Shared.Data(id, animated: false, shiny: false, form: form)) : null);
        if (gen != _generation) return;
        if (still != null) _tray.SetFrames(TrayFrames.Bob(SpriteImages.CropToContent(still)));
        foreach (var variant in shiny ? new[] { true, false } : new[] { false })
        {
            var gif = await SpriteStore.Shared.Data(id, animated: true, shiny: variant, form: form);
            if (gen != _generation) return;
            if (gif == null) continue;
            var frames = GifDecoder.CapFrameRate(GifDecoder.Frames(gif), floor);
            if (frames.Count > 1)
            {
                _tray.SetFrames(TrayFrames.Animated(frames));
                return;
            }
        }
    }

    public void Dispose()
    {
        _companion.Changed -= Ensure;
        _store.Changed -= Ensure;
        _power.Changed -= Ensure;
    }
}
