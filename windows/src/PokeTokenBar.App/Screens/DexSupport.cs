using System.Globalization;

using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Collection-screen colors the shared <see cref="Theme"/> has no token for: SwiftUI opacity variants
/// (<c>Color.secondary.opacity(0.06)</c>, <c>accentColor.opacity(0.16)</c>, rarity tints),
/// <c>controlBackgroundColor</c> and <c>.regularMaterial</c>. Installed as application resources so
/// <c>SetResourceReference</c> repaints them on a theme switch, like Theme's own keys.
/// </summary>
internal static class DexTheme
{
    private static readonly Dictionary<string, Func<Color>> Defs = new();
    private static bool? _installedDark;
    private static bool _subscribed;

    /// <summary>Called at the top of every Collection build: (re)installs the keys for the current appearance.</summary>
    public static void Ensure()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            Theme.Changed += Reinstall;
        }
        if (_installedDark != Theme.IsDark) Reinstall();
    }

    private static void Reinstall()
    {
        _installedDark = Theme.IsDark;
        foreach (var (key, color) in Defs) Install(key, color);
    }

    private static void Install(string key, Func<Color> color)
    {
        if (Application.Current == null) return;
        var brush = new SolidColorBrush(color());
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static string Define(string key, Func<Color> color)
    {
        if (Defs.TryAdd(key, color)) Install(key, color);
        return key;
    }

    /// <summary><c>color.opacity(x)</c> of a Theme key — multiplies the token's own alpha, as SwiftUI does.</summary>
    public static string Tint(string baseKey, double opacity) => Define(
        $"Dex.{baseKey}@{opacity.ToString("0.###", CultureInfo.InvariantCulture)}", () =>
        {
            var c = (Application.Current?.Resources[baseKey] as SolidColorBrush)?.Color ?? Colors.Gray;
            return Color.FromArgb((byte)Math.Round(c.A * opacity), c.R, c.G, c.B);
        });

    /// <summary>NSColor.controlBackgroundColor (search field, toolbar buttons).</summary>
    public static string ControlBackground => Define("Dex.ControlBackground",
        () => Theme.IsDark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White);

    /// <summary><c>.regularMaterial</c> under small labels drawn over sprites.</summary>
    public static string Material => Define("Dex.Material",
        () => Theme.IsDark ? Color.FromArgb(0xD8, 0x29, 0x29, 0x2A) : Color.FromArgb(0xD0, 0xF4, 0xF4, 0xF5));

    /// <summary>Stands in for the ✨ emoji's color: WPF draws emoji glyphs monochrome.</summary>
    public static string Sparkle => Define("Dex.Sparkle", () => Color.FromRgb(0xFF, 0xC4, 0x2E));
}

/// <summary>Small view helpers shared by the Collection screens.</summary>
internal static class DexUi
{
    /// <summary>Segoe Fluent Icons stand-ins for the SF Symbols the Collection screens use.</summary>
    public static class Glyph
    {
        public const string Search = "";        // magnifyingglass
        public const string ClearFilled = "";   // xmark.circle.fill
        public const string Sort = "";          // arrow.up.arrow.down
        public const string ChevronDown = "";
        public const string ChevronUp = "";
        public const string ChevronLeft = "";
        public const string ChevronRight = "";
        public const string Star = "";          // star
        public const string StarFill = "";      // star.fill
        public const string Sync = "";          // arrow.triangle.2.circlepath
        public const string ArrowRight = "";    // arrow.right
        public const string Check = "";         // checkmark
    }

    public static TextBlock Icon(string glyph, double size, string brushKey, bool bold = false)
    {
        var t = Ui.Icon(glyph, size, brushKey);
        if (bold) t.FontWeight = FontWeights.Bold;
        return t;
    }

    /// <summary>One four-pointed star with concave sides in a 16×16 box (SF Symbol <c>sparkle</c>).</summary>
    private static string Star(double cx, double cy, double r)
    {
        var k = r * 0.16;   // how far the curved sides bow toward the centre
        string P(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"{x:0.###},{y:0.###}");
        return $"M {P(cx, cy - r)} Q {P(cx + k, cy - k)} {P(cx + r, cy)} Q {P(cx + k, cy + k)} {P(cx, cy + r)} " +
               $"Q {P(cx - k, cy + k)} {P(cx - r, cy)} Q {P(cx - k, cy - k)} {P(cx, cy - r)} Z";
    }

    private static Path StarPath(string data, double size, string brushKey)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data), Stretch = Stretch.Uniform, Width = size, Height = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
        path.SetResourceReference(Shape.FillProperty, brushKey);
        return path;
    }

    /// <summary>SF <c>sparkle</c>: the shiny filter when off.</summary>
    public static FrameworkElement Sparkle(double size, string brushKey) => StarPath(Star(8, 8, 8), size, brushKey);

    /// <summary>The ✨ emoji (and SF <c>sparkles</c>): drawn, because WPF renders emoji monochrome.</summary>
    public static FrameworkElement Sparkles(double size, string? brushKey = null) =>
        StarPath($"{Star(6.5, 9.5, 6.5)} {Star(13, 3, 3)} {Star(13.5, 13.5, 2.5)}", size, brushKey ?? DexTheme.Sparkle);

    /// <summary>A shape with a fill and an optional inner stroke (SwiftUI <c>.background(…).overlay(strokeBorder)</c>).</summary>
    /// The stroke is an overlay, as in SwiftUI: it never insets or resizes the content (a WPF border thickness would).
    public static Border Rounded(UIElement? child, string? backgroundKey, double radius, Thickness padding,
        string? strokeKey = null, double strokeWidth = 0)
    {
        var b = new Border { CornerRadius = new CornerRadius(radius) };
        if (backgroundKey != null) b.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        if (strokeKey != null && strokeWidth > 0)
        {
            var stroke = new Border { CornerRadius = b.CornerRadius, BorderThickness = new Thickness(strokeWidth), IsHitTestVisible = false };
            stroke.SetResourceReference(Border.BorderBrushProperty, strokeKey);
            b.Child = new Grid { Children = { new Border { Child = child, Padding = padding }, stroke } };
        }
        else
        {
            b.Child = child;
            b.Padding = padding;
        }
        return b;
    }

    public static Border Capsule(UIElement? child, string? backgroundKey, Thickness padding,
        string? strokeKey = null, double strokeWidth = 0)
    {
        var b = PokeTokenBar.App.Capsule.Round(Rounded(child, backgroundKey, 0, padding, strokeKey, strokeWidth));
        if (b.Child is Grid { Children.Count: 2 } g && g.Children[1] is Border stroke)
            b.SizeChanged += (_, _) => stroke.CornerRadius = new CornerRadius(b.ActualHeight / 2);
        return b;
    }

    /// <summary>Fixed-height multi-line label (SwiftUI <c>.lineLimit(n, reservesSpace: true)</c>).</summary>
    public static TextBlock Lines(TextBlock t, int lines, bool reserve)
    {
        // SF's line height (~1.2 em), not Segoe's 1.33, so reserved lines take the original's space.
        var lineHeight = Math.Round(t.FontSize * 1.22);
        t.TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        t.LineHeight = lineHeight;
        t.MaxHeight = lineHeight * lines;
        if (reserve) t.Height = lineHeight * lines;
        return t;
    }

    /// <summary>
    /// A grid of equal flexible columns with fixed gaps (SwiftUI <c>LazyVGrid</c> of <c>.flexible()</c> items,
    /// or the Pokédex's HStack/VStack rows). Rows size to content unless <paramref name="fillRows"/>.
    /// </summary>
    public static Grid FlexGrid(IReadOnlyList<UIElement?> items, int columns, double hSpacing, double vSpacing,
        int? rows = null, bool fillRows = false)
    {
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            if (c > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(hSpacing) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var rowCount = rows ?? Math.Max(1, (items.Count + columns - 1) / columns);
        for (var r = 0; r < rowCount; r++)
        {
            if (r > 0) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(vSpacing) });
            grid.RowDefinitions.Add(new RowDefinition { Height = fillRows ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }
        for (var i = 0; i < items.Count && i < rowCount * columns; i++)
        {
            if (items[i] is not { } item) continue;
            Grid.SetColumn(item, i % columns * 2);
            Grid.SetRow(item, i / columns * 2);
            grid.Children.Add(item);
        }
        return grid;
    }

    /// <summary>
    /// A vertical ScrollViewer whose thin scroller overlays the content's right edge — the macOS overlay
    /// scroller. Content keeps the 12 pt lane (<see cref="Theme.ScrollerInset"/>) so nothing sits under it
    /// (Swift <c>reservesScrollerLane</c>). The stock template would give the bar its own column instead.
    /// </summary>
    public static ScrollViewer OverlayScroller(UIElement? content = null)
    {
        var sv = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Template = OverlayTemplate(),
        };
        if (content is FrameworkElement fe) fe.Margin = new Thickness(0, 0, Theme.ScrollerInset, 0);
        return sv;
    }

    private static ControlTemplate? _overlay;

    private static ControlTemplate OverlayTemplate()
    {
        if (_overlay != null) return _overlay;
        var grid = new FrameworkElementFactory(typeof(Grid));
        var presenter = new FrameworkElementFactory(typeof(ScrollContentPresenter)) { Name = "PART_ScrollContentPresenter" };
        presenter.SetBinding(ScrollContentPresenter.CanContentScrollProperty,
            new Binding(nameof(ScrollViewer.CanContentScroll)) { RelativeSource = RelativeSource.TemplatedParent });
        grid.AppendChild(presenter);
        var bar = new FrameworkElementFactory(typeof(ScrollBar)) { Name = "PART_VerticalScrollBar" };
        bar.SetValue(ScrollBar.OrientationProperty, Orientation.Vertical);
        bar.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        bar.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 2, 0));
        bar.SetBinding(RangeBase.ValueProperty, new Binding(nameof(ScrollViewer.VerticalOffset)) { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.OneWay });
        bar.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(ScrollViewer.ScrollableHeight)) { RelativeSource = RelativeSource.TemplatedParent });
        bar.SetBinding(ScrollBar.ViewportSizeProperty, new Binding(nameof(ScrollViewer.ViewportHeight)) { RelativeSource = RelativeSource.TemplatedParent });
        bar.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ScrollViewer.ComputedVerticalScrollBarVisibility)) { RelativeSource = RelativeSource.TemplatedParent });
        // Overlay scrollers stay hidden until the pointer is over the scroll view.
        bar.SetValue(UIElement.OpacityProperty, 0.0);
        grid.AppendChild(bar);
        _overlay = new ControlTemplate(typeof(ScrollViewer)) { VisualTree = grid };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "PART_VerticalScrollBar"));
        _overlay.Triggers.Add(hover);
        _overlay.Seal();
        return _overlay;
    }

    /// <summary>
    /// A clickable chrome-less element for SwiftUI <c>.buttonStyle(.plain)</c> buttons whose label is a whole
    /// view. Disabled elements ignore clicks (callers dim them as the original does).
    /// </summary>
    public static T Plain<T>(this T element, Action action, bool enabled = true) where T : FrameworkElement =>
        enabled ? element.OnClick(action) : element;

    /// <summary>A popup menu (SwiftUI <c>Menu</c> / <c>Picker(.menu)</c>) with an optional check per item.</summary>
    public static ContextMenu Menu(IEnumerable<(string Title, bool Checked, Action Action)> items)
    {
        var menu = new ContextMenu { FontFamily = Theme.UiFont, FontSize = Theme.FontSize.Body };
        foreach (var (title, isChecked, action) in items)
        {
            var item = new MenuItem { Header = title, IsCheckable = false, IsChecked = isChecked };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        return menu;
    }

    public static void ShowMenu(FrameworkElement anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>macOS indeterminate <c>ProgressView()</c>: a small spinning arc.</summary>
    public static FrameworkElement Spinner(double size = 14)
    {
        var arc = new Ellipse
        {
            Width = size, Height = size, StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { size * 1.2 / 2, size * 3 },
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(),
        };
        arc.SetResourceReference(Shape.StrokeProperty, Theme.Keys.Secondary);
        arc.Loaded += (_, _) => arc.RenderTransform.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        return arc;
    }

    /// <summary>The rarity's raw value for <see cref="L.RarityLabel(string)"/>.</summary>
    public static string RarityLabel(this L l, Rarity r) => l.RarityLabel(r.RawValue());

    /// <summary>Shared by the Pokédex header and the catch-log header (most valuable first). Display order only.</summary>
    public static readonly Rarity[] RarityDisplayOrder = [Rarity.Legendary, Rarity.Rare, Rarity.Uncommon, Rarity.Common];
}

/// <summary>
/// Port of <c>PokemonNameClient</c> + <c>PokemonNameDisplayStore</c> (PokemonNameLocalization.swift /
/// PokemonNameLabel.swift): localized type, ability, move and form names fetched from PokéAPI on demand,
/// shared across labels, cached on disk for 30 days (same file layout as macOS: <c>pokemon-names-v1</c>).
/// Labels show "…" until their names arrive, then update in place.
/// </summary>
internal static class DexNames
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly Dictionary<PokemonNameResource, Dictionary<string, string>> Names = new();
    private static readonly HashSet<PokemonNameResource> Failed = [];
    private static readonly Dictionary<PokemonNameResource, Task<bool>> InFlight = new();
    private static readonly DateTime ReferenceDate = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Directory => System.IO.Path.Combine(AppEnv.StateDirectory(), "pokemon-names-v1");

    public static IReadOnlyDictionary<PokemonNameResource, Dictionary<string, string>> Loaded => Names;

    public static bool IsLoaded(PokemonNameResource r) => Names.ContainsKey(r);

    /// <summary>For <c>CompanionStore(formNameLoader:)</c>.</summary>
    public static async Task<Dictionary<string, string>?> Fetch(PokemonNameResource r) =>
        await Load(r) && Names.TryGetValue(r, out var n) ? n : null;

    /// <summary>Loads once; concurrent callers share the request. UI thread only.</summary>
    public static Task<bool> Load(PokemonNameResource r)
    {
        if (Names.ContainsKey(r)) return Task.FromResult(true);
        if (InFlight.TryGetValue(r, out var pending)) return pending;
        var task = LoadCore(r);
        if (!task.IsCompleted) InFlight[r] = task;
        return task;
    }

    private sealed record Snapshot(double fetchedAt, Dictionary<string, string> names);

    private static async Task<bool> LoadCore(PokemonNameResource r)
    {
        Snapshot? previous = null;
        try
        {
            if (!r.IsValid) throw new ArgumentException("invalid resource");
            var file = System.IO.Path.Combine(Directory, r.CacheKey + ".json");
            previous = await Task.Run(() =>
            {
                try { return System.IO.File.Exists(file) ? JsonSerializer.Deserialize<Snapshot>(System.IO.File.ReadAllBytes(file)) : null; }
                catch (Exception) { return null; }
            });
            var age = previous == null ? double.MaxValue : (DateTime.UtcNow - ReferenceDate).TotalSeconds - previous.fetchedAt;
            if (previous != null && age < 30 * 86400)
            {
                Names[r] = previous.names;
                return true;
            }
            await Gate.WaitAsync();
            Dictionary<string, string> names;
            try
            {
                var url = $"https://pokeapi.co/api/v2/{PokemonNameResource.RawValue(r.Kind)}/{r.Name}/";
                var bytes = await Http.GetByteArrayAsync(url);
                names = Parse(bytes, r.Kind == PokemonNameResource.ResourceKind.PokemonForm);
            }
            finally
            {
                Gate.Release();
            }
            Names[r] = names;
            Failed.Remove(r);
            var snapshot = new Snapshot((DateTime.UtcNow - ReferenceDate).TotalSeconds, names);
            _ = Task.Run(() =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    System.IO.File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(snapshot));
                }
                catch (Exception) { /* the memory copy still serves this session */ }
            });
            return true;
        }
        catch (Exception)
        {
            if (previous != null)
            {
                Names[r] = previous.names;
                return true;
            }
            Failed.Add(r);
            return false;
        }
        finally
        {
            InFlight.Remove(r);
        }
    }

    /// <summary><c>pokemon-form</c> keeps the bare form name ("Sky Forme") in <c>form_names</c>.</summary>
    private static Dictionary<string, string> Parse(byte[] json, bool form)
    {
        using var doc = JsonDocument.Parse(json);
        var key = form ? "form_names" : "names";
        var entries = new List<(string, string)>();
        if (doc.RootElement.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray())
                if (e.TryGetProperty("name", out var n) && e.TryGetProperty("language", out var lang)
                    && lang.TryGetProperty("name", out var code))
                    entries.Add((code.GetString() ?? "", n.GetString() ?? ""));
        return PokemonNameLocalization.Collect(entries);
    }

    public readonly record struct Item(PokemonNameResource Resource, string Suffix = "");

    public static Item Of(PokemonNameResource.ResourceKind kind, string name, string suffix = "") =>
        new(new PokemonNameResource(kind, name), suffix);

    /// <summary>Port of <c>PokemonNameText.text</c>.</summary>
    public static string Text(IEnumerable<Item> items, AppLanguage language) => string.Join(" · ", items.Select(item =>
    {
        if (!Names.TryGetValue(item.Resource, out var translations))
            return (Failed.Contains(item.Resource) ? PokemonNameLocalization.Identifier(item.Resource.Name) : "…") + item.Suffix;
        return (language.ResolveName(translations) ?? PokemonNameLocalization.Identifier(item.Resource.Name)) + item.Suffix;
    }));

    /// <summary>Port of <c>PokemonNameLabel</c>: a text that fills itself in once its names load.</summary>
    public static TextBlock Label(IReadOnlyList<Item> items, AppLanguage language, double size,
        string brushKey = Theme.Keys.Primary, FontWeight? weight = null, bool loadNow = true,
        Func<string, string>? transform = null)
    {
        transform ??= s => s;
        var t = Ui.Text(transform(Text(items, language)), size, brushKey, weight);
        if (loadNow) Request(t, items, language, transform);
        else t.Tag = new Action(() => Request(t, items, language, transform));
        return t;
    }

    public static void Request(TextBlock t, IReadOnlyList<Item> items, AppLanguage language, Func<string, string>? transform = null)
    {
        transform ??= s => s;
        var pending = items.Select(i => i.Resource).Distinct().Where(r => !Names.ContainsKey(r)).Select(Load).ToList();
        if (pending.Count == 0) return;
        _ = Update();
        async Task Update()
        {
            await Task.WhenAll(pending);
            t.Text = transform(Text(items, language));
        }
    }

    /// <summary>
    /// Rows of a long list (the complete move list) fetch their names only once scrolled near the viewport —
    /// the LazyVStack's lazy row creation in the original.
    /// </summary>
    public static void LoadWhenVisible(ScrollViewer viewer, IEnumerable<TextBlock> labels)
    {
        var pending = labels.ToList();
        void Check()
        {
            if (pending.Count == 0 || !viewer.IsLoaded) return;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var label = pending[i];
                if (!label.IsVisible) continue;
                double y;
                try { y = label.TranslatePoint(new Point(0, 0), viewer).Y; }
                catch (InvalidOperationException) { continue; }
                if (y > viewer.ViewportHeight + 120 || y + label.ActualHeight < -120) continue;
                (label.Tag as Action)?.Invoke();
                pending.RemoveAt(i);
            }
        }
        viewer.ScrollChanged += (_, _) => Check();
        viewer.Loaded += (_, _) => viewer.Dispatcher.BeginInvoke(Check, System.Windows.Threading.DispatcherPriority.Background);
    }
}

/// <summary>
/// SwiftUI <c>Text(date, style: .relative)</c>: the two largest units between a date and now in the short
/// unit style ("2 yrs, 10 mths", "2년 10개월"). Windows has no localized equivalent to borrow.
/// </summary>
internal static class DexRelativeTime
{
    private enum Unit { Year, Month, Week, Day, Hour, Minute, Second }

    public static string Format(DateTimeOffset date, DateTimeOffset now, AppLanguage language)
    {
        var (from, to) = date <= now ? (date, now) : (now, date);
        var parts = new List<(Unit, int)>();
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        if (from.AddMonths(months) > to) months--;
        var cursor = from.AddMonths(months);
        if (months >= 12) parts.Add((Unit.Year, months / 12));
        if (months % 12 > 0) parts.Add((Unit.Month, months % 12));
        var rest = to - cursor;
        var days = (int)rest.TotalDays;
        if (days >= 7) parts.Add((Unit.Week, days / 7));
        if (days % 7 > 0) parts.Add((Unit.Day, days % 7));
        if (rest.Hours > 0) parts.Add((Unit.Hour, rest.Hours));
        if (rest.Minutes > 0) parts.Add((Unit.Minute, rest.Minutes));
        if (rest.Seconds > 0 || parts.Count == 0) parts.Add((Unit.Second, rest.Seconds));
        var shown = parts.Take(2).Select(p => Name(p.Item1, p.Item2, language));
        return language switch
        {
            AppLanguage.Ko => string.Join(" ", shown),
            AppLanguage.Ja => string.Concat(shown),
            _ => string.Join(", ", shown),
        };
    }

    private static string Name(Unit unit, int n, AppLanguage language)
    {
        var one = n == 1;
        return language switch
        {
            AppLanguage.Ko => n + unit switch { Unit.Year => "년", Unit.Month => "개월", Unit.Week => "주", Unit.Day => "일", Unit.Hour => "시간", Unit.Minute => "분", _ => "초" },
            AppLanguage.Ja => n + unit switch { Unit.Year => "年", Unit.Month => "か月", Unit.Week => "週間", Unit.Day => "日", Unit.Hour => "時間", Unit.Minute => "分", _ => "秒" },
            AppLanguage.Es => $"{n} " + unit switch { Unit.Year => one ? "a" : "a", Unit.Month => "m", Unit.Week => "sem.", Unit.Day => "d", Unit.Hour => "h", Unit.Minute => "min", _ => "s" },
            AppLanguage.Fr => $"{n} " + unit switch { Unit.Year => one ? "an" : "ans", Unit.Month => "m.", Unit.Week => "sem.", Unit.Day => "j", Unit.Hour => "h", Unit.Minute => "min", _ => "s" },
            AppLanguage.Pt => $"{n} " + unit switch { Unit.Year => one ? "ano" : "anos", Unit.Month => one ? "mês" : "meses", Unit.Week => "sem.", Unit.Day => one ? "dia" : "dias", Unit.Hour => "h", Unit.Minute => "min", _ => "s" },
            AppLanguage.De => $"{n} " + unit switch { Unit.Year => "J.", Unit.Month => "M.", Unit.Week => "W.", Unit.Day => one ? "Tag" : "Tg.", Unit.Hour => "Std.", Unit.Minute => "Min.", _ => "Sek." },
            AppLanguage.Ru => $"{n} " + unit switch { Unit.Year => "г.", Unit.Month => "мес.", Unit.Week => "нед.", Unit.Day => "дн.", Unit.Hour => "ч", Unit.Minute => "мин", _ => "с" },
            _ => $"{n} " + unit switch { Unit.Year => one ? "yr" : "yrs", Unit.Month => one ? "mth" : "mths", Unit.Week => one ? "wk" : "wks", Unit.Day => one ? "day" : "days", Unit.Hour => "hr", Unit.Minute => "min", _ => "sec" },
        };
    }

    /// <summary><c>Date.FormatStyle(date: .numeric)</c> in the app language ("10/15/2026", "2026/10/15").</summary>
    public static string NumericDate(DateTimeOffset date, AppLanguage language)
    {
        if (language == AppLanguage.Ko) return date.ToString("yyyy. M. d.", CultureInfo.InvariantCulture);
        try
        {
            var culture = CultureInfo.GetCultureInfo(language.RawValue());
            return date.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
        }
        catch (CultureNotFoundException)
        {
            return date.ToString("d", CultureInfo.InvariantCulture);
        }
    }
}
