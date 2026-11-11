using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of the usage recap screen (<c>RecapScreen</c> in UsageRecapView.swift): Back + scope picker, a
/// row that moves through time, the Pokédex-style card and two caption lines. Takes over the popover
/// like Settings; scope and offset are the screen's <c>@State</c>, kept for one visit.
/// </summary>
public static class RecapScreen
{
    private sealed class State
    {
        public RecapScope Scope = RecapScope.Week;
        /// <summary>0 is the running period, -1 the one before it. Never positive: the future has no usage.</summary>
        public int Offset;
        public FrameworkElement? Root;
    }

    private static readonly ConditionalWeakTable<PopoverNavigation, State> States = new();
    private static readonly ConditionalWeakTable<PopoverNavigation, object> Hooked = new();

    public static FrameworkElement Build(AppServices s)
    {
        MacTokens.Ensure();
        var st = StateFor(s);
        var l = s.L;
        var content = new RecapContent(s.Store, s.Companion, st.Scope, st.Offset);

        void Back() => s.Nav.ShowingRecap = false;
        void Rebuild()
        {
            DependencyObject? node = st.Root;
            while (node != null)
            {
                if (node is Live live) { live.Rebuild(); return; }
                node = LogicalTreeHelper.GetParent(node);
            }
        }

        var back = Ui.HStack(3, Ui.Icon("\uE76B", 12, Theme.Keys.Secondary), Ui.Text(l.Back, Theme.FontSize.Body, Theme.Keys.Secondary));
        back.OnClick(Back);
        System.Windows.Automation.AutomationProperties.SetName(back, l.Back);
        var scopes = Enum.GetValues<RecapScope>().Select(x => (x, l.RecapScopeName(x))).ToList();
        var picker = MacSegmented.Make(scopes, st.Scope, scope => { st.Scope = scope; st.Offset = 0; Rebuild(); }, small: true);
        System.Windows.Automation.AutomationProperties.SetName(picker, l.RecapOpen);
        var header = new DockPanel();
        DockPanel.SetDock(picker, Dock.Right);
        header.Children.Add(picker);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        header.Children.Add(back);

        // The period itself is printed on the card, so this row only moves through time.
        var previous = NavButton(l.RecapPrevious, "\uEDD9", leading: true, content.Recap.CanGoBack, () => { st.Offset -= 1; Rebuild(); });
        var current = NavButton(l.RecapCurrentPeriod(st.Scope), null, leading: true, st.Offset != 0, () => { st.Offset = 0; Rebuild(); });
        var next = NavButton(l.RecapNext, "\uEDDA", leading: false, st.Offset < 0, () => { st.Offset += 1; Rebuild(); });
        var timeRow = Ui.HStack(0, previous, Ui.Spacer(), current, Ui.Spacer(), next);

        var captions = Ui.VStack(2);
        if (content.Recap.BestDay is { } best)
            captions.Children.Add(Ui.Text(l.RecapBestDayLine(content.DayLabel(best.Key), TokenFormatter.Grouped(best.Tokens)),
                Theme.FontSize.Caption, Theme.Keys.Secondary).Wrapping());
        if (content.Recap.IsInProgress && content.Recap.Delta != null)
            captions.Children.Add(Ui.Text(l.RecapCompareSoFar(st.Scope), Theme.FontSize.Caption, Theme.Keys.Secondary).Wrapping());

        var card = RecapCard.Build(content);
        card.HorizontalAlignment = HorizontalAlignment.Left;
        var root = Ui.VStack(8, header, timeRow, card, captions);
        root.Background = System.Windows.Media.Brushes.Transparent;
        st.Root = root;

        // Esc goes back, like Settings: both take over the popover.
        Window? window = null;
        void OnKey(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !s.Nav.ShowingRecap) return;
            e.Handled = true;
            Back();
        }
        root.Loaded += (_, _) => { window = Window.GetWindow(root); if (window != null) window.PreviewKeyDown += OnKey; };
        root.Unloaded += (_, _) => { if (window != null) window.PreviewKeyDown -= OnKey; };
        return root;
    }

    /// <summary>Render seam: opens this visit on another scope.</summary>
    internal static void Preset(AppServices s, RecapScope scope) => StateFor(s).Scope = scope;

    private static State StateFor(AppServices s)
    {
        if (!Hooked.TryGetValue(s.Nav, out _))
        {
            Hooked.Add(s.Nav, new object());
            var nav = s.Nav;
            nav.Changed += () => { if (!nav.ShowingRecap) States.Remove(nav); };
        }
        if (States.TryGetValue(s.Nav, out var st)) return st;
        st = new State();
        States.Add(s.Nav, st);
        // .task: the graduate chips read stored names; old saves get them filled once per visit.
        _ = s.Companion.BackfillMissingDexNames();
        return st;
    }

    /// <summary>A borderless caption button (<c>.buttonStyle(.borderless).font(.caption)</c>), dimmed when disabled.</summary>
    private static FrameworkElement NavButton(string title, string? glyph, bool leading, bool enabled, Action action)
    {
        var key = enabled ? Theme.Keys.Secondary : Theme.Keys.Tertiary;
        var text = Ui.Text(title, Theme.FontSize.Caption, key);
        var row = Ui.HStack(4);
        var icon = glyph == null ? null : Ui.Icon(glyph, 8, key);
        if (leading && icon != null) row.Children.Add(icon);
        row.Children.Add(text);
        if (!leading && icon != null) row.Children.Add(icon);
        System.Windows.Automation.AutomationProperties.SetName(row, title);
        if (enabled) row.OnClick(action);
        else row.IsEnabled = false;
        return row;
    }
}
