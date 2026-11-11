using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>DexEntryLink</c> (CompanionView.swift): makes a sprite open its Pokédex detail page. With no
/// target (egg, uncaught species, <c>?</c> node) the content is returned untouched — no hover highlight or
/// tooltip — so only real links look clickable. The highlight is a background, so layout never changes.
/// </summary>
internal static class DexSpriteLink
{
    public static FrameworkElement Wrap(FrameworkElement content, string? target, string hint, string? accessibilityName,
        double cornerRadius, Action<string>? open)
    {
        if (target == null || open == null) return content;
        var box = new Border { Child = content, CornerRadius = new CornerRadius(cornerRadius), Background = Brushes.Transparent };
        box.MouseEnter += (_, _) => box.SetResourceReference(Border.BackgroundProperty, DexTheme.Tint(Theme.Keys.Primary, 0.1));
        box.MouseLeave += (_, _) => box.Background = Brushes.Transparent;
        System.Windows.Automation.AutomationProperties.SetName(box, accessibilityName is { } n ? $"{n}, {hint}" : hint);
        return box.OnClick(() => open(target)).Help(hint);
    }
}

/// <summary>
/// Port of <c>EvoLineView</c> (CompanionView.swift): an evolution line first → final, with done/current/future
/// nodes. A branched line can be wider than the popover; past <c>maxWidth</c> it scrolls horizontally inside
/// that width (thumbnails keep their size) with edge fades and chevrons signalling the overflow — otherwise the
/// overflowing row would widen the whole popover.
/// </summary>
internal sealed class DexEvoLine
{
    public required IReadOnlyList<EvoLineItem> Nodes { get; init; }
    public required string MysteryLabel { get; init; }
    public AppLanguage Language { get; init; } = AppLanguage.En;
    public double Thumb { get; init; } = 40;
    public bool Shiny { get; init; }
    /// <summary>When given, a small name label sits under each sprite.</summary>
    public IReadOnlyDictionary<int, string>? Names { get; init; }
    public double MaxWidth { get; init; } = double.PositiveInfinity;
    public PokemonForm? Form { get; init; }
    public double NameFontSize { get; init; } = 8;
    public int NameLineLimit { get; init; } = 1;
    /// <summary>Species number → dex <c>CollectionID</c>; with <see cref="OnOpenDexEntry"/> sprites open their page.</summary>
    public IReadOnlyDictionary<int, string> DexLinks { get; init; } = new Dictionary<int, string>();
    public Action<string>? OnOpenDexEntry { get; init; }

    private const double Spacing = 2;
    /// <summary>Arrow column = thumb × this; a fixed frame so the row width never depends on glyph metrics.</summary>
    private const double ArrowRatio = 0.25;
    /// <summary>How much wider than the thumbnail a name label may be.</summary>
    private const double NameSlack = 6;
    private const double FadeWidth = 24;

    public static double RowWidth(int count, double thumb, bool hasNames)
    {
        if (count <= 0) return 0;
        var column = thumb + (hasNames ? NameSlack : 0);
        return count * column + (count - 1) * thumb * ArrowRatio + (2 * count - 2) * Spacing;
    }

    public static bool NeedsScroll(int count, double thumb, bool hasNames, double maxWidth) =>
        double.IsFinite(maxWidth) && maxWidth > 0 && RowWidth(count, thumb, hasNames) > maxWidth;

    /// <summary>Column index a chevron press scrolls to: one screenful from the current leading column.</summary>
    public static int PageTarget(bool forward, double scrollX, int count, double thumb, bool hasNames, double maxWidth)
    {
        if (count <= 0) return 0;
        var stride = Stride(thumb, hasNames);
        var visible = Math.Max(1, (int)(maxWidth / stride));
        var first = Math.Max(0, (int)Math.Round(scrollX / stride));
        return Math.Min(Math.Max(0, first + (forward ? visible : -visible)), count - 1);
    }

    private static double Stride(double thumb, bool hasNames) => thumb + (hasNames ? NameSlack : 0) + thumb * ArrowRatio + Spacing * 2;

    public FrameworkElement Build() =>
        NeedsScroll(Nodes.Count, Thumb, Names != null, MaxWidth) ? ScrollableRow() : Row();

    private FrameworkElement Row()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var l = new L(Language);
        for (var i = 0; i < Nodes.Count; i++)
        {
            var node = Nodes[i];
            if (i > 0)
            {
                var arrow = Ui.Icon(DexUi.Glyph.ArrowRight, Thumb * 0.2, Theme.Keys.Tertiary);
                arrow.HorizontalAlignment = HorizontalAlignment.Center;
                arrow.VerticalAlignment = VerticalAlignment.Top;
                // Centred on the sprite (Swift pads by thumb × 0.4 above an SF arrow of thumb × 0.2).
                var arrowBox = new Border
                {
                    Child = arrow, Width = Thumb * ArrowRatio, Margin = new Thickness(Spacing, Thumb * 0.4, Spacing, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                row.Children.Add(arrowBox);
            }
            row.Children.Add(Column(node, l));
        }
        return row;
    }

    private FrameworkElement Column(EvoLineItem node, L l)
    {
        var column = new StackPanel { Orientation = Orientation.Vertical, Width = Thumb + (Names == null ? 0 : NameSlack), VerticalAlignment = VerticalAlignment.Top };
        FrameworkElement art;
        if (node.Content.SpeciesID is { } id)
            art = new SpriteView(id, Thumb, shiny: Shiny, form: Form);
        else
        {
            var q = new TextBlock
            {
                Text = "?", FontSize = Thumb * 0.55, FontWeight = FontWeights.Bold, FontFamily = Theme.DisplayFont,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            q.SetResourceReference(TextBlock.ForegroundProperty, Theme.Keys.Primary);
            System.Windows.Automation.AutomationProperties.SetName(q, MysteryLabel);
            art = new Grid { Width = Thumb, Height = Thumb, Children = { q } };
        }
        if (node.State == EvoLineItemState.Future) art.Opacity = 0.32;
        var artBox = new Grid { Width = Thumb, Height = Thumb, HorizontalAlignment = HorizontalAlignment.Center, Children = { art } };
        if (node.State == EvoLineItemState.Current)
        {
            var dot = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -2) };
            dot.SetResourceReference(Shape.FillProperty, Theme.Keys.Accent);
            artBox.Children.Add(dot);
        }
        column.Children.Add(artBox);
        string? accessibilityName = null;
        if (Names != null && node.Content.SpeciesID is { } sid)
        {
            // One thumbnail wide: only Unown's letter fits; the sprite shows other forms.
            var raw = Names.TryGetValue(sid, out var n) ? n : "…";
            accessibilityName = PokemonForm.DisplayName(raw, sid, Form, (string?)null);
            var name = DexUi.Lines(Ui.Text(accessibilityName, NameFontSize, Theme.Keys.Secondary), NameLineLimit, reserve: false);
            name.TextAlignment = TextAlignment.Center;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.MaxWidth = Thumb + NameSlack;
            name.Margin = new Thickness(0, 1, 0, 0);
            column.Children.Add(name);
        }
        var target = OnOpenDexEntry == null || node.Content.SpeciesID is not { } linkID ? null
            : DexLinks.TryGetValue(linkID, out var t) ? t : null;
        return DexSpriteLink.Wrap(column, target, l.DexOpenEntryHint, accessibilityName, 6, OnOpenDexEntry);
    }

    private FrameworkElement ScrollableRow()
    {
        var hasNames = Names != null;
        var row = Row();
        var viewer = new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Width = MaxWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Focusable = false,
        };
        var l = new L(Language);
        var back = Chevron(false, l.EvolutionScrollPrevious);
        var forward = Chevron(true, l.EvolutionScrollNext);
        var host = new Grid { Width = MaxWidth, HorizontalAlignment = HorizontalAlignment.Left, Children = { viewer, back, forward } };

        void Update()
        {
            var content = viewer.ExtentWidth > 0 ? viewer.ExtentWidth : RowWidth(Nodes.Count, Thumb, hasNames);
            var scrollable = content > MaxWidth + 0.5;
            var canBack = scrollable && viewer.HorizontalOffset > 0.5;
            var canForward = scrollable && viewer.HorizontalOffset < content - MaxWidth - 0.5;
            back.Visibility = canBack ? Visibility.Visible : Visibility.Collapsed;
            forward.Visibility = canForward ? Visibility.Visible : Visibility.Collapsed;
            // Fade only the side that still has more — "continues", not "cut off".
            var f = FadeWidth / MaxWidth;
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            mask.GradientStops.Add(new GradientStop(canBack ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, f));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - f));
            mask.GradientStops.Add(new GradientStop(canForward ? Colors.Transparent : Colors.Black, 1));
            viewer.OpacityMask = mask;
        }
        viewer.ScrollChanged += (_, _) => Update();
        Update();
        void Page(bool fwd)
        {
            var index = PageTarget(fwd, viewer.HorizontalOffset, Nodes.Count, Thumb, hasNames, MaxWidth);
            // Leading anchor: a request past the end clamps exactly to the end.
            viewer.ScrollToHorizontalOffset(index * Stride(Thumb, hasNames));
        }
        back.MouseLeftButtonUp += (_, e) => { e.Handled = true; Page(false); };
        forward.MouseLeftButtonUp += (_, e) => { e.Handled = true; Page(true); };
        return host;
    }

    private Border Chevron(bool forward, string label)
    {
        var glyph = DexUi.Icon(forward ? DexUi.Glyph.ChevronRight : DexUi.Glyph.ChevronLeft, 8, Theme.Keys.Secondary, bold: true);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var circle = new Border
        {
            Child = glyph, Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            HorizontalAlignment = forward ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            // The 16 pt button's centre on the sprite's centre.
            Margin = new Thickness(forward ? 0 : 1, Math.Max(0, Thumb / 2 - 8), forward ? 1 : 0, 0),
            Cursor = Cursors.Hand,
        };
        circle.SetResourceReference(Border.BackgroundProperty, DexTheme.Material);
        circle.SetResourceReference(Border.BorderBrushProperty, DexTheme.Tint(Theme.Keys.Primary, 0.12));
        System.Windows.Automation.AutomationProperties.SetName(circle, label);
        return circle;
    }
}
