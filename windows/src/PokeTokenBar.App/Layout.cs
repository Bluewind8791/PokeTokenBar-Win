using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PokeTokenBar.App;

/// <summary>Flexible gap inside a <see cref="Stack"/> — SwiftUI's <c>Spacer()</c>.</summary>
public sealed class Spacer : FrameworkElement
{
    public double MinLength { get; init; }
}

/// <summary>
/// SwiftUI-style <c>HStack</c>/<c>VStack</c>: children in a line with a fixed spacing; any
/// <see cref="Spacer"/> takes an equal share of the leftover main-axis length. Cross-axis
/// placement follows each child's alignment (default: centre for rows, stretch-left for columns).
/// The views are written in C# on top of this so they read like the SwiftUI they are ported from.
/// </summary>
public sealed class Stack : Panel
{
    public Orientation Orientation { get; set; } = Orientation.Vertical;
    public double Spacing { get; set; }

    private bool Horizontal => Orientation == Orientation.Horizontal;

    private IEnumerable<UIElement> Visible => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed);

    protected override Size MeasureOverride(Size available)
    {
        double main = 0, cross = 0;
        var visible = Visible.ToList();
        var mainAvail = Horizontal ? available.Width : available.Height;
        var crossAvail = Horizontal ? available.Height : available.Width;
        var spacers = 0;
        foreach (var child in visible)
        {
            if (child is Spacer s) { spacers++; main += s.MinLength; continue; }
            var remaining = double.IsInfinity(mainAvail) ? double.PositiveInfinity : Math.Max(0, mainAvail - main);
            child.Measure(Horizontal ? new Size(remaining, crossAvail) : new Size(crossAvail, remaining));
            var d = child.DesiredSize;
            main += Horizontal ? d.Width : d.Height;
            cross = Math.Max(cross, Horizontal ? d.Height : d.Width);
        }
        main += Spacing * Math.Max(0, visible.Count - 1);
        // A row with a spacer wants the whole width it is offered (Spacer pushes to the edge).
        if (spacers > 0 && !double.IsInfinity(mainAvail)) main = Math.Max(main, mainAvail);
        return Horizontal ? new Size(main, cross) : new Size(cross, main);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var visible = Visible.ToList();
        var mainFinal = Horizontal ? final.Width : final.Height;
        var crossFinal = Horizontal ? final.Height : final.Width;
        var fixedMain = visible.Where(c => c is not Spacer).Sum(c => Horizontal ? c.DesiredSize.Width : c.DesiredSize.Height)
            + visible.OfType<Spacer>().Sum(s => s.MinLength)
            + Spacing * Math.Max(0, visible.Count - 1);
        var spacers = visible.OfType<Spacer>().Count();
        var extra = spacers > 0 ? Math.Max(0, mainFinal - fixedMain) / spacers : 0;

        double pos = 0;
        foreach (var child in visible)
        {
            double len;
            if (child is Spacer s) len = s.MinLength + extra;
            else len = Horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            var crossLen = Horizontal ? child.DesiredSize.Height : child.DesiredSize.Width;
            double crossPos = 0;
            if (child is FrameworkElement fe)
            {
                if (Horizontal)
                {
                    switch (fe.VerticalAlignment)
                    {
                        case VerticalAlignment.Top: break;
                        case VerticalAlignment.Bottom: crossPos = crossFinal - crossLen; break;
                        case VerticalAlignment.Stretch: crossLen = crossFinal; break;
                        default: crossPos = (crossFinal - crossLen) / 2; break;
                    }
                }
                else
                {
                    switch (fe.HorizontalAlignment)
                    {
                        case HorizontalAlignment.Center: crossPos = (crossFinal - crossLen) / 2; break;
                        case HorizontalAlignment.Right: crossPos = crossFinal - crossLen; break;
                        case HorizontalAlignment.Stretch: crossLen = crossFinal; break;
                    }
                }
            }
            child.Arrange(Horizontal ? new Rect(pos, crossPos, len, crossLen) : new Rect(crossPos, pos, crossLen, len));
            pos += len + Spacing;
        }
        return final;
    }
}

/// <summary>Small builders so ported views read like their SwiftUI originals.</summary>
public static class Ui
{
    public static Stack HStack(double spacing, params UIElement?[] children) => Make(Orientation.Horizontal, spacing, children);
    public static Stack VStack(double spacing, params UIElement?[] children) => Make(Orientation.Vertical, spacing, children);

    private static Stack Make(Orientation o, double spacing, UIElement?[] children)
    {
        var s = new Stack { Orientation = o, Spacing = spacing };
        if (o == Orientation.Vertical) s.HorizontalAlignment = HorizontalAlignment.Stretch;
        foreach (var c in children)
        {
            if (c == null) continue;
            if (o == Orientation.Vertical && c is FrameworkElement fe && fe.ReadLocalValue(FrameworkElement.HorizontalAlignmentProperty) == DependencyProperty.UnsetValue)
                fe.HorizontalAlignment = HorizontalAlignment.Stretch;
            s.Children.Add(c);
        }
        return s;
    }

    public static Spacer Spacer(double minLength = 0) => new() { MinLength = minLength };

    /// <summary>A text run styled by a size token and a theme brush key.</summary>
    public static TextBlock Text(string text, double size = Theme.FontSize.Body, string brushKey = Theme.Keys.Primary,
        FontWeight? weight = null, bool monospacedDigits = false)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = size >= Theme.FontSize.Title2 ? Theme.DisplayFont : Theme.UiFont,
            FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (monospacedDigits) System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }

    public static TextBlock Wrapping(this TextBlock t)
    {
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        return t;
    }

    public static T Help<T>(this T element, string? tip) where T : FrameworkElement
    {
        if (!string.IsNullOrEmpty(tip))
        {
            element.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(element, 600);   // NSInitialToolTipDelay = 600 ms
        }
        return element;
    }

    public static T Padding<T>(this T element, double l, double t, double r, double b) where T : FrameworkElement
    {
        element.Margin = new Thickness(l, t, r, b);
        return element;
    }

    /// <summary>A 1px separator line (SwiftUI <c>Divider()</c>).</summary>
    public static FrameworkElement Divider()
    {
        var b = new Border { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        b.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Separator);
        return b;
    }

    /// <summary>Rounded background box (e.g. <c>.padding(8).background(...).clipShape(RoundedRectangle(6))</c>).</summary>
    public static Border Box(UIElement child, string backgroundKey, double padding = 8, double radius = 6)
    {
        var b = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(radius) };
        b.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        return b;
    }

    /// <summary>Segoe Fluent Icons glyph — stands in for SF Symbols.</summary>
    public static TextBlock Icon(string glyph, double size = 14, string brushKey = Theme.Keys.Secondary)
    {
        var t = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }

    /// <summary>SF Symbol → Segoe Fluent Icons code points used by the ported views.</summary>
    public static class Glyphs
    {
        public const string Refresh = "";            // arrow.clockwise
        public const string Settings = "";           // gearshape
        public const string Power = "";              // power
        public const string Hide = "";               // eye.slash
        public const string Chart = "";              // chart.bar.xaxis
        public const string Help = "";               // questionmark.circle
        public const string Warning = "";            // exclamationmark.triangle.fill
        public const string Back = "";               // chevron.left
        public const string Forward = "";            // chevron.right
        public const string Close = "";              // xmark
        public const string Star = "";               // star.fill
        public const string Search = "";             // magnifyingglass
        public const string Sparkle = "";            // sparkles
    }
}
