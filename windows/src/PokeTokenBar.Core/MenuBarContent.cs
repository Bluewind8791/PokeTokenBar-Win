namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>MenuBarContent</c> (#388): the menu-bar lines of both stores. Windows has no menu-bar text, so these
/// are the tray tooltip's lines; the app re-reads them when either store changes, so growth from a candy, a hatch or
/// a language change shows without waiting for a usage poll.
/// </summary>
public static class MenuBarContent
{
    public static IReadOnlyList<string> Lines(UsageStore store, CompanionStore companion) =>
        store.MenuLinesWithGrowth(store.ShowGrowthInMenu ? companion.MenuGrowthText : null);
}
