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
    private readonly AgentUrlListener _agentUrls;
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
        _companion.Changed += () =>
        {
            _store.LocalizationLanguage = _companion.Language;
            // Candy, hatch and language change the growth text without a usage refresh.
            _tray.SetToolTip(ToolTip());
        };
        _store.LimitNotificationPosted += n => _tray.Notify(n.Title, n.Body);
        _companion.CompanionNotification += (title, body) => _tray.Notify(title, body);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _power.Changed += OnDisplayChanged;

        _ = _companion.PreparePokemonProfiles();
        _ = _updater.Check();
        OnStoreChanged();

        // poketokenbar:// (macOS Info.plist CFBundleURLTypes): the protocol starts a second process, which hands the
        // URL over the pipe. Events arrive off the UI thread and are applied on it.
        try
        {
            if (AgentUrlProtocol.EnsureRegistered()) AppLog.Write("agent urls: registered the poketokenbar protocol");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            AppLog.Write($"agent urls: cannot register the poketokenbar protocol: {e.Message}");
        }
        var dispatcher = Application.Current.Dispatcher;
        _agentUrls = new AgentUrlListener(AgentUrlChannel.CurrentPipeName(), e => dispatcher.BeginInvoke(() => _pet.HandleAgentEvent(e)));
    }

    /// <summary>The URL this process was launched with when no instance was running; validated like a forwarded one.</summary>
    public void HandleAgentUrl(string url)
    {
        if (AgentUrlChannel.Accept(url) is { } e) _pet.HandleAgentEvent(e);
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
    /// lines (tokens · cost · limits, then the companion's growth) become the tooltip, followed by the
    /// tracked-account hint. The tooltip is cut from the end at 127 characters: the hint, whose length
    /// depends on the account title, goes last so it is the part cut.
    /// </summary>
    public static string ToolTip(UsageStore store, CompanionStore companion)
    {
        var lines = new List<string> { "PokeTokenBar" };
        lines.AddRange(MenuBarContent.Lines(store, companion).Where(x => x.Length > 0));
        if (store.MenuToolTip is { Length: > 0 } extra) lines.Add(extra);
        return string.Join("\n", lines);
    }

    private string ToolTip() => ToolTip(_store, _companion);

    public IReadOnlyList<StatusItemMenu.Entry?> MenuEntries() =>
        StatusItemMenu.Entries(_services.L, _store.TodayTotalTokens, _store.ShowsCost ? _store.TodayUsageCost.Text(_services.L) : null,
            _store.FloatingPetEnabled, _store.AlwaysFacesBack);

    public void Perform(StatusItemMenu.MenuAction action)
    {
        switch (action)
        {
            case StatusItemMenu.MenuAction.Refresh: _ = _store.Refresh(); break;
            case StatusItemMenu.MenuAction.OpenDex: _tray.ShowPopover(n => { n.Reset(); n.OpenRepresentativeDex(); }); break;
            case StatusItemMenu.MenuAction.OpenSettings: _tray.ShowPopover(n => { n.Reset(); n.ShowSettings = true; }); break;
            case StatusItemMenu.MenuAction.ToggleFloatingPet: _store.FloatingPetEnabled = !_store.FloatingPetEnabled; break;
            case StatusItemMenu.MenuAction.ToggleBackView: _store.AlwaysFacesBack = !_store.AlwaysFacesBack; break;
            case StatusItemMenu.MenuAction.Quit: Application.Current.Shutdown(); break;
        }
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
        _agentUrls.Dispose();
        _pet.Dispose();
        _sprite.Dispose();
        _tray.Dispose();
        _store.Dispose();
        _power.Dispose();
    }
}

/// <summary>Port of <c>StatusItemMenu</c> (StatusItemMenu.swift): the tray icon's right-click menu.</summary>
public static class StatusItemMenu
{
    public enum MenuAction { Refresh, OpenDex, OpenSettings, ToggleFloatingPet, ToggleBackView, Quit }

    /// <summary>A null <see cref="Action"/> is an informational row: greyed out, not clickable.</summary>
    public sealed record Entry(string Title, MenuAction? Action, bool IsChecked = false);

    /// <summary>Null elements are separators. <paramref name="todayCost"/> is the formatted cost, or null when hidden.</summary>
    public static IReadOnlyList<Entry?> Entries(L l, long todayTokens, string? todayCost, bool floatingPetEnabled,
        bool alwaysFacesBack = false)
    {
        var summary = $"{l.TodayTokens} {TokenFormatter.Compact(todayTokens)}";
        if (todayCost != null) summary += $" · {todayCost}";
        return
        [
            new(summary, null),
            null,
            new(l.RefreshNow, MenuAction.Refresh),
            new(l.DexTitle, MenuAction.OpenDex),
            new(l.Settings + "…", MenuAction.OpenSettings),
            new(floatingPetEnabled ? l.FloatingPetHideLabel : l.FloatingPetEnableLabel, MenuAction.ToggleFloatingPet),
            new(l.BackViewLabel, MenuAction.ToggleBackView, IsChecked: alwaysFacesBack),
            null,
            new(l.Quit, MenuAction.Quit),
        ];
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
        var back = _companion.FacesBack;
        var key = subject.SpeciesID is { } id ? MenuSpriteKey(id, subject.IsShiny, floor, form, back) : "egg";
        if (key == _key) return;
        _key = key;
        var gen = ++_generation;
        if (subject.SpeciesID is not { } speciesID)
        {
            _tray.SetFrames(TrayFrames.Egg());
            _ = LoadEgg(gen);
            return;
        }
        _ = Load(gen, speciesID, subject.IsShiny, form, TimeSpan.FromSeconds(floor), back);
    }

    /// <summary>Identity of the tray animation (<c>AppDelegate.menuSpriteKey</c>): the frame floor and the facing are
    /// part of it, or a setting change would keep the old frames until the next evolution.</summary>
    public static string MenuSpriteKey(int id, bool shiny, double floor, PokemonForm? form, bool back = false)
    {
        var key = $"{id}-{shiny}-{floor}-{form?.RawValue ?? ""}";
        return back ? key + "-back" : key;
    }

    private async Task LoadEgg(int gen)
    {
        var egg = SpriteImages.Decode(await SpriteStore.Shared.EggData());
        if (gen == _generation && egg != null) _tray.SetFrames(TrayFrames.Bob(SpriteImages.CropToContent(egg)));
    }

    private async Task Load(int gen, int id, bool shiny, PokemonForm? form, TimeSpan floor, bool back)
    {
        // The back sprite while sulking (or always, by choice), the front one when there is no back sprite.
        System.Drawing.Bitmap? still = null;
        foreach (var (facingBack, variant) in SpriteStore.Variants(shiny, back))
            if ((still = SpriteImages.Decode(await SpriteStore.Shared.Data(id, animated: false, shiny: variant, form: form, back: facingBack))) != null)
                break;
        if (gen != _generation) return;
        if (still != null) _tray.SetFrames(TrayFrames.Bob(SpriteImages.CropToContent(still)));
        foreach (var (facingBack, variant) in SpriteStore.Variants(shiny, back))
        {
            var gif = await SpriteStore.Shared.Data(id, animated: true, shiny: variant, form: form, back: facingBack);
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
