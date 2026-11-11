using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>SF Mono stand-in for the recap readout.</summary>
public static class RecapFonts
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
}

/// <summary>A dex entry as the recap sees it (UsageRecap only needs when and whether it graduated).</summary>
internal sealed record RecapGraduate(DexEntry Entry) : IRecapGraduate
{
    public DateTimeOffset? CaughtAt => Entry.CaughtAt;
    public bool IsReleased => Entry.IsReleased;
}

/// <summary>Port of <c>RecapContent</c>: everything the recap prints, read once.</summary>
internal sealed class RecapContent
{
    public AppLanguage Language { get; }
    public UsageRecap Recap { get; }
    public int? CompanionSpeciesID { get; }
    public bool CompanionShiny { get; }
    public PokemonForm? CompanionForm { get; }
    public IReadOnlyList<(DexEntry Entry, string Name)> Graduates { get; }
    private readonly RecapCalendar _calendar;
    private readonly CultureInfo _culture;

    public RecapContent(UsageStore store, CompanionStore companion, RecapScope scope, int offset,
        DateTimeOffset? now = null, RecapCalendar? calendar = null)
    {
        Language = companion.Language;
        _calendar = calendar ?? RecapCalendar.Current();
        _culture = Language.DisplayCulture();
        var at = now ?? DateTimeOffset.Now;
        var period = new RecapPeriod(scope, at, _calendar).Shifted(offset, _calendar);
        Recap = UsageRecap.Make(store.DailyLedger, companion.State.Dex.Select(e => (IRecapGraduate)new RecapGraduate(e)), period, at, _calendar);
        // The Pokémon being raised right now, not the pinned representative: the recap is about usage that
        // just happened, so it shows who lived it.
        CompanionSpeciesID = companion.CurrentSpeciesID;
        CompanionShiny = companion.CurrentIsShiny;
        CompanionForm = companion.CurrentForm;
        Graduates = Recap.Graduated.Take(4).Select(g =>
        {
            var entry = ((RecapGraduate)g).Entry;
            var stored = companion.DexStoredChainNames(entry);
            var name = stored != null && stored.TryGetValue(entry.FinalID, out var n) ? n : $"#{entry.FinalID}";
            // Four chips share a row: only Unown's letter fits, the sprite already shows other forms.
            return (entry, PokemonForm.DisplayName(name, entry.FinalID, entry.Form, (string?)null));
        }).ToList();
    }

    public L L => new(Language);
    public RecapScope Scope => Recap.Period.Scope;

    private DateTime Local(DateTimeOffset d) => _calendar.LocalDate(d);

    /// <summary>"Oct 11 – 17, 2026", "October 2026" or "2026" in the display language. It titles the card.</summary>
    public string PeriodLabel()
    {
        var start = Local(Recap.Period.Start);
        switch (Scope)
        {
            case RecapScope.Week:
                return WeekInterval(start, Local(Recap.Period.End).AddDays(-1));
            case RecapScope.Month:
                return start.ToString("Y", _culture);
            default:
                return start.Year.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The <c>DateIntervalFormatter</c> (template dMMMy) shapes per language.</summary>
    private string WeekInterval(DateTime a, DateTime b)
    {
        string F(DateTime d, string pattern) => d.ToString(pattern, _culture);
        var sameYear = a.Year == b.Year;
        var sameMonth = sameYear && a.Month == b.Month;
        switch (Language)
        {
            case AppLanguage.En:
                return sameMonth ? $"{F(a, "MMM d")} – {F(b, "%d")}, {a.Year}"
                    : sameYear ? $"{F(a, "MMM d")} – {F(b, "MMM d")}, {a.Year}"
                    : $"{F(a, "MMM d, yyyy")} – {F(b, "MMM d, yyyy")}";
            case AppLanguage.Ko:
                return sameMonth ? $"{F(a, "yyyy년 M월 d일")}~{F(b, "d일")}"
                    : sameYear ? $"{F(a, "yyyy년 M월 d일")}~{F(b, "M월 d일")}"
                    : $"{F(a, "yyyy년 M월 d일")}~{F(b, "yyyy년 M월 d일")}";
            case AppLanguage.Ja:
                return sameMonth ? $"{F(a, "yyyy年M月d日")}～{F(b, "d日")}"
                    : sameYear ? $"{F(a, "yyyy年M月d日")}～{F(b, "M月d日")}"
                    : $"{F(a, "yyyy年M月d日")}～{F(b, "yyyy年M月d日")}";
            case AppLanguage.De:
                return sameMonth ? $"{F(a, "%d")}.–{F(b, "d. MMM yyyy")}"
                    : sameYear ? $"{F(a, "d. MMM")} – {F(b, "d. MMM yyyy")}"
                    : $"{F(a, "d. MMM yyyy")} – {F(b, "d. MMM yyyy")}";
            case AppLanguage.Pt:
                return sameMonth ? $"{F(a, "%d")}–{F(b, "d 'de' MMM 'de' yyyy")}"
                    : sameYear ? $"{F(a, "d 'de' MMM")} – {F(b, "d 'de' MMM 'de' yyyy")}"
                    : $"{F(a, "d 'de' MMM 'de' yyyy")} – {F(b, "d 'de' MMM 'de' yyyy")}";
            default:
                return sameMonth ? $"{F(a, "%d")}–{F(b, "d MMM yyyy")}"
                    : sameYear ? $"{F(a, "d MMM")} – {F(b, "d MMM yyyy")}"
                    : $"{F(a, "d MMM yyyy")} – {F(b, "d MMM yyyy")}";
        }
    }

    /// <summary>
    /// One short label per meter: two-letter weekdays (one letter collides: Tuesday/Thursday), a month's
    /// weekly marks only (31 numbers do not fit), month initials for a year.
    /// </summary>
    public IReadOnlyList<string> BucketLabels() => Recap.Buckets.Select(bucket =>
    {
        if (!DateTime.TryParseExact(bucket.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return "";
        switch (Scope)
        {
            case RecapScope.Week:
                var name = _culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek).TrimEnd('.');
                return name[..Math.Min(2, name.Length)].ToUpper(_culture);
            case RecapScope.Month:
                return date.Day is 1 or 8 or 15 or 22 or 29 ? date.Day.ToString(CultureInfo.InvariantCulture) : "";
            default:
                return Language switch
                {
                    AppLanguage.Ko => $"{date.Month}월",
                    AppLanguage.Ja => $"{date.Month}月",
                    _ => _culture.DateTimeFormat.GetMonthName(date.Month)[..1].ToUpper(_culture),
                };
        }
    }).ToList();

    /// <summary>"Sunday, Oct 11" — the template EEEE d MMM in the display language.</summary>
    public string DayLabel(string key)
    {
        if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return key;
        var pattern = Language switch
        {
            AppLanguage.En => "dddd, MMM d",
            AppLanguage.Ko => "M월 d일 dddd",
            AppLanguage.Ja => "M月d日 dddd",
            AppLanguage.De => "dddd, d. MMM",
            AppLanguage.Fr => "dddd d MMM",
            AppLanguage.Pt => "dddd, d 'de' MMM",
            _ => "dddd, d MMM",
        };
        return date.ToString(pattern, _culture);
    }

    public string AccessibilityLabel(UsageRecap.Bucket bucket)
    {
        var name = Scope == RecapScope.Year && DateTime.TryParseExact(bucket.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("Y", _culture)
            : DayLabel(bucket.Key);
        return $"{name}, {(bucket.HasData ? TokenFormatter.Grouped(bucket.Tokens) : L.RecapNoData)}";
    }
}

/// <summary>
/// Port of <c>RecapCard</c>: a Pokédex readout, not a second trainer card — red shell, dark LCD,
/// monospaced digits and segmented meters. Colors are explicit so the card never shifts with the theme.
/// </summary>
internal static class RecapCard
{
    public const double Height = 226;
    private static readonly Color ShellTop = Rgb(0.84, 0.21, 0.18);
    private static readonly Color ShellBottom = Rgb(0.66, 0.12, 0.11);
    private static readonly Color ScreenInk = Rgb(0.05, 0.07, 0.08);
    private static readonly Color Lcd = Rgb(0.51, 0.98, 0.62);
    private static readonly Color Amber = Rgb(1.0, 0.73, 0.28);
    /// <summary>Segments per meter. Eight reads as a level gauge; more would turn back into a plain bar.</summary>
    private const int MeterBlocks = 8;

    private static Color Rgb(double r, double g, double b) => Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    private static SolidColorBrush Solid(Color c, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private static TextBlock Mono(string text, double size, Brush brush, FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = text, FontFamily = RecapFonts.Mono, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            Foreground = brush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.None,
        };
        TextOptions.SetTextFormattingMode(t, TextFormattingMode.Ideal);
        return t;
    }

    /// <summary><c>.lineLimit(1).minimumScaleFactor(0.7)</c>: shrink to fit, never below 70 %.</summary>
    private static FrameworkElement ScaleDown(FrameworkElement content, HorizontalAlignment align)
    {
        var box = new Viewbox { Child = content, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = align };
        return box;
    }

    /// <summary>Text with letter spacing (<c>.tracking</c>), which a TextBlock cannot do: one run per character.</summary>
    private static FrameworkElement Tracked(string text, double size, Brush brush, FontWeight weight, double tracking)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var t = Mono(e.GetTextElement(), size, brush, weight);
            t.Margin = new Thickness(0, 0, tracking, 0);
            row.Children.Add(t);
        }
        return row;
    }

    public static FrameworkElement Build(RecapContent content)
    {
        var column = Ui.VStack(8, Header(content), Screen(content), GraduateStrip(content));
        column.Margin = new Thickness(12);
        var shell = new Grid { Width = Theme.ContentWidth, Height = Height, ClipToBounds = true };
        shell.Clip = new RectangleGeometry(new Rect(0, 0, Theme.ContentWidth, Height), 10, 10);
        shell.Children.Add(new Border { Background = new LinearGradientBrush(ShellTop, ShellBottom, 90) });
        // The hinge side of the case, so the panel reads as a device and not as a card.
        shell.Children.Add(new Border { Width = 5, HorizontalAlignment = HorizontalAlignment.Left, Background = Solid(Colors.Black, 0.14) });
        shell.Children.Add(column);
        column.VerticalAlignment = VerticalAlignment.Top;
        return shell;
    }

    private static FrameworkElement Header(RecapContent content)
    {
        var row = Ui.HStack(6, Lens());
        foreach (var c in new[] { Rgb(0.95, 0.35, 0.35), Rgb(0.98, 0.82, 0.35), Rgb(0.45, 0.88, 0.5) })
            row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Solid(c), Stroke = Solid(Colors.Black, 0.25), StrokeThickness = 0.5 });
        row.Children.Add(Ui.Spacer(4));
        var label = content.PeriodLabel().ToUpper(content.Language.DisplayCulture());
        row.Children.Add(ScaleDown(Tracked(label, 10, Solid(Colors.White, 0.95), FontWeights.Bold, 1.2), HorizontalAlignment.Right));
        row.Margin = new Thickness(2, 0, 0, 0);
        return row;
    }

    private static FrameworkElement Lens()
    {
        var grid = new Grid { Width = 16, Height = 16 };
        grid.Children.Add(new Ellipse
        {
            Fill = new RadialGradientBrush(Rgb(0.72, 0.88, 1.0), Rgb(0.15, 0.45, 0.85))
            {
                GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.35, 0.3), RadiusX = 11.0 / 16, RadiusY = 11.0 / 16,
            },
        });
        grid.Children.Add(new Ellipse { Stroke = Solid(Colors.White, 0.9), StrokeThickness = 1.5 });
        grid.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = Solid(Colors.White, 0.75), Margin = new Thickness(0, 0, 6, 6) });
        return grid;
    }

    private static FrameworkElement Screen(RecapContent content)
    {
        var recap = content.Recap;
        var l = content.L;
        var lcd = Solid(Lcd);
        var total = Mono(TokenFormatter.Compact(recap.Total), 24, lcd, FontWeights.Bold);
        total.VerticalAlignment = VerticalAlignment.Bottom;
        var unit = Mono(l.RecapTokensUnit.ToUpper(content.Language.DisplayCulture()), 7, Solid(Lcd, 0.5), FontWeights.Medium);
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        unit.Margin = new Thickness(0, 0, 0, 5.5);
        var top = new DockPanel();
        if (DeltaChip(recap) is { } chip)
        {
            DockPanel.SetDock(chip, Dock.Right);
            chip.Margin = new Thickness(4, 0, 0, 0);
            top.Children.Add(chip);
        }
        var totals = Ui.HStack(5, total, unit);
        totals.HorizontalAlignment = HorizontalAlignment.Left;
        top.Children.Add(totals);

        var readouts = new Grid();
        readouts.ColumnDefinitions.Add(new ColumnDefinition());
        readouts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        readouts.ColumnDefinitions.Add(new ColumnDefinition());
        var left = Ui.VStack(3,
            Readout(l.RecapBestDay, recap.BestDay is { } best ? TokenFormatter.Compact(best.Tokens) : "--", content),
            Readout(l.RecapBestStreak, l.RecapDays(recap.BestStreak), content));
        var right = Ui.VStack(3,
            Readout(l.RecapActiveDays, $"{recap.ActiveDays}/{recap.CountedDays}", content),
            Readout(l.RecapGraduates, recap.Graduated.Count.ToString(CultureInfo.InvariantCulture), content));
        Grid.SetColumn(right, 2);
        readouts.Children.Add(left);
        readouts.Children.Add(right);

        var stack = Ui.VStack(7, top, Meters(content), readouts);
        stack.Margin = new Thickness(10);
        var glow = new RadialGradientBrush(Color.FromArgb((byte)Math.Round(0.07 * 255), Lcd.R, Lcd.G, Lcd.B), Color.FromArgb(0, Lcd.R, Lcd.G, Lcd.B))
        {
            MappingMode = BrushMappingMode.Absolute, Center = new Point(0.15 * 304, 0.1 * 150),
            GradientOrigin = new Point(0.15 * 304, 0.1 * 150), RadiusX = 180, RadiusY = 180,
        };
        var screen = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        screen.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Solid(ScreenInk) });
        screen.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = glow });
        screen.Children.Add(stack);
        screen.Children.Add(new Border { CornerRadius = new CornerRadius(6), BorderBrush = Solid(Colors.Black, 0.35), BorderThickness = new Thickness(2), IsHitTestVisible = false });
        return screen;
    }

    /// <summary>No chip unless the ledger covers the whole compared stretch: a fresh install must not claim a jump.</summary>
    private static FrameworkElement? DeltaChip(UsageRecap recap)
    {
        if (recap.Delta is not { } delta) return null;
        var up = delta >= 0;
        var color = up ? Lcd : Amber;
        var text = Mono($"{(up ? "▲" : "▼")}{TokenFormatter.Percent(Math.Abs(delta) * 100)}", 9, Solid(color), FontWeights.Bold);
        return new Border
        {
            Child = text, Padding = new Thickness(5, 2, 5, 2), CornerRadius = new CornerRadius(2),
            BorderBrush = Solid(color, 0.6), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static FrameworkElement Meters(RecapContent content)
    {
        var recap = content.Recap;
        var peak = Math.Max(1, recap.Buckets.Count == 0 ? 1 : recap.Buckets.Max(b => b.Tokens));
        var labels = content.BucketLabels();
        var spacing = content.Scope switch { RecapScope.Week => 6.0, RecapScope.Month => 1.5, _ => 4.0 };
        var grid = new Grid();
        for (var i = 0; i < recap.Buckets.Count; i++)
        {
            if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(spacing) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bucket = recap.Buckets[i];
            var label = Mono(i < labels.Count ? labels[i] : "", content.Scope == RecapScope.Week ? 8 : 7,
                bucket.IsCurrent ? Solid(Lcd) : Solid(Lcd, 0.45), bucket.IsCurrent ? FontWeights.Bold : FontWeights.Normal);
            // Zero-width frame: a month's "15" is wider than its column and must not widen it.
            var labelHost = new OverflowCenter { Child = label, Height = 10 };
            var cell = Ui.VStack(3, Meter(bucket, peak), labelHost);
            cell.Background = Brushes.Transparent;
            cell.Help(content.AccessibilityLabel(bucket));
            System.Windows.Automation.AutomationProperties.SetName(cell, content.AccessibilityLabel(bucket));
            Grid.SetColumn(cell, i * 2);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static FrameworkElement Meter(UsageRecap.Bucket bucket, long peak)
    {
        // Usage always lights one segment, so "quiet" and "nothing" stay distinct; an unknown bucket (future,
        // or before the ledger started) dims even its unlit grid.
        var lit = bucket.Tokens > 0 ? Math.Max(1, (int)Math.Round((double)bucket.Tokens / peak * MeterBlocks, MidpointRounding.AwayFromZero)) : 0;
        var stack = Ui.VStack(2);
        for (var index = 0; index < MeterBlocks; index++)
        {
            var on = index >= MeterBlocks - lit;
            var fill = on ? (bucket.IsCurrent ? Solid(Lcd) : Solid(Lcd, 0.55)) : Solid(Lcd, bucket.HasData ? 0.09 : 0.035);
            stack.Children.Add(new Rectangle { Height = 4, Fill = fill, SnapsToDevicePixels = true });
        }
        return stack;
    }

    private static FrameworkElement Readout(string label, string value, RecapContent content)
    {
        var culture = content.Language.DisplayCulture();
        var name = ScaleDown(Mono(label.ToUpper(culture), 7, Solid(Lcd, 0.5), FontWeights.Medium), HorizontalAlignment.Left);
        var number = ScaleDown(Mono(value, 9.5, Solid(Lcd), FontWeights.Bold), HorizontalAlignment.Right);
        var row = new DockPanel();
        DockPanel.SetDock(number, Dock.Right);
        number.Margin = new Thickness(2, 0, 0, 0);
        row.Children.Add(number);
        row.Children.Add(name);
        return row;
    }

    private static FrameworkElement GraduateStrip(RecapContent content)
    {
        var row = Ui.HStack(5,
            new SpriteView(content.CompanionSpeciesID, 18, shiny: content.CompanionShiny, form: content.CompanionForm),
            new Rectangle { Width = 1, Height = 14, Fill = Solid(Colors.White, 0.35) });
        if (content.Graduates.Count == 0)
        {
            var empty = Mono(content.L.RecapNoGraduates, 8, Solid(Colors.White, 0.8));
            empty.TextWrapping = TextWrapping.Wrap;
            empty.MaxWidth = Theme.ContentWidth - 24 - 18 - 12;
            row.Children.Add(empty);
        }
        else
        {
            foreach (var (entry, name) in content.Graduates)
            {
                var sprite = new Grid { Width = 18, Height = 18 };
                sprite.Children.Add(new SpriteView(entry.FinalID, 18, shiny: entry.IsShiny, form: entry.Form));
                // A badge on the sprite rather than after the name: four chips share the row.
                if (entry.IsShiny)
                    sprite.Children.Add(new TextBlock
                    {
                        Text = "✨", FontSize = 7, Foreground = Solid(Color.FromRgb(0xFF, 0xD6, 0x0A)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, -2, -3, 0),
                    });
                var chip = new Border
                {
                    Child = Ui.HStack(2, sprite, ScaleDown(Mono(name, 8, Solid(Colors.White), FontWeights.SemiBold), HorizontalAlignment.Left)),
                    Padding = new Thickness(4, 2, 4, 2), CornerRadius = new CornerRadius(3), Background = Solid(Colors.Black, 0.22),
                    MaxWidth = 74,
                };
                row.Children.Add(chip);
            }
        }
        row.Children.Add(Ui.Spacer());
        row.Margin = new Thickness(2, 0, 0, 0);
        return row;
    }
}

/// <summary>A zero-width slot that centres its child, letting it overflow both sides (SwiftUI <c>.fixedSize().frame(width: 0)</c>).</summary>
internal sealed class OverflowCenter : FrameworkElement
{
    private UIElement? _child;

    public UIElement? Child
    {
        get => _child;
        set
        {
            if (_child != null) { RemoveVisualChild(_child); RemoveLogicalChild(_child); }
            _child = value;
            if (value != null) { AddVisualChild(value); AddLogicalChild(value); }
        }
    }

    protected override int VisualChildrenCount => _child == null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => _child!;

    protected override Size MeasureOverride(Size available)
    {
        _child?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(0, _child?.DesiredSize.Height ?? 0);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (_child != null)
        {
            var w = _child.DesiredSize.Width;
            _child.Arrange(new Rect((final.Width - w) / 2, 0, w, final.Height));
        }
        return final;
    }
}
