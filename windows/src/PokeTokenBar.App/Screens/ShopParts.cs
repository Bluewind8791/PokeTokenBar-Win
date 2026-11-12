using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>Pieces shared by the Shop and Bag tabs (ShopView.swift / BagView.swift / ItemIconView).</summary>
public static class ShopUi
{
    /// <summary>
    /// Item / wallet card: <c>.padding(10).background(Color.secondary.opacity(0.06)).clipShape(RoundedRectangle(10))</c>.
    /// The fill is the secondary brush at 6 % on its own layer so it follows the theme like the original.
    /// </summary>
    public static FrameworkElement Card(UIElement content)
    {
        var fill = new Border { CornerRadius = new CornerRadius(10), Opacity = 0.06 };
        fill.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Secondary);
        var grid = new Grid();
        grid.Children.Add(fill);
        grid.Children.Add(new Border { Child = content, Padding = new Thickness(10) });
        return grid;
    }

    /// <summary>
    /// A row whose first element takes the leftover width (and truncates there) and whose trailing
    /// elements keep their natural size — SwiftUI's <c>HStack { Text; Spacer(); Button… }</c>, where the
    /// text gives way before the buttons do.
    /// </summary>
    public static FrameworkElement Row(UIElement? leading, double spacing, params UIElement?[] trailing)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (leading != null) grid.Children.Add(leading);
        var col = 1;
        foreach (var t in trailing)
        {
            if (t == null) continue;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (t is FrameworkElement fe)
            {
                fe.Margin = new Thickness(spacing, 0, 0, 0);
                if (fe.ReadLocalValue(FrameworkElement.VerticalAlignmentProperty) == DependencyProperty.UnsetValue)
                    fe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(t, col++);
            grid.Children.Add(t);
        }
        return grid;
    }

    /// <summary><c>HStack(alignment: .top, spacing: 10) { icon; VStack {…} }</c> — the info column takes the rest.</summary>
    public static FrameworkElement IconHeader(FrameworkElement icon, FrameworkElement info)
    {
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 0, 10, 0);
        info.VerticalAlignment = VerticalAlignment.Top;
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(info);
        return dock;
    }

    /// <summary>
    /// Port of <c>ItemIconView</c>: the PokéAPI item sprite (fitted, nearest-neighbour) once loaded;
    /// the emoji before that, when there is no sprite, or when the load fails. A sprite already in the
    /// cache shows synchronously so a rebuild does not flash the emoji.
    /// </summary>
    public static FrameworkElement ItemIcon(ItemKind kind, double size = 30)
    {
        var box = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Top };
        var emoji = EmojiImages.View(kind.FallbackEmoji(), size);
        var image = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        box.Children.Add(emoji);
        box.Children.Add(image);
        if (kind.SpriteName() is not { } name) return box;

        void Apply(byte[]? data)
        {
            if (SpriteImages.Decode(data) is not { } bmp) return;
            var src = SpriteImages.ToBitmapSource(bmp);
            // External sprites are not guaranteed square: fit (aspect kept) like SpriteFit.
            var scale = Math.Min(size / src.PixelWidth, size / src.PixelHeight);
            image.Width = src.PixelWidth * scale;
            image.Height = src.PixelHeight * scale;
            image.Source = src;
            emoji.Visibility = Visibility.Collapsed;
        }

        var load = SpriteStore.Shared.ItemData(name);
        if (load.IsCompleted) Apply(load.Result);
        else load.ContinueWith(t => Apply(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
        return box;
    }

    /// <summary>
    /// macOS <c>Stepper</c> with its label (<c>Stepper(value:in:) { Text("×N") }</c>): the label, then the
    /// small rounded up/down control. Steps are clamped to <paramref name="min"/>…<paramref name="max"/>.
    /// </summary>
    public static FrameworkElement Stepper(int value, int min, int max, Action<int> onChange, string accessibleName)
    {
        var label = Ui.Text($"×{value}", Theme.FontSize.Callout, Theme.Keys.Primary, FontWeights.SemiBold, monospacedDigits: true);

        FrameworkElement Half(bool up)
        {
            // Chevrons drawn as strokes: the Segoe glyphs are heavier than AppKit's stepper arrows.
            var chevron = new Path
            {
                Data = Geometry.Parse(up ? "M0,4.5 L5.25,0 L10.5,4.5" : "M0,0 L5.25,4.5 L10.5,0"),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chevron.SetResourceReference(Shape.StrokeProperty, Theme.Keys.Secondary);
            var hit = new Border { Child = chevron, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var next = Math.Clamp(value + (up ? 1 : -1), min, max);
            hit.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (next != value) onChange(next);
            };
            return hit;
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var up = Half(true);
        var down = Half(false);
        Grid.SetRow(down, 1);
        var divider = new Border { Height = 1, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
        divider.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Separator);
        Grid.SetRowSpan(divider, 2);
        grid.Children.Add(divider);
        grid.Children.Add(up);
        grid.Children.Add(down);
        var control = new Border { Child = grid, Width = 20, Height = 26, CornerRadius = new CornerRadius(6) };
        control.SetResourceReference(Border.BackgroundProperty, Theme.Keys.ControlBackground);
        System.Windows.Automation.AutomationProperties.SetName(control, accessibleName);
        System.Windows.Automation.AutomationProperties.SetHelpText(control, value.ToString());
        // Keyboard: arrow keys step like the AppKit stepper.
        control.Focusable = true;
        control.KeyDown += (_, e) =>
        {
            var next = e.Key switch { Key.Up => value + 1, Key.Down => value - 1, _ => value };
            next = Math.Clamp(next, min, max);
            if (next != value) { e.Handled = true; onChange(next); }
        };
        return Ui.HStack(9, label, control);
    }

    /// <summary><c>.buttonStyle(.borderless).controlSize(.small)</c>: accent-tinted text, no chrome.</summary>
    public static FrameworkElement Borderless(string title, Action action)
    {
        var text = Ui.Text(title, Theme.FontSize.Subheadline, Theme.Keys.Accent);
        text.TextTrimming = TextTrimming.None;
        return new Border { Child = text, Padding = new Thickness(2, 2, 2, 3) }.OnClick(action);
    }

    /// <summary>Small bordered/prominent push button that never truncates its title.</summary>
    public static Button Button(string title, Action action, bool prominent = false, bool enabled = true)
    {
        var b = Buttons.Small(title, action, prominent);
        b.IsEnabled = enabled;
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    /// <summary><c>checkmark.seal.fill</c> + green caption — "owned" / passive-effect line.</summary>
    public static FrameworkElement SealLine(string text, double spacing)
    {
        var icon = Ui.Icon("", Theme.FontSize.Caption2, Theme.Keys.Green);   // CompletedSolid ≈ checkmark.seal.fill
        var label = Ui.Text(text, Theme.FontSize.Caption2, Theme.Keys.Green, FontWeights.SemiBold);
        return Ui.HStack(spacing, icon, label, Ui.Spacer());
    }

    /// <summary>Caption text that wraps (<c>.fixedSize(horizontal: false, vertical: true)</c>).</summary>
    public static TextBlock Wrapped(string text, double size, string brushKey, FontWeight? weight = null)
    {
        var t = Ui.Text(text, size, brushKey, weight).Wrapping();
        t.HorizontalAlignment = HorizontalAlignment.Left;
        return t;
    }

    /// <summary>Text limited to <paramref name="lines"/> lines, truncating after that (<c>.lineLimit(n)</c>).</summary>
    public static TextBlock Lines(string text, double size, string brushKey, int lines, FontWeight? weight = null)
    {
        var t = Ui.Text(text, size, brushKey, weight);
        if (lines > 1)
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.MaxHeight = Math.Ceiling(size * Theme.UiFont.LineSpacing * lines);
        }
        return t;
    }
}

/// <summary>
/// Color emoji for the item fallbacks. WPF draws emoji glyphs in one color (no COLR/CPAL support), so the
/// macOS look — Apple's color emoji — is approximated with Microsoft's Fluent Emoji 3D artwork, fetched
/// once and cached beside the sprites. Until it loads (or offline) the glyph shows, tinted secondary so it
/// stays visible on both themes.
/// </summary>
public static class EmojiImages
{
    private const string Base = "https://raw.githubusercontent.com/microsoft/fluentui-emoji/main/assets";
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> Memory = new();

    /// <summary>Fluent Emoji asset folder per fallback emoji (CompanionModel <c>FallbackEmoji</c>) and per Pokédex
    /// completion medal (<c>RegionTally</c>, <c>DexBadgeMedal</c>), and the floating egg's prayer particle.</summary>
    private static readonly Dictionary<string, string> Assets = new()
    {
        ["🌿"] = "Herb",
        ["🍬"] = "Candy",
        ["✨"] = "Sparkles",
        ["🏅"] = "Sports medal",
        ["🔤"] = "Input latin letters",
        ["🌍"] = "Globe showing europe-africa",
        ["🙏"] = "Folded hands",
    };

    /// <summary>Assets with skin tones keep each tone in its own folder; the default (yellow) one is used.</summary>
    private static readonly HashSet<string> SkinToneAssets = ["Folded hands"];

    public static FrameworkElement View(string emoji, double size)
    {
        var box = new Grid { Width = size, Height = size };
        var glyph = Ui.Text(emoji, size * 0.8, Theme.Keys.Secondary);
        glyph.FontFamily = new FontFamily("Segoe UI Emoji");
        glyph.TextTrimming = TextTrimming.None;
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        box.Children.Add(glyph);
        box.Children.Add(image);
        if (!Assets.TryGetValue(emoji, out var asset)) return box;

        void Apply(System.Windows.Media.Imaging.BitmapSource? src)
        {
            if (src == null) return;
            image.Source = src;
            glyph.Visibility = Visibility.Collapsed;
        }

        if (Memory.TryGetValue(asset, out var hit)) { Apply(hit); return box; }
        _ = Load(asset).ContinueWith(t => Apply(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
        return box;
    }

    private static async Task<System.Windows.Media.Imaging.BitmapSource?> Load(string asset)
    {
        var slug = asset.ToLowerInvariant().Replace(' ', '_');
        var file = SpriteStore.Shared.FilePath($"emoji-{slug}.png");
        byte[]? bytes = null;
        try
        {
            if (System.IO.File.Exists(file)) bytes = await System.IO.File.ReadAllBytesAsync(file);
            else
            {
                bytes = await Http.GetByteArrayAsync(SkinToneAssets.Contains(asset)
                    ? $"{Base}/{Uri.EscapeDataString(asset)}/Default/3D/{slug}_3d_default.png"
                    : $"{Base}/{Uri.EscapeDataString(asset)}/3D/{slug}_3d.png");
                // Atomic write (unique temp name: two cards may fetch the same emoji at once).
                var tmp = $"{file}.{Guid.NewGuid():N}.tmp";
                await System.IO.File.WriteAllBytesAsync(tmp, bytes);
                System.IO.File.Move(tmp, file, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException or UnauthorizedAccessException)
        {
            if (bytes == null) return null;
        }
        if (SpriteImages.Decode(bytes) is not { } bmp) return null;
        // Apple's emoji fill the em box; the Fluent art has a wide margin, so crop to the drawing.
        var src = SpriteImages.ToBitmapSource(SpriteImages.CropToContent(bmp));
        Memory[asset] = src;
        return src;
    }
}

/// <summary>
/// A card that can rebuild itself for local view state (<c>@State</c> such as "confirming" or the
/// stepper value) without waiting for a store change. Store changes still rebuild the whole tab through
/// <see cref="Live"/>; the state itself lives in <see cref="ScreenState{T}"/>, so it survives both.
/// </summary>
public sealed class CardHost : ContentControl
{
    private readonly Func<CardHost, FrameworkElement> _build;

    public CardHost(Func<CardHost, FrameworkElement> build)
    {
        _build = build;
        Focusable = false;
        Content = build(this);
    }

    public void Refresh() => Content = _build(this);
}

/// <summary>
/// Per-card <c>@State</c> for a tab. SwiftUI drops a view's state when it leaves the hierarchy — here,
/// when navigation moves off the tab or Settings/Recap cover it (also on popover reset, which goes Home).
/// </summary>
public sealed class ScreenState<T> where T : new()
{
    private static readonly ConditionalWeakTable<PopoverNavigation, ScreenState<T>> Table = new();
    private readonly Dictionary<string, T> _cards = new();

    public static ScreenState<T> For(PopoverNavigation nav, PopoverTab tab) =>
        Table.GetValue(nav, n =>
        {
            var state = new ScreenState<T>();
            n.Changed += () =>
            {
                if (n.Tab != tab || n.ShowSettings || n.ShowingRecap) state._cards.Clear();
            };
            return state;
        });

    public T this[string key] => _cards.TryGetValue(key, out var v) ? v : _cards[key] = new T();
}
