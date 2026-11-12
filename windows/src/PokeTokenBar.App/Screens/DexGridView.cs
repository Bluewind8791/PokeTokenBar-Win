using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>DexScrollPager</c>: turns mouse-wheel / touchpad input into single page steps for the Pokédex
/// grid. A wheel notch (a multiple of <see cref="NotchDelta"/>) is one page. Precision touchpads send small
/// deltas with no gesture phases on Windows, so they accumulate to a threshold and then latch until the input
/// pauses — the stand-in for macOS's "one page per gesture, ignore the momentum tail".
/// </summary>
internal sealed class DexScrollPager
{
    public const int NotchDelta = 120;
    /// <summary>Accumulated touchpad distance before a gesture flips the page (half a notch ≈ 24 pt).</summary>
    public const int PreciseThreshold = 60;
    /// <summary>A pause this long ends a touchpad gesture.</summary>
    public const int GestureGapMs = 220;

    private int _accumulated;
    private bool _firedThisGesture;
    private int? _lastTimestamp;

    /// <summary>+1 = next page, -1 = previous page, 0 = no change. Positive Windows deltas scroll up.</summary>
    public int Step(int delta, int timestampMs)
    {
        if (delta % NotchDelta == 0) return Direction(delta);
        if (_lastTimestamp is not { } last || unchecked(timestampMs - last) > GestureGapMs)
        {
            _accumulated = 0;
            _firedThisGesture = false;
        }
        _lastTimestamp = timestampMs;
        if (_firedThisGesture) return 0;
        _accumulated += delta;
        if (Math.Abs(_accumulated) < PreciseThreshold) return 0;
        var result = Direction(_accumulated);
        _accumulated = 0;
        _firedThisGesture = true;
        return result;
    }

    public static int PageAfterScroll(int current, int pageCount, int step) =>
        Math.Min(Math.Max(0, current + step), Math.Max(0, pageCount - 1));

    /// <summary>The page that shows <paramref name="id"/> — where Back from a detail page lands. 0 when
    /// <paramref name="id"/> is null or filtered out, the page a fresh grid opens on.</summary>
    public static int Page(string? id, IReadOnlyList<string> ids, int pageSize)
    {
        if (id == null) return 0;
        var index = ids.ToList().IndexOf(id);
        return index < 0 ? 0 : index / pageSize;
    }

    private static int Direction(int delta) => delta > 0 ? -1 : delta < 0 ? 1 : 0;
}

/// <summary>The grid's own SwiftUI <c>@State</c>: lives while the grid is on screen, recreated when it reappears.</summary>
internal sealed class DexGridState(string? returnAnchor)
{
    /// <summary>Null = the page that shows <see cref="ReturnAnchor"/>. Paging or filtering sets it.</summary>
    public int? Page;
    /// <summary>The species whose detail page was just closed. Only picks the page; nothing is selected.</summary>
    public readonly string? ReturnAnchor = returnAnchor;
    /// <summary>The clicked cell — the footer shows its rarity.</summary>
    public string? SelectedID;
    public readonly DexScrollPager Pager = new();
    /// <summary>The grid's <c>.task</c>: refresh legacy names once per appearance.</summary>
    public bool NamesBackfillStarted;

    public void ResetPage()
    {
        Page = 0;
        SelectedID = null;
    }
}

/// <summary>
/// Port of <c>DexGridView</c>: owned species only, 16 per page (4 columns × 4 rows) in a fixed grid that fills
/// the Collection height; the wheel flips pages; the footer pages and shows the selected cell's rarity.
/// </summary>
internal static class DexGridView
{
    private const int Columns = 4;
    private const int Rows = 4;
    private const int PageSize = Columns * Rows;
    private const double Spacing = 4;

    public sealed record Context(
        AppServices Services,
        CollectionState Filters,
        DexGridState Grid,
        Action<CompanionStore.DexSpecies> OnSelectSpecies,
        Action OnResetFilters,
        Action<Rarity> OnToggleRarity,
        Action<PokedexRegion> OnToggleRegion,
        Action Refresh);

    public static FrameworkElement Build(Context c)
    {
        var store = c.Services.Companion;
        var l = store.L;
        var f = c.Filters;
        var all = store.DexSpeciesList;
        var visible = store.FilteredDexSpecies(f.SearchText, f.SelectedRarity, f.ShinyOnly, f.SelectedRegion, f.DexSort);
        var pageCount = Math.Max(1, (visible.Count + PageSize - 1) / PageSize);
        var current = Math.Min(c.Grid.Page ?? DexScrollPager.Page(c.Grid.ReturnAnchor, visible.Select(v => v.CollectionID).ToList(), PageSize),
            pageCount - 1);
        var slice = visible.Skip(current * PageSize).Take(PageSize).ToList();

        // Legacy saves without names show "#41" until the line lookup refreshes them.
        if (!c.Grid.NamesBackfillStarted)
        {
            c.Grid.NamesBackfillStarted = true;
            _ = store.BackfillMissingDexNames();
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = CollectionView.RarityHeader(l.DexTitle, l.DexSpeciesTotal(all.Count),
            r => all.Count(s => s.Rarity == r), f.SelectedRarity, l, c.OnToggleRarity,
            DexBadgeMedal.Strip(store.EarnedDexBadges, l), RegionChips(c));
        root.Children.Add(header);
        if (visible.Count == 0)
        {
            var empty = CollectionView.EmptySearchResults(l, c.OnResetFilters);
            empty.Margin = new Thickness(0, 6, 0, 0);
            Grid.SetRow(empty, 1);
            root.Children.Add(empty);
            return root;
        }

        var grid = Cells(c, slice);
        grid.Margin = new Thickness(0, 6, 0, 6);
        Grid.SetRow(grid, 1);
        root.Children.Add(grid);
        grid.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            var step = c.Grid.Pager.Step(e.Delta, e.Timestamp);
            if (step == 0) return;
            var next = DexScrollPager.PageAfterScroll(current, pageCount, step);
            if (next == current) return;
            c.Grid.Page = next;
            c.Grid.SelectedID = null;
            c.Refresh();
        };
        var footer = Footer(c, slice, current, pageCount);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    /// <summary>One progress capsule per generation; clicking filters the grid to it (again: back to all). A generation
    /// with nothing owned yet is disabled.</summary>
    private static FrameworkElement RegionChips(Context c)
    {
        var l = c.Services.Companion.L;
        var chips = Ui.HStack(4);
        foreach (var completion in c.Services.Companion.DexCompletions)
        {
            var region = completion.Region;
            var label = l.DexRegionProgressLabel(region, completion.Owned, completion.Total);
            var selected = c.Filters.SelectedRegion == region;
            var chip = RegionTally.Build(l.RegionName(region), completion, selected).Help(label);
            System.Windows.Automation.AutomationProperties.SetName(chip, label);
            // Every filter change rebuilds the strip at offset 0; SwiftUI keeps the scroll position, so the selected
            // capsule (Unova sits past the edge) is scrolled back into view instead.
            if (selected) chip.Loaded += (_, _) => chip.BringIntoView();
            chips.Children.Add(chip.Plain(() => c.OnToggleRegion(region), enabled: RegionTally.IsEnabled(completion)));
        }
        return CollectionView.CapsuleStrip(chips);
    }

    private static Grid Cells(Context c, IReadOnlyList<CompanionStore.DexSpecies> slice)
    {
        var store = c.Services.Companion;
        var items = new List<UIElement?>();
        foreach (var sp in slice)
        {
            int? formCount = PokemonForm.HasForms(sp.Id) ? store.State.CollectedForms(sp.Id).Count : null;
            var cell = DexSpeciesCell.Build(store, sp, c.Grid.SelectedID == sp.CollectionID, store.IsRepresentative(sp), formCount,
                onTap: () =>
                {
                    c.Grid.SelectedID = sp.CollectionID;
                    c.OnSelectSpecies(sp);
                });
            cell.VerticalAlignment = VerticalAlignment.Center;
            items.Add(cell);
        }
        var grid = DexUi.FlexGrid(items, Columns, Spacing, Spacing, Rows, fillRows: true);
        grid.Background = Brushes.Transparent;   // the wheel pages over the gaps too
        return grid;
    }

    private static FrameworkElement Footer(Context c, IReadOnlyList<CompanionStore.DexSpecies> slice, int current, int pageCount)
    {
        var store = c.Services.Companion;
        var l = store.L;
        var bar = new Grid { Height = 18, ClipToBounds = false };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (slice.FirstOrDefault(s => s.CollectionID == c.Grid.SelectedID) is { } sel)
        {
            // Cells show number, sprite and name only, so the rarity is what selecting adds.
            var left = Ui.HStack(8, Ui.Text($"#{sel.Id} {sel.Name} · {l.RarityLabel(sel.Rarity)}", Theme.FontSize.Subheadline, Theme.Keys.Secondary));
            if (!PokemonForm.HasForms(sel.Id))
            {
                var isRep = store.IsRepresentative(sel);
                left.Children.Add(CollectionView.RepresentativeButton(l, isRep,
                    () => store.SetRepresentativeSpeciesID(isRep ? null : sel.Id)));
            }
            left.HorizontalAlignment = HorizontalAlignment.Left;
            bar.Children.Add(left);
        }
        if (pageCount > 1)
        {
            var pager = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            pager.Children.Add(PageButton(false, l.DexPagePrev, current > 0, () =>
            {
                c.Grid.Page = Math.Max(0, current - 1);
                c.Grid.SelectedID = null;
                c.Refresh();
            }));
            var label = Ui.Text($"{current + 1} / {pageCount}", Theme.FontSize.Callout, Theme.Keys.Secondary, FontWeights.SemiBold, monospacedDigits: true);
            System.Windows.Automation.AutomationProperties.SetName(label, l.DexPageLabel(current + 1, pageCount));
            pager.Children.Add(label);
            pager.Children.Add(PageButton(true, l.DexPageNext, current < pageCount - 1, () =>
            {
                c.Grid.Page = Math.Min(pageCount - 1, current + 1);
                c.Grid.SelectedID = null;
                c.Refresh();
            }));
            // The widened hit areas' insets are the chevron↔number gap. Negative margins keep the glyphs at the
            // trailing edge and let the extra hit area reach into the popover padding without growing the 18 pt row.
            pager.Margin = new Thickness(-DexPageButtonEdgeInset, -(HitSize - 18) / 2, -DexPageButtonEdgeInset, -(HitSize - 18) / 2);
            Grid.SetColumn(pager, 1);
            bar.Children.Add(pager);
        }
        return bar;
    }

    /// <summary>Port of <c>DexPageButton</c>: a 13 pt chevron that was hard to hit (#379), so the click area is a
    /// <see cref="HitSize"/> square around the unchanged glyph.</summary>
    public const double HitSize = 28;
    public const double DexPageButtonEdgeInset = 10;

    private static FrameworkElement PageButton(bool forward, string label, bool enabled, Action action)
    {
        var glyph = DexUi.Icon(forward ? DexUi.Glyph.ChevronRight : DexUi.Glyph.ChevronLeft, 13,
            enabled ? Theme.Keys.Primary : DexTheme.Tint(Theme.Keys.Primary, 0.5), bold: true);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var hit = new Border { Child = glyph, Width = HitSize, Height = HitSize, Background = Brushes.Transparent };
        System.Windows.Automation.AutomationProperties.SetName(hit, label);
        if (enabled) hit.OnClick(action);
        return hit;
    }
}

/// <summary>
/// Port of <c>DexSpeciesCell</c>: number + sprite + name, species information only (nature and catch counts belong
/// to the log). Static sprites only — sixteen animated GIFs at once would cost too much CPU.
/// </summary>
internal static class DexSpeciesCell
{
    private const double Thumb = 56;

    public static FrameworkElement Build(CompanionStore store, CompanionStore.DexSpecies species, bool isSelected,
        bool isRepresentative, int? formCount, Action onTap)
    {
        var l = store.L;
        var formTotal = PokemonForm.Forms(species.Id).Count;

        // A shiny catch shows its shiny colors here; species with forms keep the default normal icon
        // (per-form colors are on the detail page).
        var sprite = new SpriteView(species.Id, Thumb, shiny: formCount == null && species.IsShiny);
        var spriteBox = new Grid { Width = Thumb, Height = Thumb, HorizontalAlignment = HorizontalAlignment.Center, Children = { sprite } };
        if (species.IsRaising)
        {
            // Over the sprite, leaving room for the name; material keeps the translucent tint readable.
            var badge = Badge.Make(l.DexRaising.ToUpperInvariant(), Theme.Brush(Theme.Keys.Accent), solid: false, size: 10);
            var plate = DexUi.Capsule(badge, DexTheme.Material, new Thickness(0));
            plate.HorizontalAlignment = HorizontalAlignment.Center;
            plate.VerticalAlignment = VerticalAlignment.Bottom;
            spriteBox.Children.Add(plate);
            spriteBox.ClipToBounds = false;
        }
        var name = DexUi.Lines(Ui.Text(formCount is { } fc ? $"{species.Name} {fc}/{formTotal}" : species.Name, Theme.FontSize.Callout),
            lines: 2, reserve: true);
        name.TextAlignment = TextAlignment.Center;
        name.HorizontalAlignment = HorizontalAlignment.Stretch;
        name.Margin = new Thickness(0, 1, 0, 0);
        var column = new StackPanel { Orientation = Orientation.Vertical, Children = { spriteBox, name } };

        var inner = new Grid { Children = { column } };
        // Number and shiny markers attach to the full cell width.
        var number = NumberTag(species, isRepresentative);
        number.HorizontalAlignment = HorizontalAlignment.Left;
        number.VerticalAlignment = VerticalAlignment.Top;
        inner.Children.Add(number);
        if (species.IsShiny)
        {
            // ✨ = a shiny of this species was caught.
            var shiny = DexUi.Capsule(DexUi.Sparkles(Theme.FontSize.Caption2), DexTheme.Material, new Thickness(2, 0, 2, 0));
            shiny.HorizontalAlignment = HorizontalAlignment.Right;
            shiny.VerticalAlignment = VerticalAlignment.Top;
            System.Windows.Automation.AutomationProperties.SetName(shiny, l.DexShinyLabel);
            inner.Children.Add(shiny);
        }

        // Representative = persistent accent fill; just-clicked = accent ring. Two different cards never look like
        // two selections.
        var card = DexUi.Rounded(inner,
            isRepresentative ? DexTheme.Tint(Theme.Keys.Accent, 0.16) : DexTheme.Tint(Theme.Keys.Secondary, isSelected ? 0.16 : 0.06),
            8, new Thickness(2));
        var root = new Grid();
        // Hover = clickable feedback: an opaque card with a shadow behind, and a scale that stays inside the gap.
        // The shadow is on the card shape only, not on every glyph.
        var hoverPlate = new Border
        {
            CornerRadius = new CornerRadius(8), Visibility = Visibility.Hidden,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.25, BlurRadius = 8, ShadowDepth = 2, Direction = 270 },
        };
        hoverPlate.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Background);
        root.Children.Add(hoverPlate);
        root.Children.Add(card);
        if (isSelected) root.Children.Add(DexUi.Rounded(null, null, 8, new Thickness(0), Theme.Keys.Accent, 1.5));

        var scale = new ScaleTransform(1, 1);
        root.RenderTransform = scale;
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        void Hover(bool on)
        {
            hoverPlate.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            var anim = new DoubleAnimation(on ? 1.04 : 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            Panel.SetZIndex(root, on ? 1 : 0);
        }
        root.MouseEnter += (_, _) => Hover(true);
        root.MouseLeave += (_, _) => Hover(false);

        var tip = Tooltip(l, species, formCount, formTotal, isRepresentative);
        System.Windows.Automation.AutomationProperties.SetName(root, tip);
        root.Help(tip);
        root.ContextMenu = formCount != null
            ? DexUi.Menu([(l.ChooseForm, false, onTap)])
            : DexUi.Menu([(isRepresentative ? l.RepresentativeFollowCurrent : l.RepresentativeSet, false,
                () => store.SetRepresentativeSpeciesID(isRepresentative ? null : species.Id))]);
        return root.OnClick(onTap);
    }

    /// <summary>Number colored by rarity (every cell shows its rarity, even unfiltered) on a material plate;
    /// the representative star joins it so it never competes with the shiny / raising / name slots.</summary>
    private static FrameworkElement NumberTag(CompanionStore.DexSpecies species, bool isRepresentative)
    {
        var row = Ui.HStack(2, Ui.Text($"#{species.Id}", Theme.FontSize.Caption2, PokemonTypeColors.RarityKey(species.Rarity), FontWeights.SemiBold));
        if (isRepresentative) row.Children.Add(DexUi.Icon(DexUi.Glyph.StarFill, 8, Theme.Keys.Accent, bold: true));
        return DexUi.Capsule(row, DexTheme.Material, new Thickness(2, 0, 2, 0));
    }

    /// <summary>Tooltip and accessibility label share one sentence carrying what the cell cannot show in text.</summary>
    private static string Tooltip(L l, CompanionStore.DexSpecies species, int? formCount, int formTotal, bool isRepresentative)
    {
        var parts = new List<string> { $"#{species.Id} {species.Name}", l.RarityLabel(species.Rarity) };
        if (formCount is { } fc) parts.Add(l.FormsCollected(fc, formTotal));
        if (species.IsShiny) parts.Add(l.DexShinyLabel);
        if (species.IsRaising) parts.Add(l.DexRaising);
        if (isRepresentative) parts.Add(l.RepresentativeBadge);
        return string.Join(" · ", parts);
    }
}
