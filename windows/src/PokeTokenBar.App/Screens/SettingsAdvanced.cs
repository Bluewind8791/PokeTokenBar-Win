using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// The Advanced fold of Settings: claude.ai session keys (default and per account), the limit-token
/// refresh, additional Claude accounts, extra scan folders and the aggregation note.
/// The macOS "Disable Keychain access" toggle is not ported: Windows has no Keychain prompt to suppress
/// (the OAuth token is read from <c>~/.claude/.credentials.json</c>).
/// </summary>
public static partial class SettingsView
{
    private static FrameworkElement AdvancedGroup(AppServices s, SettingsState st)
    {
        var l = s.L;
        var store = s.Store;
        var chevron = Ui.Icon("\uE76C", 10, Theme.Keys.Secondary);
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform(st.AdvancedExpanded ? 90 : 0);
        var disclosure = new Border
        {
            Child = Ui.HStack(6, chevron, Label(l.AdvancedDisclosureLabel), Ui.Spacer()),
            Padding = new Thickness(12, 9, 12, 9),
            Focusable = true,
        };
        disclosure.OnClick(() => st.SetAdvancedExpanded(!st.AdvancedExpanded));
        disclosure.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Space or Key.Enter)) return;
            e.Handled = true;
            st.SetAdvancedExpanded(!st.AdvancedExpanded);
        };
        System.Windows.Automation.AutomationProperties.SetName(disclosure, l.AdvancedDisclosureLabel);

        var rows = new List<UIElement?> { disclosure };
        if (st.AdvancedExpanded)
        {
            rows.Add(Ui.Divider());
            rows.AddRange(SessionKeyRows(s, st));
            rows.AddRange(AccountSessionKeyRows(s, st));
            rows.Add(Ui.Divider());
            rows.Add(Row(LabelWithHint(l.RefreshLimitToken, l.WinOnlyOnPress),
                MacButton.Make(l.RefreshLimitToken, () => _ = store.RefreshLimitTokenFromKeychain(), busy: store.IsRefreshingLimitToken)));
            if (store.LimitTokenRefreshMessage is { } message)
                rows.Add(Note(message, Theme.Keys.Orange));
            rows.Add(Ui.Divider());
            rows.Add(AdditionalAccountsRow(s, st));
            rows.Add(Ui.Divider());
            rows.Add(CustomScanRootsRow(s, st));
            rows.Add(Ui.Divider());
            var note = Hint(l.AggregationNote);
            note.Margin = new Thickness(12, 8, 12, 8);
            rows.Add(note);
        }
        return Section(l.AdvancedSectionTitle, rows.ToArray());
    }

    /// <summary>A caption under a row (<c>.padding(.horizontal, 12).padding(.bottom, 6)</c>).</summary>
    private static TextBlock Note(string text, string brushKey = Theme.Keys.Tertiary)
    {
        var t = Hint(text, brushKey);
        t.Margin = new Thickness(12, 0, 12, 6);
        return t;
    }

    private static FrameworkElement Badge(string text, string brushKey) => Ui.Text(text, Theme.FontSize.Caption2, brushKey);

    /// <summary>claude.ai session key — the limits path that reads no credentials file. Validated when saved.</summary>
    private static IEnumerable<UIElement> SessionKeyRows(AppServices s, SettingsState st)
    {
        var l = s.L;
        var store = s.Store;
        var title = Ui.HStack(6, Label(l.SessionKeyLabel));
        if (store.SessionKeyExpired)
            // Still stored but rejected: "Saved" would leave someone who came from the expiry notice without a next step.
            title.Children.Add(Badge(l.SessionKeyExpiredBadge, Theme.Keys.Orange));
        else if (store.SessionKeyConfigured)
            title.Children.Add(Badge(l.SessionKeySaved, Theme.Keys.Green));
        // The key only replaces the default login's token; other accounts have their own below.
        var hint = store.AdditionalClaudeConfigRoots.Count == 0 ? l.WinSessionKeyHint : l.WinSessionKeyHint + " " + l.SessionKeyPerAccountNote;
        yield return Row(Ui.VStack(1, title, Hint(hint)), null);

        // The value is never shown back: a stored key leaves the field empty with a placeholder.
        Button? save = null;
        var field = MacField.Secure(store.SessionKeyConfigured ? "••••••••" : "sk-ant-sid…", v =>
        {
            st.SessionKeyInput = v;
            if (save != null) save.IsEnabled = v.Length > 0 && !store.IsValidatingSessionKey;
        }, out var box, st.SessionKeyInput);
        st.SessionKeyBox = box;
        st.TrackField("sessionKey", box);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SubmitSessionKey(s, st); } };
        save = MacButton.Make(l.Save, () => SubmitSessionKey(s, st), enabled: st.SessionKeyInput.Length > 0, busy: store.IsValidatingSessionKey);
        var keyRow = KeyFieldRow(field, save,
            store.SessionKeyConfigured ? MacButton.Make(l.Delete, () => { st.SessionKeyInput = ""; store.ClearSessionKey(); }) : null);
        yield return keyRow;

        // Only with several organizations — the automatic pick can choose the wrong personal/work account.
        if (store.SessionKeyOrganizations.Count > 1)
        {
            var picker = MacPopUp.Picker(store.SessionKeyOrganizations.Select(o => (o.Id, o.Name)).ToList(),
                store.SessionKeySelectedOrgID ?? "", id => _ = store.SelectSessionOrganization(id), l.SessionKeyOrganizationLabel);
            picker.MaxWidth = 220;
            yield return Row(Label(l.SessionKeyOrganizationLabel), picker);
        }
        if (store.SessionKeyError is { } error)
            yield return Note(error, Theme.Keys.Orange);
        else if (store.SessionKeyConfigured)
            yield return Note(l.WinSessionKeyStorageNote);
    }

    /// <summary><c>HStack { SecureField.frame(maxWidth: 220); Save; Delete?; Spacer }</c>: the field shrinks before the buttons do.</summary>
    private static FrameworkElement KeyFieldRow(FrameworkElement field, Button save, Button? delete)
    {
        var grid = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 220 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.001, GridUnitType.Star) });
        grid.Children.Add(field);
        save.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(save, 1);
        grid.Children.Add(save);
        if (delete != null)
        {
            delete.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(delete, 2);
            grid.Children.Add(delete);
        }
        return grid;
    }

    private static async void SubmitSessionKey(AppServices s, SettingsState st)
    {
        var pasted = st.SessionKeyInput;
        if (pasted.Length == 0) return;
        await s.Store.SaveSessionKey(pasted);
        // Saved: clear the field so the key does not stay on screen. Failed: keep it to fix.
        if (s.Store.SessionKeyError == null) st.SessionKeyInput = "";
        st.Rebuild();
    }

    /// <summary>Each additional Claude folder can have its own key; without one its limits come from its credentials
    /// file, which stops once that token expires.</summary>
    private static IEnumerable<UIElement> AccountSessionKeyRows(AppServices s, SettingsState st)
    {
        var l = s.L;
        var store = s.Store;
        foreach (var rootPath in store.AdditionalClaudeConfigRoots)
        {
            yield return Ui.Divider();
            var folder = ClaudeAccountRoots.AbbreviatingWithTilde(rootPath);
            var account = store.ClaudeAccounts.FirstOrDefault(a => a.Id == ClaudeAccountRoots.PathKey(rootPath));
            var title = account?.Title ?? folder;
            var configured = store.AccountSessionKeyPaths.Contains(rootPath);
            var header = Ui.HStack(6, Label(l.AccountSessionKeyLabel(title)));
            if (account?.SessionKeyExpired == true) header.Children.Add(Badge(l.SessionKeyExpiredBadge, Theme.Keys.Orange));
            else if (configured) header.Children.Add(Badge(l.SessionKeySaved, Theme.Keys.Green));
            var label = Ui.VStack(1, header);
            // The email names the browser login to copy the key from; the folder tells two logins apart.
            if (title != folder) label.Children.Add(Hint(folder));
            yield return Row(label, null);

            var input = st.AccountSessionKeyInputs.GetValueOrDefault(rootPath, "");
            var root = rootPath;
            Button? save = null;
            var field = MacField.Secure(configured ? "••••••••" : "sk-ant-sid…", v =>
            {
                st.AccountSessionKeyInputs[root] = v;
                if (save != null) save.IsEnabled = v.Length > 0 && store.ValidatingAccountSessionKeyPath == null;
            }, out var box, input);
            st.TrackField("accountKey:" + rootPath, box);
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SubmitAccountSessionKey(s, st, root); } };
            save = MacButton.Make(l.Save, () => SubmitAccountSessionKey(s, st, root),
                enabled: input.Length > 0 && store.ValidatingAccountSessionKeyPath == null,
                busy: store.ValidatingAccountSessionKeyPath == rootPath);
            var row = KeyFieldRow(field, save,
                configured ? MacButton.Make(l.Delete, () => { st.AccountSessionKeyInputs.Remove(root); store.ClearAccountSessionKey(root); }) : null);
            yield return row;
            if (store.AccountSessionKeyError(rootPath) is { } error)
                yield return Note(error, Theme.Keys.Orange);
        }
        // The storage note is shown once, under the default key when that one is saved.
        if (store.AccountSessionKeyPaths.Count > 0 && (!store.SessionKeyConfigured || store.SessionKeyError != null))
            yield return Note(l.WinSessionKeyStorageNote);
    }

    private static async void SubmitAccountSessionKey(AppServices s, SettingsState st, string rootPath)
    {
        if (!st.AccountSessionKeyInputs.TryGetValue(rootPath, out var pasted) || pasted.Length == 0) return;
        await s.Store.SaveAccountSessionKey(pasted, rootPath);
        if (s.Store.AccountSessionKeyError(rootPath) == null) st.AccountSessionKeyInputs.Remove(rootPath);
        st.Rebuild();
    }

    /// <summary>Detected and extra Claude config folders; committed on submit / focus loss (each commit refreshes).</summary>
    private static FrameworkElement AdditionalAccountsRow(AppServices s, SettingsState st)
    {
        var l = s.L;
        var store = s.Store;
        var column = Ui.VStack(6, Label(l.AdditionalClaudeAccountsLabel), Hint(l.WinAdditionalClaudeAccountsHint));
        if (store.DetectedClaudeConfigDirs.Count > 0)
            column.Children.Add(Hint(l.AdditionalClaudeAccountsDetected(string.Join(", ",
                store.DetectedClaudeConfigDirs.Select(d => ClaudeAccountRoots.AbbreviatingWithTilde(d)))), Theme.Keys.Secondary));
        var found = Hint("");
        void UpdateFound()
        {
            var blank = string.IsNullOrWhiteSpace(st.AdditionalAccountsDraft);
            found.Visibility = blank ? Visibility.Collapsed : Visibility.Visible;
            if (!blank) found.Text = l.AdditionalClaudeAccountsFound(ClaudeAccountRoots.Roots(st.AdditionalAccountsDraft).Count);
        }
        var field = MacField.Text(st.AdditionalAccountsDraft, l.AdditionalClaudeAccountsPlaceholder, v =>
        {
            st.AdditionalAccountsDraft = v;
            UpdateFound();
        }, out var box, multiline: true, fontSize: Theme.FontSize.Caption);
        st.TrackField("additionalAccounts", box);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; st.CommitAdditionalAccounts(); } };
        box.LostKeyboardFocus += (_, _) => { if (!SettingsState.IsDetached(box)) st.CommitAdditionalAccounts(); };
        column.Children.Add(field);
        column.Children.Add(found);
        UpdateFound();
        return Row(column, null);
    }

    private static FrameworkElement CustomScanRootsRow(AppServices s, SettingsState st)
    {
        var l = s.L;
        var store = s.Store;
        var providers = store.RegisteredProviders.Select(p => (p.Id, p.DisplayName)).ToList();
        var picker = MacPopUp.Picker(providers, st.SelectedScanProviderID, st.SelectScanProvider, l.CustomScanProviderLabel);
        var providerRow = new DockPanel();
        DockPanel.SetDock(picker, Dock.Right);
        providerRow.Children.Add(picker);
        providerRow.Children.Add(Ui.Text(l.CustomScanProviderLabel, Theme.FontSize.Caption));

        var matches = Hint("");
        void UpdateMatches()
        {
            var blank = string.IsNullOrWhiteSpace(st.CustomScanDraft);
            matches.Visibility = blank ? Visibility.Collapsed : Visibility.Visible;
            matches.Text = l.CustomScanRootsMatches(st.CustomScanMatchCount);
        }
        var field = MacField.Text(st.CustomScanDraft, l.CustomScanRootsPlaceholder, v =>
        {
            st.CustomScanDraft = v;
            st.ScheduleCustomScanMatchCount(UpdateMatches);
            UpdateMatches();
        }, out var box, multiline: true, fontSize: Theme.FontSize.Caption, monospaced: true);
        st.TrackField("customScan", box);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; st.CommitCustomScanDraft(); } };
        box.LostKeyboardFocus += (_, _) => { if (!SettingsState.IsDetached(box)) st.CommitCustomScanDraft(); };
        if (st.CustomScanDraft.Length > 0 && st.CustomScanMatchCount == 0) st.ScheduleCustomScanMatchCount(UpdateMatches);
        UpdateMatches();
        var browse = MacButton.Make("…", () =>
        {
            // Windows addition: pick a folder instead of typing its path. Appends to the draft and commits.
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = l.CustomScanRootsLabel, Multiselect = true };
            s.ClosePopoverForDialog();
            if (dialog.ShowDialog() != true) return;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(st.CustomScanDraft)) parts.Add(st.CustomScanDraft.TrimEnd(',', '\n', ' '));
            parts.AddRange(dialog.FolderNames.Select(f => ClaudeAccountRoots.AbbreviatingWithTilde(f)));
            st.CustomScanDraft = string.Join(",\n", parts);
            st.CommitCustomScanDraft();
        }, small: true);
        browse.ToolTip = l.CustomScanRootsLabel;
        browse.VerticalAlignment = VerticalAlignment.Top;
        var fieldRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(6, 1, 0, 0);
        fieldRow.Children.Add(browse);
        fieldRow.Children.Add(field);

        var column = Ui.VStack(6, Label(l.CustomScanRootsLabel), Hint(l.CustomScanRootsHint), providerRow, fieldRow, matches);
        return Row(column, null);
    }
}

internal static class SettingsDialogExtensions
{
    /// <summary>
    /// A file dialog deactivates the popover, which closes it (it is transient, like <c>NSPopover</c>).
    /// Close it first so the dialog does not end up owned by a closing window; the result is reported by
    /// an alert or by File Explorer, as on macOS.
    /// </summary>
    public static void ClosePopoverForDialog(this AppServices s) => s.ClosePopover();
}
