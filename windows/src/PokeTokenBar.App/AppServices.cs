using PokeTokenBar.Core;

namespace PokeTokenBar.App;

public enum PopoverTab { Home, Shop, Bag, Collection }

/// <summary>
/// Port of <c>PopoverNavigation</c> (PopoverView.swift): which screen the popover shows. Lives
/// across popover openings; <see cref="Reset"/> runs on each open so it always starts at Home.
/// Setting a property raises <see cref="Changed"/>; the popover rebuilds its content from it.
/// </summary>
public sealed class PopoverNavigation
{
    private bool _showSettings;
    private PopoverTab _tab = PopoverTab.Home;
    private bool _showingCollectionLog;
    private bool _showingRecap;
    private string? _dexDetailCollectionID;
    private string? _providerID;
    private string? _claudeAccountID;

    public event Action? Changed;

    public bool ShowSettings
    {
        get => _showSettings;
        set { _showSettings = value; if (value) _dexDetailCollectionID = null; Changed?.Invoke(); }
    }

    public PopoverTab Tab
    {
        get => _tab;
        set { _tab = value; if (value != PopoverTab.Collection) _dexDetailCollectionID = null; Changed?.Invoke(); }
    }

    /// <summary>Collection segment: catch log (true) or Pokédex (false). Kept across visits.</summary>
    public bool ShowingCollectionLog
    {
        get => _showingCollectionLog;
        set { if (value != _showingCollectionLog) _dexDetailCollectionID = null; _showingCollectionLog = value; Changed?.Invoke(); }
    }

    public bool ShowingRecap
    {
        get => _showingRecap;
        set { _showingRecap = value; if (value) _dexDetailCollectionID = null; Changed?.Invoke(); }
    }

    /// <summary>The Collection tab's open Pokédex detail page; null = grid or log.</summary>
    public string? DexDetailCollectionID
    {
        get => _dexDetailCollectionID;
        set { _dexDetailCollectionID = value; Changed?.Invoke(); }
    }

    /// <summary>Provider tab selection — kept across openings.</summary>
    public string? ProviderID
    {
        get => _providerID;
        set { _providerID = value; Changed?.Invoke(); }
    }

    /// <summary>Claude account tab in the limits section — kept across openings.</summary>
    public string? ClaudeAccountID
    {
        get => _claudeAccountID;
        set { _claudeAccountID = value; Changed?.Invoke(); }
    }

    /// <summary>Open Settings with the advanced section expanded (the session-key row lives there).</summary>
    public bool ExpandAdvancedOnOpen { get; set; }

    public void Reset()
    {
        _showSettings = false;
        ExpandAdvancedOnOpen = false;
        _showingRecap = false;
        _tab = PopoverTab.Home;
        _dexDetailCollectionID = null;
        Changed?.Invoke();
    }

    public void OpenSessionKeySettings()
    {
        ExpandAdvancedOnOpen = true;
        ShowSettings = true;
    }

    /// <summary>A Home sprite opens that species' Pokédex page; the segment stays as it was.</summary>
    public void OpenDexEntry(string collectionID)
    {
        _showSettings = false;
        _tab = PopoverTab.Collection;
        _dexDetailCollectionID = collectionID;
        Changed?.Invoke();
    }

    /// <summary>Settings' representative-Pokémon row → the Pokédex segment, which has the select action.</summary>
    public void OpenRepresentativeDex()
    {
        _showSettings = false;
        _showingCollectionLog = false;
        _tab = PopoverTab.Collection;
        _dexDetailCollectionID = null;
        Changed?.Invoke();
    }
}

/// <summary>
/// Everything a screen needs, passed to every view builder (the SwiftUI <c>@Environment</c> objects).
/// All members are used from the UI thread only.
/// </summary>
public sealed class AppServices
{
    public required UsageStore Store { get; init; }
    public required CompanionStore Companion { get; init; }
    public required UpdateChecker Updater { get; init; }
    public required PopoverNavigation Nav { get; init; }

    /// <summary>Strings in the companion's language (Swift <c>companion.l</c>).</summary>
    public L L => new(Companion.State.Language);

    /// <summary>Closes the popover (e.g. after Quit is chosen, or an action that opens another window).</summary>
    public Action ClosePopover { get; init; } = () => { };

    public Action Quit { get; init; } = () => { };
}
