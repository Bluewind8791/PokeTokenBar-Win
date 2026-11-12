using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of the Settings screen (SettingsView.swift, DifficultySettingsSection.swift): takes over the
/// whole popover (no popover padding) with a fixed header, a scrolling body of inset-grouped sections
/// and a fixed footer, 460 pt tall.
///
/// <see cref="Live"/> rebuilds the screen on every store change, so the SwiftUI <c>@State</c> lives in
/// a <see cref="SettingsState"/> kept for one visit (dropped when Settings closes). Drafts (text fields,
/// difficulty) are only written to the stores on submit / focus loss / leaving, like the original.
/// </summary>
public static partial class SettingsView
{
    private static readonly ConditionalWeakTable<PopoverNavigation, SettingsState> States = new();
    private static readonly ConditionalWeakTable<PopoverNavigation, object> Hooked = new();

    public const double Height = 460;

    public static FrameworkElement Build(AppServices s)
    {
        MacTokens.Ensure();
        var st = StateFor(s);
        // Opened from the session-key expiry notice: start with the fold open, or the field to fix is hidden.
        if (s.Nav.ExpandAdvancedOnOpen && !st.DidApplyStartExpanded)
        {
            st.DidApplyStartExpanded = true;
            st.SetAdvancedExpanded(true, rebuild: false);
            st.RevealAdvancedOnLoad = true;
        }
        var body = Ui.VStack(18,
            GeneralGroup(s, st),
            DifficultyGroup(s, st),
            MenuBarGroup(s),
            FloatingPetGroup(s, st),
            NotificationsGroup(s, st),
            UpdateGroup(s, st),
            TransferGroup(s, st),
            st.AdvancedSection = AdvancedGroup(s, st),
            AboutSupportGroup(s, st));
        // The 8 pt overlay-style scrollbar takes its own column: trim the right padding by its width so
        // the cards keep the original 16 pt inset on both sides.
        body.Margin = new Thickness(16, 16, 8, 16);
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        st.Scroll = scroll;

        var root = new Grid { Height = Height, Margin = new Thickness(-Theme.PopoverPadding), Background = Brushes.Transparent };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        void Put(UIElement e, int row) { Grid.SetRow(e, row); root.Children.Add(e); }
        Put(Header(s, st), 0);
        Put(Ui.Divider(), 1);
        Put(scroll, 2);
        Put(Ui.Divider(), 3);
        Put(Footer(s), 4);

        st.Attach(root);
        return root;
    }

    /// <summary>Render seam: sets this visit's view state before the first build.</summary>
    internal static void Preset(AppServices s, Action<SettingsState> configure) => configure(StateFor(s));

    private static SettingsState StateFor(AppServices s)
    {
        if (!Hooked.TryGetValue(s.Nav, out _))
        {
            Hooked.Add(s.Nav, new object());
            var nav = s.Nav;
            nav.Changed += () =>
            {
                if (nav.ShowSettings || !States.TryGetValue(nav, out var leaving)) return;
                States.Remove(nav);
                leaving.Leave();
            };
        }
        if (States.TryGetValue(s.Nav, out var st)) return st;
        st = new SettingsState(s);
        States.Add(s.Nav, st);
        return st;
    }

    private static void Close(AppServices s, SettingsState st)
    {
        st.CommitDrafts();
        s.Nav.ExpandAdvancedOnOpen = false;
        s.Nav.ShowSettings = false;
    }

    // MARK: Header / footer

    private static FrameworkElement Header(AppServices s, SettingsState st)
    {
        var l = s.L;
        var back = Ui.HStack(2, Ui.Icon("\uE76B", 12, Theme.Keys.Accent), Ui.Text(l.Back, Theme.FontSize.Body, Theme.Keys.Accent));
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.OnClick(() => Close(s, st));
        System.Windows.Automation.AutomationProperties.SetName(back, l.Back);
        var title = Ui.Text(l.Settings, Theme.FontSize.Headline, weight: FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var bar = new Grid { Margin = new Thickness(14, 10, 14, 10) };
        bar.Children.Add(title);
        bar.Children.Add(back);
        return bar;
    }

    private static FrameworkElement Footer(AppServices s)
    {
        var l = s.L;
        var row = Ui.HStack(5,
            FooterText($"v{s.Updater.CurrentVersion}"),
            FooterText("·"),
            FooterLink("GitHub", "https://github.com/chattymin/PokeTokenBar"),
            FooterText("·"),
            FooterLink(l.Website, "https://chattymin.github.io/PokeTokenBar/"),
            FooterText("·"),
            // Sponsoring — a footer link with no feature lock or nudge.
            FooterLink("♥ " + l.Sponsor, "https://github.com/sponsors/chattymin"),
            Ui.Spacer());
        row.Margin = new Thickness(16, 10, 16, 10);
        return row;
    }

    private static TextBlock FooterText(string text) => Ui.Text(text, Theme.FontSize.Caption2, Theme.Keys.Tertiary);

    private static FrameworkElement FooterLink(string title, string url)
    {
        var t = FooterText(title);
        t.TextDecorations = TextDecorations.Underline;
        return t.OnClick(() => SettingsActions.OpenUrl(url)).Help(url);
    }

    // MARK: Groups

    private static FrameworkElement GeneralGroup(AppServices s, SettingsState st)
    {
        var l = s.L;
        var store = s.Store;
        var companion = s.Companion;
        var rows = new List<UIElement?>
        {
            Row(Label(l.Language), MacPopUp.Picker(Enum.GetValues<AppLanguage>().Select(x => (x, x.Label())).ToList(),
                companion.Language, lang => { companion.SetLanguage(lang); store.LocalizationLanguage = lang; }, l.Language)),
            Ui.Divider(),
            RepresentativeRow(s),
            Ui.Divider(),
            Row(Label(l.RefreshInterval), MacPopUp.Picker(UsageStore.IntervalPresets.Select(p => (p.Value, l.IntervalLabel(p.Value))).ToList(),
                store.RefreshInterval, v => store.RefreshInterval = v, l.RefreshInterval)),
            Ui.Divider(),
            // Applies to the tray sprite and the floating pet alike, so it sits here and not in the pet section.
            Row(LabelWithHint(l.AnimationQualityLabel, l.AnimationQualityHint), MacPopUp.Picker(
                [(AnimationQuality.PowerSaver, l.AnimationPowerSaver), (AnimationQuality.Balanced, l.AnimationBalanced), (AnimationQuality.Smooth, l.AnimationSmooth)],
                store.AnimationQuality, q => store.AnimationQuality = q, l.AnimationQualityLabel)),
            Ui.Divider(),
            Row(Label(l.LimitDisplayModeLabel), MacSegmented.Make(
                [(LimitPace.LimitDisplayMode.Used, l.LimitDisplayUsed), (LimitPace.LimitDisplayMode.Remaining, l.LimitDisplayRemaining)],
                store.LimitDisplayMode, m => store.LimitDisplayMode = m)),
            Ui.Divider(),
            Row(Label(l.DailyTrendPeriod), MacPopUp.Picker(Enum.GetValues<DailyTrendWindow>().Select(w => (w, l.Title(w))).ToList(),
                store.DailyTrendWindow, w => store.DailyTrendWindow = w, l.DailyTrendPeriod)),
        };
        if (store.ClaudeAccounts.Count > 1)
        {
            rows.Add(Ui.Divider());
            rows.Add(TrackedAccountRow(s));
        }
        rows.Add(Ui.Divider());
        rows.Add(LaunchAtLoginRow(s, st));
        return Section(l.GeneralSectionTitle, rows.ToArray());
    }

    private static FrameworkElement RepresentativeRow(AppServices s)
    {
        var l = s.L;
        var companion = s.Companion;
        var species = companion.RepresentativeDexSpecies;
        var text = species == null ? l.RepresentativeFollowCurrent : $"#{species.Id} {species.Name}{(species.IsShiny ? " ✨" : "")}";
        var menu = MacPopUp.Make(text,
        [
            new MacPopUp.Entry(l.RepresentativeFollowCurrent, companion.RepresentativeSpeciesID == null, () => companion.SetRepresentativeSpeciesID(null)),
            new MacPopUp.Entry(l.RepresentativeChooseFromDex, false, s.Nav.OpenRepresentativeDex),
        ], pullDown: true, small: true, accessibleName: l.RepresentativePokemonLabel, width: 150);
        // .lineLimit(1).minimumScaleFactor(0.75): one line, trimmed rather than wrapped.
        var label = Ui.Text(l.RepresentativePokemonLabel);
        return Row(label, menu);
    }

    private static FrameworkElement LaunchAtLoginRow(AppServices s, SettingsState st)
    {
        var l = s.L;
        // The macOS "only as an .app bundle" note has no Windows counterpart: the installed app always writes its
        // own Run value. Off the installed app (renders, tests) the toggle stays read-only and never touches the registry.
        var label = Ui.VStack(1, Label(l.LaunchAtLogin));
        if (st.LaunchAtLoginError is { } error)
            label.Children.Add(Hint(error, Theme.Keys.Red));
        var toggle = new MacSwitch(st.LaunchAtLogin, on =>
        {
            try
            {
                LoginItem.SetEnabled(on);
                st.LaunchAtLoginError = null;
                st.LaunchAtLogin = on;
            }
            catch (Exception ex)
            {
                AppLog.Write($"login item update failed: {ex.Message}");
                st.LaunchAtLoginError = l.UserFacingError(ex);
                st.LaunchAtLogin = LoginItem.IsEnabled;
            }
            st.Rebuild();
        }, l.LaunchAtLogin, enabled: AppEnv.IsInstalledApp);
        return Row(label, toggle);
    }

    /// <summary>Only with several Claude accounts. Stacked: account emails make the picker too wide for one line.</summary>
    private static FrameworkElement TrackedAccountRow(AppServices s)
    {
        var l = s.L;
        var store = s.Store;
        var modes = store.ClaudeAccounts.Select(a => (Mode: a.IsDefault ? ClaudeTrackedAccountMode.DefaultAccount : ClaudeTrackedAccountMode.Account(a.Id), a.Title)).ToList();
        // A pinned account that is gone falls back to automatic (UsageStore.TrackedAccount): show that.
        var mode = store.ClaudeTrackedAccountMode;
        var selection = modes.Any(m => m.Mode == mode) || mode == ClaudeTrackedAccountMode.Highest ? mode : ClaudeTrackedAccountMode.Automatic;
        var items = new List<(ClaudeTrackedAccountMode, string)>
        {
            (ClaudeTrackedAccountMode.Automatic, l.TrackedAccountAutomatic),
            (ClaudeTrackedAccountMode.Highest, l.TrackedAccountHighest),
        };
        items.AddRange(modes.Select(m => (m.Mode, m.Title)));
        var picker = MacPopUp.Picker(items, selection, m => store.ClaudeTrackedAccountMode = m, l.TrackedAccountLabel);
        picker.HorizontalAlignment = HorizontalAlignment.Right;
        return Row(Ui.VStack(6, LabelWithHint(l.TrackedAccountLabel, l.TrackedAccountHint), picker), null);
    }

    private static FrameworkElement MenuBarGroup(AppServices s)
    {
        var l = s.L;
        var store = s.Store;
        var rows = new List<UIElement?>
        {
            ToggleRow(l.TodayTokensShort, store.ShowTokensInMenu, v => store.ShowTokensInMenu = v),
            Ui.Divider(),
            ToggleRow(l.TodayCost, store.ShowCostInMenu, v => store.ShowCostInMenu = v),
            Ui.Divider(),
            ToggleRow(l.PokemonGrowthPercent, store.ShowGrowthInMenu, v => store.ShowGrowthInMenu = v, hint: l.PokemonGrowthPercentHint),
            Ui.Divider(),
            ToggleRow(l.LimitPercent, store.ShowLimitInMenu, v => store.ShowLimitInMenu = v),
        };
        if (store.ShowLimitInMenu)
        {
            rows.Add(Ui.Divider());
            rows.Add(Row(LabelWithHint(l.MenuLimitColor, l.MenuLimitColorHint), MacPopUp.Picker(
                Enum.GetValues<MenuLimitColorMode>().Select(m => (m, l.MenuLimitColorMode(m))).ToList(),
                store.MenuLimitColorMode, m => store.MenuLimitColorMode = m, l.MenuLimitColor)));
        }
        var hint = Hint(l.WinAllOffHint);
        hint.Margin = new Thickness(4, 0, 0, 0);
        return Ui.VStack(6, Section(l.WinMenuBarSectionTitle, rows.ToArray()), hint);
    }

    private static FrameworkElement FloatingPetGroup(AppServices s, SettingsState st)
    {
        var l = s.L;
        var store = s.Store;
        var rows = new List<UIElement?>
        {
            Row(LabelWithHint(l.FloatingPetEnableLabel, l.FloatingPetHint),
                new MacSwitch(store.FloatingPetEnabled, v => store.FloatingPetEnabled = v, l.FloatingPetEnableLabel)),
            Ui.Divider(),
            // Also reachable from both right-click menus. Applies to every surface, not only the pet.
            Row(LabelWithHint(l.BackViewLabel, l.BackViewHint),
                new MacSwitch(store.AlwaysFacesBack, v => store.AlwaysFacesBack = v, l.BackViewLabel)),
        };
        if (store.FloatingPetEnabled)
        {
            rows.Add(Ui.Divider());
            rows.Add(SliderRow(l.FloatingPetSizeLabel, 48, 384, 8, store.FloatingPetSize, v => $"{(int)v}px", 44,
                v => store.FloatingPetSize = v));
            rows.Add(Ui.Divider());
            rows.Add(ToggleRow(l.FloatingPetBubbleAlertsLabel, store.FloatingPetBubbleAlerts, v => store.FloatingPetBubbleAlerts = v));
            rows.Add(Ui.Divider());
            rows.Add(Row(LabelWithHint(l.FloatingPetAgentActivityLabel, l.FloatingPetAgentActivityHint),
                new MacSwitch(store.FloatingPetAgentActivity, v => store.FloatingPetAgentActivity = v, l.FloatingPetAgentActivityLabel)));
            if (store.FloatingPetAgentActivity)
            {
                rows.Add(Ui.Divider());
                rows.Add(ToggleRow(l.WinFloatingPetAgentRunLabel, store.FloatingPetAgentRun, v => store.FloatingPetAgentRun = v));
            }
        }
        return Section(l.FloatingPetSectionTitle, rows.ToArray());
    }

    private static FrameworkElement NotificationsGroup(AppServices s, SettingsState st)
    {
        var l = s.L;
        var store = s.Store;
        var rows = new List<UIElement?> { ToggleRow(l.LimitNotificationsLabel, store.LimitNotifications, v => store.LimitNotifications = v) };
        if (store.LimitNotifications)
        {
            rows.Add(Ui.Divider());
            rows.Add(SliderRow(l.Warning, 50, 95, 5, store.WarnThreshold, TokenFormatter.Percent, 38, v => store.WarnThreshold = v));
            rows.Add(Ui.Divider());
            rows.Add(SliderRow(l.Critical, 80, 100, 5, store.CritThreshold, TokenFormatter.Percent, 38, v => store.CritThreshold = v));
        }
        rows.Add(Ui.Divider());
        rows.Add(ToggleRow(l.CompanionNotificationsLabel, store.CompanionNotifications, v => store.CompanionNotifications = v));
        rows.Add(Ui.Divider());
        rows.Add(Row(LabelWithHint(l.StatusChecksLabel, l.StatusChecksHint),
            new MacSwitch(store.StatusChecksEnabled, v => store.StatusChecksEnabled = v, l.StatusChecksLabel)));
        return Section(l.NotificationsSection, rows.ToArray());
    }

    private static FrameworkElement UpdateGroup(AppServices s, SettingsState st)
    {
        var l = s.L;
        var store = s.Store;
        var updater = s.Updater;
        var rows = new List<UIElement?>
        {
            ToggleRow(l.UpdateNotificationsLabel, store.UpdateNotificationsEnabled, v => store.UpdateNotificationsEnabled = v),
            Ui.Divider(),
            Row(Label(l.CheckForUpdatesLabel), MacButton.Make(l.CheckNowButton, async () =>
            {
                st.IsCheckingUpdate = true;
                st.Rebuild();
                await updater.Check(TimeSpan.Zero);   // manual check: bypasses the rate limit
                st.IsCheckingUpdate = false;
                st.DidCheckUpdate = true;
                st.Rebuild();
            }, busy: st.IsCheckingUpdate)),
        };
        // The result — users with the banner off still learn about and apply a new version here.
        if (st.DidCheckUpdate && !st.IsCheckingUpdate)
        {
            rows.Add(Ui.Divider());
            var notice = updater.Notice;
            FrameworkElement result;
            if (notice.Kind == UpdateChecker.NoticeKind.Offer)
            {
                result = Row(Hint(l.UpdateFound(notice.Version ?? ""), Theme.Keys.Orange, Theme.FontSize.Caption),
                    MacButton.Make(l.UpdateButton, () => SettingsActions.ApplyUpdate(s), small: true));
            }
            else if (notice.Kind == UpdateChecker.NoticeKind.Skipped)
            {
                result = Row(Ui.VStack(6,
                    Hint(l.SkippedVersion(notice.Version ?? ""), Theme.Keys.Orange, Theme.FontSize.Caption),
                    Ui.HStack(8,
                        MacButton.Make(l.ShowSkippedAgain, updater.ShowSkippedAgain, small: true),
                        MacButton.Make(l.UpdateButton, () => SettingsActions.ApplyUpdate(s), small: true))), null);
            }
            else
            {
                result = Row(Hint(l.UpToDate(updater.CurrentVersion), Theme.Keys.Secondary, Theme.FontSize.Caption), null);
            }
            rows.Add(result);
        }
        return Section(l.UpdateSectionTitle, rows.ToArray());
    }

    /// <summary>Backup &amp; transfer — export/import the save through the standard file dialogs, and local snapshots.</summary>
    private static FrameworkElement TransferGroup(AppServices s, SettingsState st)
    {
        var l = s.L;
        var rows = new List<UIElement?>
        {
            Row(LabelWithHint(l.ExportSaveLabel, l.ExportSaveHint), MacButton.Make(l.ExportSaveButton, () => SettingsActions.ExportSave(s))),
            Ui.Divider(),
            Row(LabelWithHint(l.ImportSaveLabel, l.WinImportSaveHint), MacButton.Make(l.ImportSaveButton, () => SettingsActions.ImportSave(s))),
            Ui.Divider(),
            Row(Hint(l.CreateSnapshotHint), MacButton.Make(l.CreateSnapshotButton, () => SettingsActions.TakeManualSnapshot(s))),
            Ui.Divider(),
            SnapshotHistoryButton(s, st),
        };
        if (st.SnapshotsExpanded)
        {
            var snapshots = s.Companion.AvailableSnapshots;
            if (snapshots.Count == 0)
                rows.Add(Row(Hint(l.NoSnapshotsYet), null));
            else
                foreach (var snapshot in snapshots)
                {
                    rows.Add(Ui.Divider());
                    rows.Add(SnapshotRow(s, snapshot));
                }
        }
        return Section(l.TransferSectionTitle, rows.ToArray());
    }

    /// <summary>A full-width disclosure button (the macOS one is a native NSButton for keyboard access).</summary>
    private static FrameworkElement SnapshotHistoryButton(AppServices s, SettingsState st)
    {
        var l = s.L;
        var chevron = Ui.Icon(st.SnapshotsExpanded ? "\uE70D" : "\uE76C", 9, Theme.Keys.Secondary);
        var row = Ui.HStack(5, chevron, Ui.Text(l.SnapshotsSectionTitle, Theme.FontSize.Body, Theme.Keys.Secondary), Ui.Spacer());
        var button = new Border { Child = row, Height = 20, Margin = new Thickness(12, 8, 12, 8), Focusable = true };
        button.OnClick(() => { st.SnapshotsExpanded = !st.SnapshotsExpanded; st.Rebuild(); });
        button.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Space or Key.Enter)) return;
            e.Handled = true;
            st.SnapshotsExpanded = !st.SnapshotsExpanded;
            st.Rebuild();
        };
        System.Windows.Automation.AutomationProperties.SetName(button, l.SnapshotsSectionTitle);
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, l.SnapshotHistoryState(st.SnapshotsExpanded));
        return button;
    }

    private static FrameworkElement SnapshotRow(AppServices s, SaveSnapshot snapshot)
    {
        var l = s.L;
        var sprite = snapshot.CurrentSpeciesID is { } id
            ? new SpriteView(id, 28, shiny: snapshot.CurrentIsShiny, form: snapshot.CurrentForm)
            : new SpriteView(null, 28);
        var info = Ui.VStack(2,
            Ui.Text(SettingsActions.ExportedAtText(snapshot.Date, s.Companion.Language), Theme.FontSize.Caption),
            Ui.Text(l.SnapshotDexAndTokens(snapshot.DexCount, TokenFormatter.Compact(snapshot.LifetimeTokens)), Theme.FontSize.Caption2, Theme.Keys.Secondary));
        return Row(Ui.HStack(8, sprite, info), MacButton.Make(l.RestoreSnapshotButton, () => SettingsActions.ConfirmAndRestoreSnapshot(s, snapshot), small: true));
    }

    private static FrameworkElement AboutSupportGroup(AppServices s, SettingsState st)
    {
        var l = s.L;
        var rows = new List<UIElement?>
        {
            Row(LabelWithHint(l.ReportProblem, l.ReportAttachHint), MacButton.Make(l.ReportProblem, () =>
            {
                st.ReportFailed = !SettingsActions.ReportProblem(s);
                st.Rebuild();
            })),
            Ui.Divider(),
            // Next to "report a problem" so the log is at hand when writing (outside the advanced fold).
            // macOS reveals it in Finder; here File Explorer selects it.
            Row(Label(l.ShowLogFile), MacButton.Make("Explorer", SettingsActions.RevealLogFile)),
        };
        if (st.ReportFailed)
        {
            // Selectable, like .textSelection(.enabled): the address is meant to be copied.
            var fallback = new TextBox
            {
                Text = l.ReportMailFallback(SettingsActions.SupportAddress), IsReadOnly = true, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, FontSize = Theme.FontSize.Caption2,
                FontFamily = Theme.UiFont, Padding = new Thickness(0), Margin = new Thickness(12, 0, 12, 6),
            };
            fallback.SetResourceReference(Control.ForegroundProperty, Theme.Keys.Orange);
            rows.Add(fallback);
        }
        return Section(l.AboutSupportSectionTitle, rows.ToArray());
    }

    // MARK: Shared builders

    /// <summary>Section = small uppercase gray title + rounded card (the macOS inset-grouped look).</summary>
    internal static FrameworkElement Section(string title, params UIElement?[] rows)
    {
        var header = Ui.Text(title.ToUpper(System.Globalization.CultureInfo.CurrentCulture), Theme.FontSize.Caption, Theme.Keys.Secondary, FontWeights.SemiBold);
        header.Margin = new Thickness(4, 0, 0, 0);
        return Ui.VStack(6, header, Card(rows));
    }

    internal static Border Card(params UIElement?[] rows)
    {
        var card = new Border
        {
            Child = Ui.VStack(0, rows),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        }.Bg(MacTokens.Card);
        card.SetResourceReference(Border.BorderBrushProperty, MacTokens.CardStroke);
        return card;
    }

    /// <summary>One line inside a card — label left, control right (<c>groupRow</c>). The control is measured
    /// first so a long translated label wraps or trims instead of pushing it out.</summary>
    internal static FrameworkElement Row(UIElement left, UIElement? right)
    {
        var dock = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        if (right != null)
        {
            DockPanel.SetDock(right, Dock.Right);
            if (right is FrameworkElement fe) fe.Margin = new Thickness(10, 0, 0, 0);
            dock.Children.Add(right);
        }
        if (left is FrameworkElement lf) lf.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(left);
        return new Border { Child = dock, Padding = new Thickness(12, 8, 12, 8), MinHeight = 38 };
    }

    /// <summary>A row label; wraps like a SwiftUI <c>Text</c> when a translation is long.</summary>
    internal static TextBlock Label(string text, double size = Theme.FontSize.Body) => Ui.Text(text, size).Wrapping();

    internal static TextBlock Hint(string text, string brushKey = Theme.Keys.Tertiary, double size = Theme.FontSize.Caption2) =>
        Ui.Text(text, size, brushKey).Wrapping();

    internal static FrameworkElement LabelWithHint(string label, string hint) =>
        Ui.VStack(1, Label(label), Hint(hint));

    internal static FrameworkElement ToggleRow(string label, bool isOn, Action<bool> onChange, string? hint = null) =>
        Row(hint == null ? Label(label) : LabelWithHint(label, hint), new MacSwitch(isOn, onChange, label));

    /// <summary><c>HStack { Text.font(.callout); Slider; Text(value).font(.caption).monospacedDigit().frame(width:) }</c>.</summary>
    private static FrameworkElement SliderRow(string label, double min, double max, double step, double value,
        Func<double, string> format, double valueWidth, Action<double> commit)
    {
        var valueText = Ui.Text(format(value), Theme.FontSize.Caption, monospacedDigits: true);
        valueText.Width = valueWidth;
        valueText.TextAlignment = TextAlignment.Right;
        var slider = new MacSlider(min, max, value, commit, v => valueText.Text = format(v), step, label);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Ui.Text(label, Theme.FontSize.Callout);
        slider.Margin = new Thickness(10, 0, 10, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(valueText);
        return new Border { Child = grid, Padding = new Thickness(12, 8, 12, 8), MinHeight = 38 };
    }
}

/// <summary>The SwiftUI <c>@State</c> of one Settings visit.</summary>
internal sealed class SettingsState
{
    private readonly AppServices _s;
    public SettingsState(AppServices s)
    {
        _s = s;
        // onAppear: refresh the snapshot list, start collapsed.
        s.Companion.RefreshSnapshots();
        // Only the installed app reads (and later writes) the Run value: renders never touch the registry.
        LaunchAtLogin = AppEnv.IsInstalledApp && LoginItem.IsEnabled;
        Difficulty = new DifficultyDraft(s.Companion);
    }

    public bool AdvancedExpanded { get; private set; }
    public bool SnapshotsExpanded;
    public bool DidApplyStartExpanded;
    public bool RevealAdvancedOnLoad;
    public bool LaunchAtLogin;
    public string? LaunchAtLoginError;
    public bool ReportFailed;
    public bool IsCheckingUpdate, DidCheckUpdate;
    public DifficultyDraft Difficulty;

    // Advanced drafts
    public string SessionKeyInput = "";
    public readonly Dictionary<string, string> AccountSessionKeyInputs = new(StringComparer.OrdinalIgnoreCase);
    public string SelectedScanProviderID = "claude_code";
    /// <summary>The provider the draft describes. A picker change moves the selection before the field blurs;
    /// committing against the selection would write one provider's paths into another's key.</summary>
    public string CustomScanDraftOwnerID = "claude_code";
    public string CustomScanDraft = "";
    public int CustomScanMatchCount;
    private CancellationTokenSource? _matchCts;
    private int _matchGeneration;
    public string AdditionalAccountsDraft = "";

    /// <summary>Per-field focus carried across rebuilds (the whole tree is rebuilt on every store change).</summary>
    public readonly Dictionary<string, (Control Box, int Caret)> Fields = new();

    public FrameworkElement? Root;
    public ScrollViewer? Scroll;
    public FrameworkElement? AdvancedSection;
    public PasswordBox? SessionKeyBox;

    /// <summary>Counts survivors of the draft among the curated roots. Swappable so renders never walk the disk.</summary>
    internal static Func<string, string, int> CountScanMatches = (providerID, raw) =>
        CustomScanRoots.SurvivingExtraCount(CuratedRoots(providerID), raw);

    private static IReadOnlyList<string> CuratedRoots(string providerID) => providerID switch
    {
        "claude_code" => LocalUsageReader.ComputeClaudeProjectRoots(UsageEnvironment.Value("CLAUDE_CONFIG_DIR"), null,
            ClaudeAccountRoots.InstalledAccountRoots()),
        _ => [],
    };

    public void SetAdvancedExpanded(bool expanded, bool rebuild = true)
    {
        if (expanded == AdvancedExpanded) return;
        AdvancedExpanded = expanded;
        if (expanded)
        {
            // onAppear of the rows inside the fold.
            CustomScanDraftOwnerID = SelectedScanProviderID;
            CustomScanDraft = _s.Store.CustomScanRoots(SelectedScanProviderID);
            AdditionalAccountsDraft = _s.Store.AdditionalClaudeConfigDirs;
            ScheduleCustomScanMatchCount();
            // After a restart the candidates are empty and the organization could not be changed: fill them once.
            _ = _s.Store.RefreshSessionOrganizations();
        }
        else
        {
            CommitDrafts();
            _matchCts?.Cancel();
        }
        if (rebuild) Rebuild();
    }

    public void CommitCustomScanDraft() => _s.Store.SetCustomScanRoots(CustomScanDraft, CustomScanDraftOwnerID);

    public void CommitAdditionalAccounts() => _s.Store.AdditionalClaudeConfigDirs = AdditionalAccountsDraft;

    /// <summary>onDisappear: drafts left in a field still count when Settings closes or the fold collapses.</summary>
    public void CommitDrafts()
    {
        if (!AdvancedExpanded) return;
        CommitCustomScanDraft();
        CommitAdditionalAccounts();
    }

    public void SelectScanProvider(string id)
    {
        _s.Store.SetCustomScanRoots(CustomScanDraft, CustomScanDraftOwnerID);
        SelectedScanProviderID = id;
        CustomScanDraftOwnerID = id;
        CustomScanDraft = _s.Store.CustomScanRoots(id);
        ScheduleCustomScanMatchCount();
        Rebuild();
    }

    /// <summary>Debounced off the UI thread (a Claude Desktop walk is slow); a stale count never overwrites a newer one.</summary>
    public void ScheduleCustomScanMatchCount(Action? updated = null)
    {
        _matchCts?.Cancel();
        var cts = _matchCts = new CancellationTokenSource();
        var generation = ++_matchGeneration;
        var raw = CustomScanDraft;
        var providerID = SelectedScanProviderID;
        if (string.IsNullOrWhiteSpace(raw)) return;   // the count is only shown for a non-empty draft
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(250, cts.Token); } catch (TaskCanceledException) { return; }
            var count = CountScanMatches(providerID, raw);
            if (cts.IsCancellationRequested) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (generation != _matchGeneration) return;
                CustomScanMatchCount = count;
                updated?.Invoke();
            });
        });
    }

    /// <summary>The visit ends (Back, popover reopened at Home): keep what was typed, stop background work.</summary>
    public void Leave()
    {
        CommitDrafts();
        _matchCts?.Cancel();
    }

    /// <summary>Called once per build with the new root: wires Esc, the start-expanded reveal and focus carry-over.</summary>
    public void Attach(FrameworkElement root)
    {
        Root = root;
        Window? window = null;
        void OnKey(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !_s.Nav.ShowSettings) return;
            e.Handled = true;
            CommitDrafts();
            _s.Nav.ExpandAdvancedOnOpen = false;
            _s.Nav.ShowSettings = false;
        }
        root.Loaded += (_, _) =>
        {
            window = Window.GetWindow(root);
            if (window != null) window.PreviewKeyDown += OnKey;
            foreach (var (box, caret) in Fields.Values.Where(f => f.Box.Tag is true).ToList())
            {
                box.Tag = null;
                box.Focus();
                if (box is TextBox t) t.CaretIndex = Math.Min(caret, t.Text.Length);
            }
            if (RevealAdvancedOnLoad)
            {
                RevealAdvancedOnLoad = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    // The initial navigation must show the repair field without waiting for an animation.
                    if (Scroll?.Content is UIElement content && AdvancedSection != null && AdvancedSection.IsDescendantOf(content))
                        Scroll.ScrollToVerticalOffset(AdvancedSection.TranslatePoint(new Point(0, 0), content).Y);
                    SessionKeyBox?.Focus();
                };
                timer.Start();
            }
        };
        root.Unloaded += (_, _) =>
        {
            if (window != null) window.PreviewKeyDown -= OnKey;
            // Popover closed while Settings was open (not a rebuild, whose new root is loaded by now).
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (Root is { IsLoaded: false } && ReferenceEquals(Root, root)) CommitDrafts();
            });
        };
    }

    /// <summary>
    /// Registers a text input under a stable id: a rebuild while it has focus hands focus and caret to its
    /// replacement. Returns whether the previous instance was focused (its blur is a rebuild, not the user's).
    /// </summary>
    public void TrackField(string id, Control box)
    {
        if (Fields.TryGetValue(id, out var previous) && previous.Box.IsKeyboardFocusWithin)
        {
            var caret = previous.Box is TextBox t ? t.CaretIndex : 0;
            box.Tag = true;   // refocus on load
            Fields[id] = (box, caret);
            return;
        }
        Fields[id] = (box, 0);
    }

    /// <summary>A blur caused by a rebuild removing the field is not a commit (the user is still typing).</summary>
    public static bool IsDetached(Visual box) => PresentationSource.FromVisual(box) == null;

    /// <summary>Re-runs the view for a local state change (SwiftUI re-renders on @State writes).</summary>
    public void Rebuild()
    {
        DependencyObject? node = Root;
        while (node != null)
        {
            if (node is Live live) { live.Rebuild(); return; }
            node = LogicalTreeHelper.GetParent(node) ?? (node is Visual ? VisualTreeHelper.GetParent(node) : null);
        }
    }
}
