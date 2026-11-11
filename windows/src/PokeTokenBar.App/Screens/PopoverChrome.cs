using System.Diagnostics;
using System.Windows;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of the popover chrome shared by every tab (PopoverView.swift): the update banner above the
/// tabs and the footer (updated-ago + refresh, floating-pet toggle, settings, quit).
/// </summary>
public static class PopoverChrome
{
    /// <summary>
    /// Swift <c>updateBanner</c>. macOS installs the update in place; the Windows checker only knows the
    /// release page, so "Update" opens it (and closes the popover so the browser comes forward).
    /// </summary>
    public static FrameworkElement? UpdateBanner(AppServices s)
    {
        if (s.Updater.Available is not { } update || !s.Store.UpdateNotificationsEnabled) return null;
        var l = s.L;
        var skip = Ui.Text(l.SkipThisVersion, Theme.FontSize.Subheadline, Theme.Keys.Secondary)
            .OnClick(s.Updater.SkipCurrent);
        var apply = Buttons.Small(l.UpdateButton, () =>
        {
            Process.Start(new ProcessStartInfo(update.Url) { UseShellExecute = true });
            s.ClosePopover();
        }, prominent: true);
        var column = Ui.VStack(6,
            Ui.Text(l.UpdateAvailable(update.Version, s.Updater.CurrentVersion), Theme.FontSize.Caption).Wrapping(),
            Ui.HStack(8, Ui.Spacer(), skip, apply));
        return HomeLayout.Box(column, HomeLayout.Tint(Theme.Keys.Accent, 0.12), 8, 8);
    }

    public static FrameworkElement Footer(AppServices s)
    {
        var store = s.Store;
        var l = s.L;
        var row = Ui.HStack(10);
        // Refresh, "Updated" and the error triangle are usage freshness — on tabs without usage (dex, bag,
        // shop) they only confuse, so Home only. Settings and Quit are global and stay.
        if (s.Nav.Tab == PopoverTab.Home)
        {
            // No spinner swap: local parsing updates today's number at once, and a spinner waiting for the
            // network enrichment outlived the data. Repeated clicks hit refresh()'s re-entrancy guard.
            row.Children.Add(Buttons.Icon(Ui.Glyphs.Refresh, l.RefreshNow, () => _ = store.Refresh()));
            if (store.LastUpdated is { } updated)
            {
                var lang = l.Lang;
                row.Children.Add(Ui.Text("", Theme.FontSize.Caption, Theme.Keys.Tertiary)
                    .Ticking(() => $"{l.Updated} {HomeFormat.Relative(updated, lang)}"));
            }
            if (store.LastErrorDescription != null)
                row.Children.Add(Ui.Icon("", 15, Theme.Keys.Orange).Help(store.LastErrorMessage(l) ?? ""));   // exclamationmark.triangle
        }
        row.Children.Add(Ui.Spacer());
        // Hiding is the pet's right-click; showing is here, since a hidden pet cannot be right-clicked (#301).
        // Same setting as the Settings checkbox, on every tab so the dex can turn it on too.
        var petLabel = store.FloatingPetEnabled ? l.FloatingPetHideLabel : l.FloatingPetEnableLabel;
        var pet = Buttons.Icon(store.FloatingPetEnabled ? "" : Ui.Glyphs.Hide, petLabel,   // eye / eye.slash
            () => store.FloatingPetEnabled = !store.FloatingPetEnabled);
        System.Windows.Automation.AutomationProperties.SetName(pet, petLabel);
        row.Children.Add(pet);
        row.Children.Add(Buttons.Icon(Ui.Glyphs.Settings, l.Settings, () => s.Nav.ShowSettings = true));
        row.Children.Add(Buttons.Icon(Ui.Glyphs.Power, l.Quit, s.Quit));
        return row;
    }
}
