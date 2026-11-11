using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Date wording the Home tab and the footer need. SwiftUI gets these from Foundation
/// (<c>Text(date, style: .relative)</c>, <c>DateFormatter</c> templates); .NET has no equivalent,
/// so the per-language patterns are written out here — always in the app language, never the
/// system locale (defect-log §표시·UI: one screen, one language).
/// </summary>
public static class HomeFormat
{
    private static CultureInfo Culture(AppLanguage lang) => lang.DisplayCulture();

    /// <summary>
    /// SwiftUI <c>Text(date, style: .relative)</c>: the two largest units of the distance to
    /// <paramref name="date"/> ("2 hr, 23 min", "2 days, 23 hr", "0 sec"), with no "in"/"ago".
    /// </summary>
    public static string Relative(DateTimeOffset date, AppLanguage lang, DateTimeOffset? now = null)
    {
        var seconds = (long)Math.Floor(Math.Abs((date - (now ?? DateTimeOffset.Now)).TotalSeconds));
        long days = seconds / 86400, hours = seconds % 86400 / 3600, minutes = seconds % 3600 / 60, secs = seconds % 60;
        (long Value, int Unit)[] parts = days > 0 ? [(days, 0), (hours, 1)]
            : hours > 0 ? [(hours, 1), (minutes, 2)]
            : minutes > 0 ? [(minutes, 2), (secs, 3)]
            : [(secs, 3)];
        var shown = parts.Where((p, i) => i == 0 || p.Value > 0).Select(p => Unit(lang, p.Value, p.Unit)).ToList();
        var separator = lang switch
        {
            AppLanguage.Ko or AppLanguage.Ru or AppLanguage.Fr => " ",
            AppLanguage.Ja => "",
            _ => ", ",
        };
        return string.Join(separator, shown);
    }

    private static string Unit(AppLanguage lang, long n, int unit) => lang switch
    {
        AppLanguage.Ko => n + new[] { "일", "시간", "분", "초" }[unit],
        AppLanguage.Ja => n + new[] { "日", "時間", "分", "秒" }[unit],
        AppLanguage.De => $"{n} " + new[] { n == 1 ? "Tag" : "Tg.", "Std.", "Min.", "Sek." }[unit],
        AppLanguage.Fr => $"{n} " + new[] { "j", "h", "min", "s" }[unit],
        AppLanguage.Es => $"{n} " + new[] { n == 1 ? "día" : "días", "h", "min", "s" }[unit],
        AppLanguage.Pt => $"{n} " + new[] { n == 1 ? "dia" : "dias", "h", "min", "s" }[unit],
        AppLanguage.Ru => $"{n} " + new[] { "дн.", "ч", "мин", "с" }[unit],
        _ => $"{n} " + new[] { n == 1 ? "day" : "days", "hr", "min", "sec" }[unit],
    };

    /// <summary>
    /// Swift <c>resetClockWindow</c>: within 6 h, or today, the suffix is the clock alone; further out
    /// (weekly limits) it also names the weekday and day, because "in 2 days, 9 hr" alone does not say when.
    /// </summary>
    private static readonly TimeSpan ResetClockWindow = TimeSpan.FromHours(6);

    /// <summary>Swift <c>resetClockSuffix</c>: " (14:57)" or " (Thursday 1, 14:57)", always 24-hour.</summary>
    public static string ResetClockSuffix(DateTimeOffset reset, AppLanguage lang, DateTimeOffset? now = null)
    {
        var n = now ?? DateTimeOffset.Now;
        var local = reset.ToLocalTime();
        var nearby = local.Date == n.ToLocalTime().Date || reset - n <= ResetClockWindow;
        var pattern = nearby ? "HH:mm" : lang switch
        {
            AppLanguage.Ko => "d'일' dddd HH:mm",
            AppLanguage.Ja => "d'日'(dddd) HH:mm",
            AppLanguage.De => "dddd, d., HH:mm",
            AppLanguage.Fr => "dddd d HH:mm",
            AppLanguage.Pt or AppLanguage.Ru => "dddd, d, HH:mm",
            _ => "dddd d, HH:mm",
        };
        return " (" + local.ToString(pattern, Culture(lang)) + ")";
    }

    /// <summary>Swift <c>resetLabel</c>: countdown + absolute reset time.</summary>
    public static string ResetLabel(DateTimeOffset reset, AppLanguage lang) =>
        Relative(reset, lang) + ResetClockSuffix(reset, lang);

    /// <summary>
    /// Swift <c>DailyTrendMetrics.dayStamp</c> (template <c>MdE</c>): month/day + weekday in the order the
    /// language uses — en "Fri, 10/16", ko "10. 16. (금)", fr "ven. 16/10".
    /// </summary>
    public static string DayStamp(string date, AppLanguage lang)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return "";
        var pattern = lang switch
        {
            AppLanguage.Ko => "M. d. (ddd)",
            AppLanguage.Ja => "M/d(ddd)",
            AppLanguage.De => "ddd, d.M.",
            AppLanguage.Fr => "ddd dd/MM",
            AppLanguage.Es or AppLanguage.Pt => "ddd, d/M",
            AppLanguage.Ru => "ddd, dd.MM",
            _ => "ddd, M/d",
        };
        return d.ToString(pattern, Culture(lang));
    }

    /// <summary>
    /// Keeps a text current the way SwiftUI's <c>.relative</c> style ticks: re-evaluated every second while
    /// the element is on screen.
    /// </summary>
    public static TextBlock Ticking(this TextBlock text, Func<string> value)
    {
        text.Text = value();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => text.Text = value();
        text.Loaded += (_, _) => timer.Start();
        text.Unloaded += (_, _) => timer.Stop();
        return text;
    }
}

/// <summary>Layout helpers for SwiftUI behaviours the shared <see cref="Stack"/> does not cover.</summary>
public static class HomeLayout
{
    /// <summary>
    /// <c>HStack(alignment: .firstTextBaseline)</c>: bottom-aligns the row's texts and lifts each by the
    /// difference between its descent and the deepest one, so different sizes share one baseline.
    /// </summary>
    public static Stack BaselineAligned(this Stack row)
    {
        var texts = row.Children.OfType<TextBlock>().ToList();
        static double Descent(TextBlock t) => t.FontSize * (t.FontFamily.LineSpacing - t.FontFamily.Baseline);
        var deepest = texts.Count == 0 ? 0 : texts.Max(Descent);
        foreach (var t in texts)
        {
            t.VerticalAlignment = VerticalAlignment.Bottom;
            t.Margin = new Thickness(t.Margin.Left, t.Margin.Top, t.Margin.Right, deepest - Descent(t));
        }
        return row;
    }

    /// <summary>A brush of a theme color at another opacity (SwiftUI <c>color.opacity(x)</c>).</summary>
    public static Brush Tint(string key, double opacity)
    {
        var c = (Theme.Brush(key) as SolidColorBrush)?.Color ?? Colors.Gray;
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity * c.A / 255.0), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>Rounded box with an explicit brush (<c>.padding(p).background(color, in: RoundedRectangle(r))</c>).</summary>
    public static Border Box(UIElement child, Brush background, double padding, double radius) =>
        new() { Child = child, Background = background, Padding = new Thickness(padding), CornerRadius = new CornerRadius(radius) };
}

/// <summary>
/// SwiftUI <c>ViewThatFits(in: .horizontal)</c>: shows the first child whose natural width fits the
/// offered width, else the last one. The others stay in the tree but are laid out empty and hidden.
/// </summary>
public sealed class ViewThatFits : Panel
{
    private int _chosen;

    public ViewThatFits(params UIElement[] candidates)
    {
        VerticalAlignment = VerticalAlignment.Center;
        foreach (var c in candidates) Children.Add(c);
    }

    protected override Size MeasureOverride(Size available)
    {
        _chosen = Children.Count - 1;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, available.Height));
            if (Children[i].DesiredSize.Width <= available.Width + 0.5) { _chosen = i; break; }
        }
        var chosen = Children[_chosen];
        chosen.Measure(available);
        return chosen.DesiredSize;
    }

    protected override Size ArrangeOverride(Size final)
    {
        for (var i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            var on = i == _chosen;
            c.Opacity = on ? 1 : 0;
            c.IsHitTestVisible = on;
            c.Arrange(on ? new Rect(final) : new Rect(0, 0, 0, 0));
        }
        return final;
    }
}

/// <summary>
/// A row of equal-width columns with a fixed gap — the trend rows' <c>HStack(spacing:)</c> of
/// <c>.frame(maxWidth: .infinity)</c> children. Children keep their own width when narrower and are
/// centred in their column; wider ones (axis labels, <c>fixedSize</c>) overflow it symmetrically.
/// Vertical placement follows each child's <c>VerticalAlignment</c> (bars sit on the bottom).
/// </summary>
public sealed class EqualColumns : Panel
{
    public double Spacing { get; init; }

    private double ColumnWidth(double width) =>
        Children.Count == 0 ? 0 : Math.Max(0, (width - Spacing * (Children.Count - 1)) / Children.Count);

    protected override Size MeasureOverride(Size available)
    {
        double height = 0;
        foreach (UIElement c in Children)
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            height = Math.Max(height, c.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var column = ColumnWidth(final.Width);
        for (var i = 0; i < Children.Count; i++)
        {
            var c = Children[i];
            var x = i * (column + Spacing);
            // Texts keep their natural width (fixedSize) even with the default Stretch alignment.
            var stretch = c is FrameworkElement { HorizontalAlignment: HorizontalAlignment.Stretch } fe
                && double.IsNaN(fe.Width) && c is not TextBlock;
            var w = stretch ? column : c.DesiredSize.Width;
            var h = c.DesiredSize.Height;
            var y = c is FrameworkElement { VerticalAlignment: VerticalAlignment.Bottom } ? final.Height - h
                : c is FrameworkElement { VerticalAlignment: VerticalAlignment.Stretch } ? 0
                : (final.Height - h) / 2;
            if (c is FrameworkElement { VerticalAlignment: VerticalAlignment.Stretch }) h = final.Height;
            c.Arrange(new Rect(x + (column - w) / 2, y, w, h));
        }
        return final;
    }
}
