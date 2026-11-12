using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// The Collection tab's SwiftUI <c>@State</c>. <see cref="Live"/> rebuilds the view tree on every store or
/// navigation change, so the state lives here (one per <see cref="AppServices"/>) and is reset when the tab
/// leaves the screen, as SwiftUI drops a removed view's state.
/// </summary>
internal sealed class CollectionState
{
    public enum Screen { None, Empty, Grid, Log, Detail }

    public int Generation;
    public Screen Shown;

    public Rarity? SelectedRarity;
    /// <summary>The generation chip filter of the Pokédex grid (the catch log has none).</summary>
    public PokedexRegion? SelectedRegion;
    public string SearchText = "";
    public bool ShinyOnly;
    public DexSortOption DexSort = DexSortOption.NumberAsc;
    public CatchLogSortOption LogSort = CatchLogSortOption.RecentFirst;
    /// <summary>The species whose detail page was just closed: the rebuilt grid opens on its page. Used once, and
    /// set only when Back lands on the grid; the grid's appearance clears it.</summary>
    public string? DexReturnAnchor;
    /// <summary>The log row (<c>DexEntry.Id</c>) that opened the detail page, scrolled back into view. Used once.</summary>
    public string? LogReturnAnchor;

    public DexGridState? Grid;
    public PokemonDetailState? Detail;
    /// <summary>Reused across rebuilds so typing never loses focus or the caret.</summary>
    public TextBox? Search;

    public void Reset()
    {
        Shown = Screen.None;
        SelectedRarity = null;
        SelectedRegion = null;
        SearchText = "";
        ShinyOnly = false;
        DexSort = DexSortOption.NumberAsc;
        LogSort = CatchLogSortOption.RecentFirst;
        DexReturnAnchor = null;
        LogReturnAnchor = null;
        Grid = null;
        Detail = null;
        Search = null;
    }

    public void ResetFilters()
    {
        SearchText = "";
        ShinyOnly = false;
        SelectedRarity = null;
        SelectedRegion = null;
        DexSort = DexSortOption.NumberAsc;
        LogSort = CatchLogSortOption.RecentFirst;
        if (Search != null) Search.Text = "";
    }
}

/// <summary>
/// Port of <c>CollectionView</c> (CompanionView.swift): the Pokédex and the catch log as sub-segments, plus the
/// Pokémon detail page that replaces them. The two lists look at the same data on different axes:
/// <list type="bullet">
/// <item><b>Pokédex</b>: one cell per species. Unown's 28 letters are gathered on its detail page.</item>
/// <item><b>Log</b>: one row per individual. A line appearing in several rows is expected — per-individual
/// facts (nature, catch time) live only here.</item>
/// </list>
/// Both share a fixed 520 pt height (the same as Shop and Bag), so neither switching segment nor tab resizes the
/// popover.
/// </summary>
public static class CollectionView
{
    public const double ContentHeight = 520;

    private static readonly ConditionalWeakTable<AppServices, CollectionState> States = new();

    /// <summary>Render harness: sets the tab's state (a filter) before the first build.</summary>
    internal static void Preset(AppServices s, Action<CollectionState> configure) =>
        configure(States.GetValue(s, _ => new CollectionState()));

    public static FrameworkElement Build(AppServices s)
    {
        DexTheme.Ensure();
        var st = States.GetValue(s, _ => new CollectionState());
        var gen = ++st.Generation;
        var host = new ContentControl { Focusable = false };
        var preserveSearchFocus = st.Search?.IsKeyboardFocusWithin == true;

        void Refresh(bool keepScroll)
        {
            var offsets = keepScroll ? VerticalScrollers(host.Content as DependencyObject).Select(v => v.VerticalOffset).ToList() : [];
            preserveSearchFocus = st.Search?.IsKeyboardFocusWithin == true;
            host.Content = Screen(s, st, Refresh);
            RestoreSearchFocus();
            if (offsets.Count == 0) return;
            host.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                var viewers = VerticalScrollers(host.Content as DependencyObject).ToList();
                for (var i = 0; i < Math.Min(viewers.Count, offsets.Count); i++) viewers[i].ScrollToVerticalOffset(offsets[i]);
            });
        }

        void RestoreSearchFocus()
        {
            if (!preserveSearchFocus || st.Search is not { } tb) return;
            host.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (tb.IsLoaded) tb.Focus(); });
        }

        host.Content = Screen(s, st, Refresh);
        RestoreSearchFocus();
        // Leaving the tab (no newer build replaced this one) drops the state, as SwiftUI does.
        host.Unloaded += (_, _) => host.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (st.Generation == gen && !host.IsLoaded) st.Reset();
        });
        return host;
    }

    private static FrameworkElement Screen(AppServices s, CollectionState st, Action<bool> refresh)
    {
        var store = s.Companion;
        var nav = s.Nav;
        var previous = st.Shown;
        if (store.DexEntries.Count == 0)
        {
            st.Shown = CollectionState.Screen.Empty;
            return EmptyState(store.L);
        }
        if (nav.DexDetailCollectionID is { } id && store.DexSpeciesList.FirstOrDefault(sp => sp.CollectionID == id) is { } species)
        {
            // `.id(species.collectionID)`: another species is a fresh page.
            if (previous != CollectionState.Screen.Detail || st.Detail?.CollectionID != id) st.Detail = new PokemonDetailState(id);
            st.Shown = CollectionState.Screen.Detail;
            var detail = PokemonDetailView.Build(s, species, st.Detail!, onBack: () =>
            {
                // Back to the log must not leave a return page for the next switch to the grid.
                if (!nav.ShowingCollectionLog) st.DexReturnAnchor = id;
                nav.DexDetailCollectionID = null;
            }, refresh: () => refresh(true));
            detail.Height = ContentHeight;
            if (previous != CollectionState.Screen.Detail) ScrollToTopWhenShown(detail);
            return detail;
        }

        var showingLog = nav.ShowingCollectionLog;
        var screen = showingLog ? CollectionState.Screen.Log : CollectionState.Screen.Grid;
        var appearing = previous != screen;
        st.Shown = screen;
        if (!showingLog && (appearing || st.Grid == null))
        {
            // The grid keeps its own copy of the anchor; a later rebuild (segment switch) starts on page 1.
            st.Grid = new DexGridState(st.DexReturnAnchor);
            st.DexReturnAnchor = null;
        }

        var picker = SegmentedControl.Make([("dex", store.L.DexTitle), ("log", store.L.CatchLogTitle)],
            showingLog ? "log" : "dex",
            // Switching segment drops the open detail page (PopoverNavigation.ShowingCollectionLog).
            sel => nav.ShowingCollectionLog = sel == "log");
        picker.HorizontalAlignment = HorizontalAlignment.Left;
        TuneSegments(picker);

        var root = new Grid { Height = ContentHeight };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(picker);
        var content = new ContentControl { Focusable = false, Margin = new Thickness(0, 6, 0, 0) };
        var toolbar = FilterToolbar(s, st, showingLog, () => refresh(false), () => content.Content = List());
        toolbar.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);
        Grid.SetRow(content, 2);
        root.Children.Add(content);

        FrameworkElement List() => showingLog
            ? CatchLog(s, st, () => refresh(false), appearing)
            : DexGridView.Build(new DexGridView.Context(s, st, st.Grid!,
                sp => nav.DexDetailCollectionID = sp.CollectionID,
                () => { st.ResetFilters(); st.Grid?.ResetPage(); refresh(false); },
                r => { st.SelectedRarity = st.SelectedRarity == r ? null : r; st.Grid?.ResetPage(); refresh(false); },
                g => { st.SelectedRegion = st.SelectedRegion == g ? null : g; st.Grid?.ResetPage(); refresh(false); },
                () => refresh(false)));
        content.Content = List();
        return root;
    }

    /// <summary>macOS segmented pickers size segments to their labels plus generous insets.</summary>
    private static void TuneSegments(FrameworkElement picker)
    {
        if (picker is Border { Child: UniformGrid grid })
            foreach (var seg in grid.Children.OfType<Border>())
                seg.Padding = new Thickness(11, 2, 11, 3);
    }

    /// <summary><see cref="Live"/> carries the previous screen's scroll offset over to the new tree; a screen that
    /// just appeared starts at the top instead (runs after Live's restore).</summary>
    private static void ScrollToTopWhenShown(FrameworkElement root) =>
        root.Loaded += (_, _) => root.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            foreach (var v in VerticalScrollers(root)) v.ScrollToVerticalOffset(0);
        });

    internal static IEnumerable<ScrollViewer> VerticalScrollers(DependencyObject? root)
    {
        if (root == null) yield break;
        if (root is ScrollViewer sv && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled) yield return sv;
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
            foreach (var child in VerticalScrollers(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }

    // MARK: Filter toolbar

    private static FrameworkElement FilterToolbar(AppServices s, CollectionState st, bool showingLog, Action refreshAll, Action refreshList)
    {
        var l = s.Companion.L;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(SearchField(st, l, refreshList));

        // Off: one outlined sparkle in secondary; on: the yellow sparkles.
        var shiny = st.ShinyOnly ? DexUi.Sparkles(15) : DexUi.Sparkle(12, Theme.Keys.Secondary);
        shiny.HorizontalAlignment = HorizontalAlignment.Center;
        var shinyBox = DexUi.Rounded(new Grid { Width = 16, Height = 16, Children = { shiny } },
            st.ShinyOnly ? DexTheme.Tint(Theme.Keys.Yellow, 0.18) : DexTheme.ControlBackground, 6, new Thickness(6, 4, 6, 4),
            st.ShinyOnly ? DexTheme.Tint(Theme.Keys.Yellow, 0.6) : DexTheme.Tint(Theme.Keys.Secondary, 0.2), 0.5);
        shinyBox.OnClick(() =>
        {
            st.ShinyOnly = !st.ShinyOnly;
            st.Grid?.ResetPage();
            refreshAll();
        }).Help(l.FilterShinyOnly);
        System.Windows.Automation.AutomationProperties.SetName(shinyBox, l.FilterShinyOnly);
        Grid.SetColumn(shinyBox, 2);
        row.Children.Add(shinyBox);

        var customSort = showingLog ? st.LogSort != CatchLogSortOption.RecentFirst : st.DexSort != DexSortOption.NumberAsc;
        // The borderless menu draws its label in the primary color (the reference shows no secondary tint).
        var tint = customSort ? Theme.Keys.Accent : Theme.Keys.Primary;
        var sortLabel = Ui.HStack(3, Ui.Icon(DexUi.Glyph.Sort, 15, tint), Ui.Icon(DexUi.Glyph.ChevronDown, 8, tint));
        // A borderless menu button: label only; the accent tint marks a non-default order.
        var sortBox = DexUi.Rounded(sortLabel, customSort ? DexTheme.Tint(Theme.Keys.Accent, 0.12) : null, 6, new Thickness(6, 4, 4, 4),
            customSort ? DexTheme.Tint(Theme.Keys.Accent, 0.5) : null, customSort ? 0.5 : 0);
        sortBox.OnClick(() =>
        {
            var menu = showingLog
                ? DexUi.Menu(Enum.GetValues<CatchLogSortOption>().Select(o => (l.Label(o), st.LogSort == o, (Action)(() =>
                {
                    st.LogSort = o;
                    refreshAll();
                }))))
                : DexUi.Menu(Enum.GetValues<DexSortOption>().Select(o => (l.Label(o), st.DexSort == o, (Action)(() =>
                {
                    st.DexSort = o;
                    st.Grid?.ResetPage();
                    refreshAll();
                }))));
            DexUi.ShowMenu(sortBox, menu);
        }).Help(l.SortTitle);
        System.Windows.Automation.AutomationProperties.SetName(sortBox, l.SortTitle);
        Grid.SetColumn(sortBox, 4);
        row.Children.Add(sortBox);
        return row;
    }

    private static FrameworkElement SearchField(CollectionState st, L l, Action refreshList)
    {
        var tb = st.Search;
        if (tb == null)
        {
            tb = new TextBox
            {
                Text = st.SearchText, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
                FontSize = Theme.FontSize.Body, FontFamily = Theme.UiFont, VerticalContentAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null,
            };
            tb.SetResourceReference(Control.ForegroundProperty, Theme.Keys.Primary);
            tb.SetResourceReference(TextBoxBase.CaretBrushProperty, Theme.Keys.Primary);
            tb.SetResourceReference(TextBoxBase.SelectionBrushProperty, Theme.Keys.Accent);
            st.Search = tb;
        }
        // Rebuilt trees reattach the same box.
        switch (tb.Parent)
        {
            case Panel p: p.Children.Remove(tb); break;
            case Decorator d: d.Child = null; break;
        }

        var placeholder = Ui.Text(l.DexSearchPlaceholder, Theme.FontSize.Body, DexTheme.Tint(Theme.Keys.Primary, 0.9));
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(2, 0, 0, 0);
        var clear = DexUi.Icon(DexUi.Glyph.ClearFilled, 13, Theme.Keys.Secondary);
        clear.Margin = new Thickness(4, 0, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(clear, l.ClearFilters);
        void Sync()
        {
            placeholder.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            clear.Visibility = tb.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        Sync();
        clear.OnClick(() => tb.Text = "");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Ui.Icon(DexUi.Glyph.Search, 13, Theme.Keys.Secondary);
        icon.Margin = new Thickness(0, 0, 4, 0);
        grid.Children.Add(icon);
        var field = new Grid { Children = { placeholder, tb } };
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        Grid.SetColumn(clear, 2);
        grid.Children.Add(clear);

        // The handler belongs to this build only: an older tree must not refresh a list that is gone.
        void Changed(object? sender, TextChangedEventArgs e)
        {
            if (tb.Text == st.SearchText) { Sync(); return; }
            st.SearchText = tb.Text;
            st.Grid?.ResetPage();
            Sync();
            refreshList();
        }
        tb.TextChanged += Changed;
        grid.Unloaded += (_, _) => tb.TextChanged -= Changed;
        grid.Loaded += (_, _) => { tb.TextChanged -= Changed; tb.TextChanged += Changed; };

        var box = DexUi.Rounded(grid, DexTheme.ControlBackground, 6, new Thickness(6, 4, 6, 4), DexTheme.Tint(Theme.Keys.Secondary, 0.2), 0.5);
        box.Cursor = Cursors.IBeam;
        box.MouseLeftButtonDown += (_, _) => tb.Focus();
        return box;
    }

    // MARK: Shared pieces

    /// <summary>Port of <c>RarityTally</c>: a rarity filter capsule (count 0 → faded and disabled).</summary>
    private static FrameworkElement RarityTally(string label, int count, string colorKey, bool isSelected)
    {
        var dot = new Ellipse { Width = 6, Height = 6 };
        dot.SetResourceReference(Shape.FillProperty, colorKey);
        var row = Ui.HStack(3, dot,
            Ui.Text(label, Theme.FontSize.Subheadline, Theme.Keys.Primary, isSelected ? FontWeights.SemiBold : FontWeights.Medium),
            Ui.Text($"{count}", Theme.FontSize.Subheadline, Theme.Keys.Primary, FontWeights.Bold));
        if (isSelected) row.Children.Add(DexUi.Icon(DexUi.Glyph.Check, 9, colorKey, bold: true));
        var capsule = DexUi.Capsule(row, DexTheme.Tint(colorKey, isSelected ? 0.22 : 0.12), new Thickness(6, 2, 6, 2),
            isSelected ? colorKey : DexTheme.Tint(colorKey, 0.35), isSelected ? 1.5 : 0.5);
        if (count == 0) capsule.Opacity = 0.4;
        return capsule;
    }

    /// <summary>
    /// The title row + rarity filter capsules shared by the Pokédex (species counts) and the catch log
    /// (<c>DexSummaryHeader</c>, individual counts). The Pokédex adds its completion medals at the end of the title row
    /// and its generation capsules above the rarity ones.
    /// </summary>
    internal static FrameworkElement RarityHeader(string title, string total, Func<Rarity, int> count, Rarity? selected,
        L l, Action<Rarity> onSelect, FrameworkElement? medals = null, FrameworkElement? regionChips = null)
    {
        var titleRow = Ui.HStack(6, Ui.Text(title, Theme.FontSize.Callout, Theme.Keys.Primary, FontWeights.SemiBold),
            Ui.Text(total, Theme.FontSize.Caption, Theme.Keys.Secondary));
        if (medals != null)
        {
            titleRow.Children.Add(Ui.Spacer(4));
            titleRow.Children.Add(medals);
        }
        else titleRow.HorizontalAlignment = HorizontalAlignment.Left;
        var tallies = Ui.HStack(4);
        foreach (var r in DexUi.RarityDisplayOrder)
        {
            var n = count(r);
            var tally = RarityTally(l.RarityLabel(r), n, PokemonTypeColors.RarityKey(r), selected == r).Help(l.DexFilterHint);
            tallies.Children.Add(tally.Plain(() => onSelect(r), enabled: n > 0));
        }
        return Ui.VStack(5, titleRow, regionChips, CapsuleStrip(tallies));
    }

    /// <summary>A capsule row that scrolls sideways when it outgrows the popover (SwiftUI horizontal ScrollView). macOS
    /// trackpads swipe it sideways; here the wheel does, since a WPF ScrollViewer with no vertical axis swallows the
    /// wheel without moving and the last capsules (Unova, Common) would be out of reach.</summary>
    internal static ScrollViewer CapsuleStrip(FrameworkElement capsules)
    {
        var strip = new ScrollViewer
        {
            Content = capsules,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        strip.PreviewMouseWheel += (_, e) =>
        {
            if (strip.ScrollableWidth <= 0) return;
            strip.ScrollToHorizontalOffset(strip.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };
        return strip;
    }

    /// <summary>
    /// Port of <c>RepresentativeFooterButton</c>: icon only, so long en/es titles never push the selected
    /// species' name and rarity aside; the title stays in the tooltip and accessibility label.
    /// </summary>
    internal static FrameworkElement RepresentativeButton(L l, bool isRepresentative, Action action)
    {
        var title = isRepresentative ? l.RepresentativeFollowCurrent : l.RepresentativeSet;
        var icon = DexUi.Icon(isRepresentative ? DexUi.Glyph.Sync : DexUi.Glyph.Star, 9, Theme.Keys.Primary);
        var b = DexUi.Rounded(icon, Theme.Keys.Fill, 4, new Thickness(8, 2, 8, 2));
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(b, title);
        return b.OnClick(action).Help(title);
    }

    internal static FrameworkElement EmptySearchResults(L l, Action onReset)
    {
        var icon = Ui.Icon(DexUi.Glyph.Search, 24, Theme.Keys.Tertiary);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var text = Ui.Text(l.NoSearchResults, Theme.FontSize.Callout, Theme.Keys.Secondary, FontWeights.Medium);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        var button = Buttons.Small(l.ClearFilters, onReset);
        button.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = Ui.VStack(8, icon, text, button);
        stack.VerticalAlignment = VerticalAlignment.Center;
        foreach (FrameworkElement c in stack.Children) c.HorizontalAlignment = HorizontalAlignment.Center;
        return new Grid { Children = { stack } };
    }

    /// <summary>Empty Pokédex: the guide mascot (Pikachu) and a nudge to catch something.</summary>
    private static FrameworkElement EmptyState(L l)
    {
        var sprite = new SpriteView(25, 96, animated: true) { HorizontalAlignment = HorizontalAlignment.Center };
        var title = Ui.Text(l.DexEmptyTitle, Theme.FontSize.Callout, Theme.Keys.Primary, FontWeights.SemiBold);
        var hint = Ui.Text(l.DexEmptyHint, Theme.FontSize.Caption, Theme.Keys.Secondary).Wrapping();
        hint.TextAlignment = TextAlignment.Center;
        var stack = Ui.VStack(10, sprite, title, hint);
        foreach (FrameworkElement c in stack.Children) c.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Margin = new Thickness(0, 28, 0, 28);
        return stack;
    }

    // MARK: Catch log

    /// <summary>The catch log: per-individual records. The summary header stays put; only the list scrolls.</summary>
    private static FrameworkElement CatchLog(AppServices s, CollectionState st, Action refresh, bool appearing)
    {
        var store = s.Companion;
        var l = store.L;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(RarityHeader(l.CatchLogTitle, l.DexTotal(store.DexEntries.Count), store.DexCount, st.SelectedRarity, l, r =>
        {
            st.SelectedRarity = st.SelectedRarity == r ? null : r;
            refresh();
        }));
        var entries = store.FilteredDexEntries(st.SearchText, st.SelectedRarity, st.ShinyOnly, st.LogSort);
        FrameworkElement body;
        if (entries.Count == 0)
            body = EmptySearchResults(l, () => { st.ResetFilters(); st.Grid?.ResetPage(); refresh(); });
        else
        {
            var dexLinks = store.DexLinkTargets;   // once for all rows, not per row
            var list = new DexLazyList(entries.Cast<object>().ToList(), item =>
            {
                var entry = (DexEntry)item;
                return DexEntryRow.Build(store, entry, dexLinks, target =>
                {
                    // Keeps the segment, so Back from the detail page returns to the log.
                    st.LogReturnAnchor = entry.Id;
                    s.Nav.DexDetailCollectionID = target;
                });
            }, spacing: 8);
            if (st.LogReturnAnchor is { } anchor)
            {
                st.LogReturnAnchor = null;
                var index = entries.FindIndex(e => e.Id == anchor);
                if (index >= 0) list.CenterOnLoad(index);
            }
            else if (appearing) list.TopOnLoad();
            body = list;
        }
        body.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }
}

/// <summary>
/// A vertical list that realizes only the rows near the viewport (SwiftUI <c>LazyVStack</c>): a long catch log
/// must not build and load sprites for every individual. Pixel scrolling, the overlay scroller and the scroller
/// lane match the other Collection scroll views.
/// </summary>
internal sealed class DexLazyList : ItemsControl
{
    private readonly Func<object, UIElement> _build;
    private readonly double _spacing;

    public DexLazyList(IList<object> items, Func<object, UIElement> build, double spacing)
    {
        _build = build;
        _spacing = spacing;
        ItemsSource = items;
        Focusable = false;
        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        ItemsPanel = new ItemsPanelTemplate(panel);
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Standard);
        ScrollViewer.SetCanContentScroll(this, true);
        var viewer = new FrameworkElementFactory(typeof(ScrollViewer));
        viewer.SetValue(ScrollViewer.TemplateProperty, DexUi.OverlayScroller().Template);
        viewer.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        viewer.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        viewer.SetValue(ScrollViewer.CanContentScrollProperty, true);
        viewer.SetValue(FocusableProperty, false);
        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        presenter.SetValue(MarginProperty, new Thickness(0, 0, Theme.ScrollerInset, 0));
        viewer.AppendChild(presenter);
        Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = viewer };
    }

    protected override DependencyObject GetContainerForItemOverride() => new ContentPresenter();

    protected override bool IsItemItsOwnContainerOverride(object item) => false;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        var cp = (ContentPresenter)element;
        var index = ItemContainerGenerator.IndexFromContainer(cp);
        cp.Margin = new Thickness(0, index > 0 ? _spacing : 0, 0, 0);
        cp.Content = _build(item);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item) =>
        ((ContentPresenter)element).Content = null;

    public ScrollViewer? Viewer => VisualTreeHelper.GetChildrenCount(this) > 0 ? VisualTreeHelper.GetChild(this, 0) as ScrollViewer : null;

    public void TopOnLoad() => Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Viewer?.ScrollToVerticalOffset(0));

    /// <summary>Scrolls a row back to the middle. The first pass places it with estimated heights of rows not yet
    /// realized; the rows it reveals are measured afterwards and push it, so it is centred again once laid out.</summary>
    public void CenterOnLoad(int index)
    {
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Center(index);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Center(index));
        });
    }

    private void Center(int index)
    {
        if (Viewer is not { } sv) return;
        var panel = FindPanel(this);
        panel?.BringIndexIntoViewPublic(index);
        UpdateLayout();
        if (ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement row) return;
        var top = row.TranslatePoint(new Point(0, 0), sv).Y;
        sv.ScrollToVerticalOffset(sv.VerticalOffset + top - (sv.ViewportHeight - row.ActualHeight) / 2);
    }

    private static VirtualizingStackPanel? FindPanel(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is VirtualizingStackPanel p) return p;
            if (FindPanel(child) is { } found) return found;
        }
        return null;
    }
}

/// <summary>
/// Port of <c>DexEntryRow</c>: one catch-log record — rarity/status badges and nature, the evolution line with
/// each species' name, and when it was caught. Stored names show at once; older saves without them resolve
/// asynchronously and fill in.
/// </summary>
internal static class DexEntryRow
{
    /// <summary>Card inset; shared with the width the evolution line may use.</summary>
    private const double CardPadding = 8;

    public static FrameworkElement Build(CompanionStore store, DexEntry entry, IReadOnlyDictionary<int, string> dexLinks,
        Action<string> onOpenDexEntry)
    {
        var host = new ContentControl { Focusable = false };
        var stored = store.DexStoredChainNames(entry);
        host.Content = Card(store, entry, dexLinks, onOpenDexEntry, stored);
        var language = store.Language;
        if (stored != null && !entry.NeedsNamesRefresh) return host;
        // `.task(id: entry.id + language)`: legacy entries refresh their names; nameless ones fill in.
        host.Loaded += async (_, _) =>
        {
            var resolved = await store.DexResolveChainNames(entry);
            if (stored == null && resolved.Count > 0 && store.Language == language)
                host.Content = Card(store, entry, dexLinks, onOpenDexEntry, resolved);
        };
        return host;
    }

    private static FrameworkElement Card(CompanionStore store, DexEntry entry, IReadOnlyDictionary<int, string> dexLinks,
        Action<string> onOpenDexEntry, IReadOnlyDictionary<int, string>? names)
    {
        var l = store.L;
        var badges = Ui.HStack(8, Badge.Make(l.RarityLabel(entry.Rarity).ToUpperInvariant(),
            Theme.Brush(PokemonTypeColors.RarityKey(entry.Rarity)), size: 10));
        if (store.IsActiveDexEntry(entry))
            badges.Children.Add(Badge.Make(l.DexRaising.ToUpperInvariant(), Theme.Brush(Theme.Keys.Accent), solid: false, size: 10));
        else if (entry.IsReleased)
            // Released: the species stays in the Pokédex, but this one was not raised to the end. Neutral, not a
            // warning color — a different kind of record, not a failure.
            badges.Children.Add(Badge.Make(l.DexReleased.ToUpperInvariant(), Theme.Brush(Theme.Keys.Secondary), solid: false, size: 10));
        if (entry.IsShiny)
        {
            var sparkle = DexUi.Sparkles(Theme.FontSize.Caption2);
            System.Windows.Automation.AutomationProperties.SetName(sparkle, l.DexShinyLabel);
            badges.Children.Add(sparkle);
        }
        var header = new Grid { Children = { badges } };
        badges.HorizontalAlignment = HorizontalAlignment.Left;
        if (entry.Nature is { } nature)
        {
            var natureText = Ui.Text(nature.Name(store.Language), Theme.FontSize.Subheadline, Theme.Keys.Secondary);
            natureText.HorizontalAlignment = HorizontalAlignment.Right;
            header.Children.Add(natureText);
        }

        var line = new DexEvoLine
        {
            Nodes = entry.ChainOrder.Select(id => new EvoLineItem(EvoLineItemContent.Species(id), EvoLineItemState.Done)).ToList(),
            MysteryLabel = l.UnknownNextEvolution,
            Language = store.Language,
            Thumb = 68,
            Shiny = entry.IsShiny,
            Names = names,
            MaxWidth = Theme.ScrollContentWidth - CardPadding * 2,
            Form = entry.Form,
            NameFontSize = Theme.FontSize.Subheadline,
            NameLineLimit = 2,
            DexLinks = dexLinks,
            OnOpenDexEntry = onOpenDexEntry,
        }.Build();
        line.HorizontalAlignment = HorizontalAlignment.Left;

        var stack = Ui.VStack(3, header, line);
        if (entry.CaughtAt is { } caughtAt)
        {
            var when = Ui.Text(DexRelativeTime.Format(caughtAt, DateTimeOffset.Now, store.Language), Theme.FontSize.Subheadline, Theme.Keys.Secondary);
            when.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(when);
        }
        return DexUi.Rounded(stack, DexTheme.Tint(Theme.Keys.Secondary, 0.06), 10, new Thickness(CardPadding));
    }
}
