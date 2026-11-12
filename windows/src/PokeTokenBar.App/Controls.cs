using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>SwiftUI <c>Capsule()</c>: a WPF corner radius above half the height distorts, so track it.</summary>
public static class Capsule
{
    public static T Round<T>(T border) where T : Border
    {
        border.SizeChanged += (_, _) => border.CornerRadius = new CornerRadius(border.ActualHeight / 2);
        return border;
    }
}

/// <summary>Port of <c>PokemonType.hex</c> (Badge.swift).</summary>
public static class PokemonTypeColors
{
    private static readonly Dictionary<string, uint> Hex = new()
    {
        ["normal"] = 0xAAAA99, ["fire"] = 0xFF4422, ["water"] = 0x3399FF, ["electric"] = 0xFFCC33,
        ["grass"] = 0x77CC55, ["ice"] = 0x66CCFF, ["fighting"] = 0xBB5544, ["poison"] = 0xAA5599,
        ["ground"] = 0xDDBB55, ["flying"] = 0x8899FF, ["psychic"] = 0xFF5599, ["bug"] = 0xAABB22,
        ["rock"] = 0xBBAA66, ["ghost"] = 0x6666BB, ["dragon"] = 0x7766EE, ["dark"] = 0x775544,
        ["steel"] = 0xAAAABB, ["fairy"] = 0xEE99EE,
    };

    public static Brush? Brush(string apiName)
    {
        if (!Hex.TryGetValue(apiName.ToLowerInvariant(), out var hex)) return null;
        var b = new SolidColorBrush(Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex));
        b.Freeze();
        return b;
    }

    /// <summary>Swift <c>rarityColor</c>.</summary>
    public static string RarityKey(Rarity? r) => r switch
    {
        Rarity.Uncommon => Theme.Keys.Green,
        Rarity.Rare => Theme.Keys.Blue,
        Rarity.Legendary => Theme.Keys.Orange,
        _ => Theme.Keys.Gray,
    };
}

/// <summary>Port of <c>Badge</c> (Badge.swift): bold capsule label, solid or tinted.</summary>
public static class Badge
{
    public static Border Make(string text, Brush tint, bool solid = true, double size = 8, bool typeShadow = false,
        double hPad = 5, double vPad = 1)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = size + 1,   // SF 8pt bold reads like Segoe 9
            FontWeight = FontWeights.Bold,
            FontFamily = Theme.UiFont,
            Foreground = solid ? Brushes.White : tint,
        };
        if (typeShadow)
            label.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, Opacity = 0.35, BlurRadius = 1, ShadowDepth = 0.5, Direction = 270 };
        Brush bg = tint;
        if (!solid && tint is SolidColorBrush sc)
        {
            bg = new SolidColorBrush(Color.FromArgb(0x24, sc.Color.R, sc.Color.G, sc.Color.B));
            bg.Freeze();
        }
        return Capsule.Round(new Border
        {
            Child = label,
            Background = bg,
            Padding = new Thickness(hPad, vPad, hPad, vPad),
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    public static Border Keyed(string text, string tintKey, bool solid = true, double size = 8) =>
        Make(text, Theme.Brush(tintKey), solid, size);

    /// <summary>Port of <c>TypeBadges</c>.</summary>
    public static Stack Types(IEnumerable<(string ApiName, string Label)> types, double size = 8)
    {
        var row = Ui.HStack(5);
        foreach (var (api, label) in types)
            row.Children.Add(Make(label.ToUpperInvariant(), PokemonTypeColors.Brush(api) ?? Theme.Brush(Theme.Keys.Gray),
                size: size, typeShadow: true));
        return row;
    }
}

/// <summary>
/// Port of <c>LabeledProgressBar</c> (CompanionView.swift): a thin orange capsule that grows on
/// hover (after 300 ms) to show its label.
/// </summary>
public sealed class LabeledProgressBar : Grid
{
    private const double Collapsed = 6, Expanded = 12;
    private readonly Border _track = Capsule.Round(new Border { ClipToBounds = true });
    private readonly Border _fill = Capsule.Round(new Border { HorizontalAlignment = HorizontalAlignment.Left });
    private readonly TextBlock _label;
    private readonly DispatcherTimer _hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private double _value;

    public LabeledProgressBar(double value, string label)
    {
        Height = Expanded;
        _value = Math.Clamp(value, 0, 1);
        _track.SetResourceReference(Border.BackgroundProperty, Theme.Keys.FillStrong);
        _fill.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Orange);
        _label = Ui.Text(label, 9, Theme.Keys.Primary, FontWeights.SemiBold, monospacedDigits: true);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.Opacity = 0;
        var inner = new Grid();
        inner.Children.Add(_fill);
        inner.Children.Add(_label);
        _track.Child = inner;
        _track.Height = Collapsed;
        _track.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_track);
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => _fill.Width = ActualWidth * _value;
        _hoverDelay.Tick += (_, _) => { _hoverDelay.Stop(); Animate(true); };
        MouseEnter += (_, _) => _hoverDelay.Start();
        MouseLeave += (_, _) => { _hoverDelay.Stop(); Animate(false); };
        System.Windows.Automation.AutomationProperties.SetHelpText(this, label);
    }

    private void Animate(bool expand)
    {
        _track.BeginAnimation(HeightProperty, new DoubleAnimation(expand ? Expanded : Collapsed,
            TimeSpan.FromMilliseconds(expand ? 300 : 180)) { EasingFunction = expand ? new BackEase { Amplitude = 0.3 } : new QuadraticEase() });
        _label.BeginAnimation(OpacityProperty, new DoubleAnimation(expand ? 1 : 0, TimeSpan.FromMilliseconds(expand ? 120 : 60))
            { BeginTime = expand ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero });
    }
}

/// <summary>
/// Port of <c>LimitProgressBar</c> (PopoverView.swift): a 6 px gauge tinted by pace tier, with a
/// marker at the pace position. <paramref name="displayPercent"/> is already converted for the
/// used/remaining display mode; <paramref name="markerFraction"/> likewise.
/// </summary>
public sealed class LimitProgressBar : Grid
{
    private const double Track = 6, MarkerWidth = 2.5, MarkerHeight = Track + 4;

    public LimitProgressBar(double displayPercent, string tintKey, double? markerFraction)
    {
        Height = MarkerHeight;
        var track = new Border { Height = Track, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, Theme.Keys.FillStrong);
        var fill = new Border { Height = Track, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        fill.SetResourceReference(Border.BackgroundProperty, tintKey);
        Children.Add(track);
        Children.Add(fill);
        Rectangle? marker = null;
        if (markerFraction is { } f)
        {
            marker = new Rectangle { Width = MarkerWidth, Height = MarkerHeight, RadiusX = 1.25, RadiusY = 1.25, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.55, IsHitTestVisible = false };
            marker.SetResourceReference(Shape.FillProperty, Theme.Keys.Primary);
            Children.Add(marker);
        }
        SizeChanged += (_, _) =>
        {
            fill.Width = ActualWidth * Math.Clamp(displayPercent, 0, 100) / 100;
            if (marker != null && markerFraction is { } mf)
                marker.Margin = new Thickness((ActualWidth - MarkerWidth) * Math.Clamp(mf, 0, 1), 0, 0, 0);
        };
    }
}

/// <summary>Port of <c>CapsuleTabBar</c> (PopoverView.swift): pill buttons, the selected one accent-tinted.</summary>
public static class CapsuleTabBar
{
    public sealed record Item(string Id, string Title, string? Help = null, bool Marked = false);

    public static FrameworkElement Make(IReadOnlyList<Item> items, string? selectedId, Action<string> onSelect)
    {
        var row = Ui.HStack(6);
        foreach (var item in items)
        {
            var selected = item.Id == selectedId;
            var text = Ui.Text(item.Title, Theme.FontSize.Caption, selected ? Theme.Keys.Accent : Theme.Keys.Secondary,
                selected ? FontWeights.SemiBold : FontWeights.Normal);
            text.TextTrimming = TextTrimming.None;
            UIElement content = text;
            if (item.Marked)
            {
                var dot = new Ellipse { Width = 5, Height = 5 };
                dot.SetResourceReference(Shape.FillProperty, selected ? Theme.Keys.Accent : Theme.Keys.Secondary);
                content = Ui.HStack(4, dot, text);
            }
            var pill = Capsule.Round(new Border { Child = content, Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand });
            pill.SetResourceReference(Border.BackgroundProperty, selected ? Theme.Keys.AccentSoft : Theme.Keys.Fill);
            pill.MouseLeftButtonUp += (_, _) => onSelect(item.Id);
            row.Children.Add(pill.Help(item.Help));
        }
        return new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }
}

/// <summary>macOS segmented <c>Picker</c> (.segmented): equal-width segments in a rounded track.</summary>
public static class SegmentedControl
{
    public static FrameworkElement Make(IReadOnlyList<(string Id, string Title)> items, string selectedId, Action<string> onSelect)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        foreach (var (id, title) in items)
        {
            var selected = id == selectedId;
            var label = Ui.Text(title, Theme.FontSize.Body, Theme.Keys.Primary, selected ? FontWeights.SemiBold : FontWeights.Normal);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var seg = new Border { Child = label, CornerRadius = new CornerRadius(5), Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(1), Cursor = Cursors.Hand };
            if (selected) seg.SetResourceReference(Border.BackgroundProperty, Theme.Keys.ControlSelected);
            else seg.Background = Brushes.Transparent;
            seg.MouseLeftButtonUp += (_, _) => onSelect(id);
            grid.Children.Add(seg);
        }
        var track = new Border { Child = grid, CornerRadius = new CornerRadius(6), Padding = new Thickness(1) };
        track.SetResourceReference(Border.BackgroundProperty, Theme.Keys.ControlBackground);
        return track;
    }
}

/// <summary>Button styles the ported views use.</summary>
public static class Buttons
{
    /// <summary>A borderless clickable element (SwiftUI <c>.buttonStyle(.plain)</c>).</summary>
    public static T OnClick<T>(this T element, Action action) where T : FrameworkElement
    {
        element.Cursor = Cursors.Hand;
        // Transparent hit area so the gaps between children are clickable too.
        if (element is Panel { Background: null } p) p.Background = Brushes.Transparent;
        if (element is Border { Background: null } b) b.Background = Brushes.Transparent;
        element.MouseLeftButtonUp += (_, e) => { e.Handled = true; action(); };
        return element;
    }

    /// <summary>Icon-only toolbar button (footer refresh / settings / quit).</summary>
    public static FrameworkElement Icon(string glyph, string? help, Action action, double size = 15)
    {
        var icon = Ui.Icon(glyph, size, Theme.Keys.Secondary);
        var hit = new Border { Child = icon, Padding = new Thickness(3), Background = Brushes.Transparent, CornerRadius = new CornerRadius(4) };
        hit.MouseEnter += (_, _) => hit.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Fill);
        hit.MouseLeave += (_, _) => hit.Background = Brushes.Transparent;
        return hit.OnClick(action).Help(help);
    }

    /// <summary>A small bordered push button (SwiftUI <c>.controlSize(.small)</c> default style) or prominent.</summary>
    public static Button Small(string title, Action action, bool prominent = false, double fontSize = Theme.FontSize.Subheadline)
    {
        var b = new Button
        {
            Content = title,
            FontSize = fontSize,
            FontFamily = Theme.UiFont,
            Padding = new Thickness(10, 2, 10, 3),
            Cursor = Cursors.Hand,
            Template = PillTemplate(),
        };
        b.SetResourceReference(Control.BackgroundProperty, prominent ? Theme.Keys.Accent : Theme.Keys.ControlBackground);
        if (prominent) b.Foreground = Brushes.White;
        else b.SetResourceReference(Control.ForegroundProperty, Theme.Keys.Primary);
        b.Click += (_, _) => action();
        return b;
    }

    private static ControlTemplate? _pill;

    private static ControlTemplate PillTemplate()
    {
        if (_pill != null) return _pill;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        _pill = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        _pill.Triggers.Add(disabled);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.75));
        _pill.Triggers.Add(pressed);
        _pill.Seal();
        return _pill;
    }
}

/// <summary>
/// Port of <c>SpriteView</c> (CompanionView.swift): shows a species sprite fitted into a square
/// (aspect kept, nearest-neighbour), animated GIF when requested, egg when <c>speciesID</c> is null;
/// cached pixels show synchronously, then the exact variant loads; non-animated views can bob.
/// <c>back</c> shows the back sprite (the companion turned away); a missing back sprite falls back to the front one.
/// </summary>
public sealed class SpriteView : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _eggGlyph;
    private readonly DispatcherTimer _frameTimer = new();
    private readonly SpriteStore _store;
    private List<(BitmapSource Image, TimeSpan Delay)> _frames = [];
    private int _frameIndex;
    private int _generation;
    private readonly bool _bob;
    private readonly double _size;

    /// <summary>Distance from the box top to the sprite's visible top (<see cref="SpriteVisibleTop"/>), one anchor for
    /// the whole animation; null until an image shows.</summary>
    public double? VisibleTop { get; private set; }
    /// <summary>Raised when <see cref="VisibleTop"/> changes (a new image or animation loaded).</summary>
    public event Action? VisibleTopChanged;

    public SpriteView(int? speciesID, double size = 84, bool bob = false, bool animated = false, bool shiny = false,
        PokemonForm? form = null, TimeSpan? minFrameDelay = null, SpriteStore? store = null, bool back = false)
    {
        _store = store ?? SpriteStore.Shared;
        _bob = bob;
        _size = size;
        Width = size;
        Height = size;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        _eggGlyph = new TextBlock { Text = "🥚", FontSize = size * 0.62, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        Children.Add(_image);
        Children.Add(_eggGlyph);
        _frameTimer.Tick += (_, _) => NextFrame();
        Unloaded += (_, _) => _frameTimer.Stop();
        Loaded += (_, _) => { if (_frames.Count > 1) _frameTimer.Start(); };
        _ = Load(speciesID, animated, shiny, form, minFrameDelay ?? TimeSpan.Zero, back);
    }

    private async Task Load(int? speciesID, bool animated, bool shiny, PokemonForm? form, TimeSpan floor, bool back)
    {
        var gen = ++_generation;
        if (speciesID is not { } id)
        {
            var egg = SpriteImages.Decode(await _store.EggData());
            if (gen != _generation) return;
            if (egg != null) Show(SpriteImages.ToBitmapSource(SpriteImages.CropToContent(egg)));
            else ShowEggGlyph();
            UpdateVisibleTop();
            return;
        }
        var resolved = PokemonForm.ResolvedForSpecies(id, form);
        if (animated)
        {
            foreach (var (facingBack, variant) in SpriteStore.Variants(shiny, back))
            {
                var gif = await _store.Data(id, animated: true, shiny: variant, form: resolved, back: facingBack);
                if (gen != _generation) return;
                if (gif == null) continue;
                var decoded = GifDecoder.CapFrameRate(GifDecoder.Frames(gif), floor);
                if (decoded.Count > 1)
                {
                    _frames = decoded.Select(f => (SpriteImages.ToBitmapSource(f.Image), f.Delay)).ToList();
                    _frameIndex = 0;
                    Show(_frames[0].Image);
                    UpdateVisibleTop();
                    _frameTimer.Interval = _frames[0].Delay;
                    if (IsLoaded) _frameTimer.Start();
                    return;
                }
            }
        }
        foreach (var (facingBack, variant) in SpriteStore.Variants(shiny, back))
        {
            var png = SpriteImages.Decode(await _store.Data(id, animated: false, shiny: variant, form: resolved, back: facingBack));
            if (gen != _generation) return;
            if (png == null) continue;
            Show(SpriteImages.ToBitmapSource(animated ? SpriteImages.CropToContent(png) : png));
            UpdateVisibleTop();
            StartBob();
            return;
        }
        ShowEggGlyph();
        UpdateVisibleTop();
    }

    /// <summary>One anchor for the whole animation (<see cref="SpriteVisibleTop.AnchorFraction"/>), so it does not jitter per frame.</summary>
    private void UpdateVisibleTop()
    {
        var images = _frames.Count > 0 ? _frames.Select(f => f.Image).ToList()
            : _image.Source is BitmapSource still ? [still] : new List<BitmapSource>();
        double? top = images.Count == 0 ? null
            : SpriteVisibleTop.Inset(_size, images[0].PixelWidth, images[0].PixelHeight,
                SpriteVisibleTop.AnchorFraction(images.Select(SpriteVisibleTop.OpaqueTopFraction).ToList()));
        if (top == VisibleTop) return;
        VisibleTop = top;
        VisibleTopChanged?.Invoke();
    }

    private void Show(BitmapSource image)
    {
        _eggGlyph.Visibility = Visibility.Collapsed;
        _image.Source = image;
        // Fit (aspect kept) into the size×size box; never upscale past the box.
        var scale = Math.Min(_size / image.PixelWidth, _size / image.PixelHeight);
        _image.Width = image.PixelWidth * scale;
        _image.Height = image.PixelHeight * scale;
    }

    private void ShowEggGlyph()
    {
        _image.Source = null;
        _eggGlyph.Visibility = Visibility.Visible;
        StartBob();
    }

    private void StartBob()
    {
        if (!_bob) return;
        var t = new TranslateTransform();
        RenderTransform = t;
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -3, TimeSpan.FromSeconds(0.9))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    private void NextFrame()
    {
        if (_frames.Count < 2) { _frameTimer.Stop(); return; }
        _frameIndex = (_frameIndex + 1) % _frames.Count;
        Show(_frames[_frameIndex].Image);
        _frameTimer.Interval = _frames[_frameIndex].Delay;
    }
}
