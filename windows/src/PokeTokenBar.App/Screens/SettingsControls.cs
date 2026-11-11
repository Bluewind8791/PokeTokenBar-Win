using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PokeTokenBar.App;

/// <summary>
/// Colors of the AppKit controls Settings and the recap draw (inset-grouped card, switch, slider,
/// pop-up button, text field, push button), measured from the macOS screenshots in <c>assets/</c>.
/// Published as application resources like <see cref="Theme"/>, and re-applied on a theme switch.
/// The "on" switch and the slider fill use the accent color: the macOS references were captured
/// with the popover inactive (gray), but the Windows popover closes as soon as it deactivates, so
/// it only ever shows the active look.
/// </summary>
public static class MacTokens
{
    public const string Card = "Mac.Card";                 // NSColor.controlBackgroundColor
    public const string CardStroke = "Mac.CardStroke";     // separatorColor.opacity(0.6)
    public const string PopUp = "Mac.PopUp";
    public const string PopUpStroke = "Mac.PopUpStroke";
    public const string SwitchOff = "Mac.SwitchOff";
    public const string Knob = "Mac.Knob";
    public const string SliderTrack = "Mac.SliderTrack";
    public const string Field = "Mac.Field";
    public const string FieldStroke = "Mac.FieldStroke";
    public const string Button = "Mac.Button";
    public const string ButtonStroke = "Mac.ButtonStroke";
    public const string SegmentTrack = "Mac.SegmentTrack";
    public const string SegmentSelected = "Mac.SegmentSelected";
    public const string SegmentDivider = "Mac.SegmentDivider";

    private static bool? _installed;
    private static bool _hooked;

    /// <summary>Installs the brushes for the current appearance (cheap when already current).</summary>
    public static void Ensure()
    {
        var app = Application.Current;
        if (app == null) return;
        if (!_hooked)
        {
            _hooked = true;
            Theme.Changed += () => Install(app.Resources);
        }
        if (_installed != Theme.IsDark) Install(app.Resources);
    }

    private static void Install(ResourceDictionary resources)
    {
        var dark = Theme.IsDark;
        _installed = dark;
        Color C(uint rgb, byte a = 255) => Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var map = new Dictionary<string, Color>
        {
            [Card] = dark ? C(0x1E1E1E) : C(0xFFFFFF),
            [CardStroke] = dark ? C(0xFFFFFF, 0x12) : C(0x000000, 0x10),
            [PopUp] = dark ? C(0x333333) : C(0xFFFFFF),
            [PopUpStroke] = dark ? C(0xFFFFFF, 0x0C) : C(0x000000, 0x24),
            [SwitchOff] = dark ? C(0x2E2E2E) : C(0xE3E3E3),
            [Knob] = dark ? C(0xDFDFDF) : C(0xFFFFFF),
            [SliderTrack] = dark ? C(0x343434) : C(0xDEDEDE),
            [Field] = dark ? C(0xFFFFFF, 0x0A) : C(0xFFFFFF),
            [FieldStroke] = dark ? C(0xFFFFFF, 0x26) : C(0x000000, 0x26),
            [Button] = dark ? C(0x575757) : C(0xFFFFFF),
            [ButtonStroke] = dark ? C(0xFFFFFF, 0x10) : C(0x000000, 0x24),
            [SegmentTrack] = dark ? C(0xFFFFFF, 0x12) : C(0x000000, 0x0E),
            [SegmentSelected] = dark ? C(0x5C5C5C) : C(0xFFFFFF),
            [SegmentDivider] = dark ? C(0xFFFFFF, 0x26) : C(0x000000, 0x24),
        };
        foreach (var (key, color) in map)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
    }

    public static T Bg<T>(this T border, string key) where T : Border
    {
        border.SetResourceReference(Border.BackgroundProperty, key);
        return border;
    }

    /// <summary>The soft drop shadow light-mode AppKit bezels carry.</summary>
    public static Effect? BezelShadow() =>
        Theme.IsDark ? null : new DropShadowEffect { BlurRadius = 2, ShadowDepth = 0.5, Direction = 270, Opacity = 0.18, Color = Colors.Black };
}

/// <summary>SwiftUI <c>Toggle</c> with <c>.toggleStyle(.switch).controlSize(.small)</c>: a wide pill with a pill knob.</summary>
public sealed class MacSwitch : Grid
{
    private const double TrackWidth = 44, TrackHeight = 19, KnobWidth = 25, KnobHeight = 15, Inset = 2;
    private readonly Border _track = new() { CornerRadius = new CornerRadius(TrackHeight / 2) };
    private readonly Border _knob = new()
    {
        Width = KnobWidth, Height = KnobHeight, CornerRadius = new CornerRadius(KnobHeight / 2),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Action<bool> _onChange;

    public bool IsOn { get; private set; }

    public MacSwitch(bool isOn, Action<bool> onChange, string? accessibleName = null, bool enabled = true)
    {
        _onChange = onChange;
        Width = TrackWidth;
        Height = TrackHeight;
        VerticalAlignment = VerticalAlignment.Center;
        _knob.Bg(MacTokens.Knob);
        _knob.Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 0.5, Direction = 270, Opacity = Theme.IsDark ? 0.35 : 0.3 };
        Children.Add(_track);
        Children.Add(_knob);
        Focusable = true;
        Cursor = Cursors.Hand;
        IsEnabled = enabled;
        if (!enabled) Opacity = 0.55;
        if (accessibleName != null) System.Windows.Automation.AutomationProperties.SetName(this, accessibleName);
        Apply(isOn, animate: false);
        MouseLeftButtonUp += (_, e) => { e.Handled = true; Toggle(); };
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter) { e.Handled = true; Toggle(); }
        };
    }

    private void Toggle()
    {
        Apply(!IsOn, animate: true);
        _onChange(IsOn);
    }

    private void Apply(bool on, bool animate)
    {
        IsOn = on;
        _track.SetResourceReference(Border.BackgroundProperty, on ? Theme.Keys.Accent : MacTokens.SwitchOff);
        var target = new Thickness(on ? TrackWidth - KnobWidth - Inset : Inset, 0, 0, 0);
        if (animate)
            _knob.BeginAnimation(MarginProperty, new ThicknessAnimation(target, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() });
        else
            _knob.Margin = target;
    }
}

/// <summary>
/// SwiftUI <c>Slider</c> (AppKit linear slider): a 6 pt track, filled up to a 20×16 pill knob.
/// <paramref name="onChanging"/> runs while dragging (update labels in place), <paramref name="onCommit"/>
/// once on release: writing the store mid-drag would rebuild the view under the pointer.
/// </summary>
public sealed class MacSlider : Grid
{
    private const double TrackHeight = 6, KnobWidth = 20, KnobHeight = 16;
    private readonly double _min, _max, _step;
    private readonly Border _fill = new() { Height = TrackHeight, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _knob = new()
    {
        Width = KnobWidth, Height = KnobHeight, CornerRadius = new CornerRadius(KnobHeight / 2),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Action<double>? _onChanging;
    private readonly Action<double> _onCommit;
    private bool _dragging;

    public double Value { get; private set; }

    public MacSlider(double min, double max, double value, Action<double> onCommit, Action<double>? onChanging = null,
        double step = 0, string? accessibleName = null)
    {
        _min = min;
        _max = max;
        _step = step;
        _onCommit = onCommit;
        _onChanging = onChanging;
        Value = Math.Clamp(value, min, max);
        Height = KnobHeight;
        MinWidth = 40;
        Background = Brushes.Transparent;
        VerticalAlignment = VerticalAlignment.Center;
        var track = new Border { Height = TrackHeight, CornerRadius = new CornerRadius(3) }.Bg(MacTokens.SliderTrack);
        _fill.Bg(Theme.Keys.Accent);
        _knob.Bg(MacTokens.Knob);
        _knob.Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 0.5, Direction = 270, Opacity = Theme.IsDark ? 0.4 : 0.3 };
        if (!Theme.IsDark) _knob.SetResourceReference(Border.BorderBrushProperty, MacTokens.ButtonStroke);
        _knob.BorderThickness = new Thickness(Theme.IsDark ? 0 : 0.5);
        Children.Add(track);
        Children.Add(_fill);
        Children.Add(_knob);
        Focusable = true;
        if (accessibleName != null) System.Windows.Automation.AutomationProperties.SetName(this, accessibleName);
        SizeChanged += (_, _) => Layout();
        MouseLeftButtonDown += (_, e) =>
        {
            _dragging = true;
            CaptureMouse();
            Focus();
            Set(FromX(e.GetPosition(this).X));
            e.Handled = true;
        };
        MouseMove += (_, e) => { if (_dragging) Set(FromX(e.GetPosition(this).X)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
            _onCommit(Value);
        };
        LostMouseCapture += (_, _) => { if (_dragging) { _dragging = false; _onCommit(Value); } };
        KeyDown += (_, e) =>
        {
            var delta = _step > 0 ? _step : (_max - _min) / 20;
            if (e.Key is Key.Left or Key.Down) Set(Value - delta);
            else if (e.Key is Key.Right or Key.Up) Set(Value + delta);
            else return;
            e.Handled = true;
            _onCommit(Value);
        };
    }

    private double FromX(double x)
    {
        var usable = Math.Max(1, ActualWidth - KnobWidth);
        return _min + Math.Clamp((x - KnobWidth / 2) / usable, 0, 1) * (_max - _min);
    }

    private void Set(double raw)
    {
        var v = Math.Clamp(raw, _min, _max);
        if (_step > 0) v = Math.Clamp(_min + Math.Round((v - _min) / _step) * _step, _min, _max);
        if (v == Value) return;
        Value = v;
        Layout();
        _onChanging?.Invoke(v);
    }

    private void Layout()
    {
        var fraction = _max > _min ? (Value - _min) / (_max - _min) : 0;
        var left = fraction * Math.Max(0, ActualWidth - KnobWidth);
        _knob.Margin = new Thickness(left, 0, 0, 0);
        _fill.Width = left + KnobWidth / 2;
    }
}

/// <summary>
/// SwiftUI <c>Picker</c> (<c>.pickerStyle(.menu)</c>) and <c>Menu</c>: an AppKit pop-up / pull-down
/// button that opens a menu. Pop-ups show ⌃⌄, pull-downs ⌄.
/// </summary>
public static class MacPopUp
{
    public sealed record Entry(string Title, bool Checked, Action Action);

    public static Border Picker<T>(IReadOnlyList<(T Value, string Title)> items, T selected, Action<T> onSelect,
        string? accessibleName = null, bool small = false)
    {
        var current = items.FirstOrDefault(i => EqualityComparer<T>.Default.Equals(i.Value, selected)).Title ?? "";
        var entries = items.Select(i => new Entry(i.Title, EqualityComparer<T>.Default.Equals(i.Value, selected), () => onSelect(i.Value))).ToList();
        // NSPopUpButton sizes itself to its longest item, so the button does not jump when the choice changes.
        var size = small ? Theme.FontSize.Subheadline : Theme.FontSize.Body;
        var widest = items.Count == 0 ? 0 : items.Max(i => TextWidth(i.Title, size));
        var chrome = small ? 8 + 8 + 6 + 7 : 10 + 8 + 6 + 9;
        return Make(current, entries, pullDown: false, small: small, accessibleName: accessibleName, width: Math.Ceiling(widest + chrome + 4));
    }

    public static double TextWidth(string text, double size) =>
        new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Theme.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

    public static Border Make(string title, IReadOnlyList<Entry> entries, bool pullDown, bool small = false,
        string? accessibleName = null, double? width = null)
    {
        var label = Ui.Text(title, small ? Theme.FontSize.Subheadline : Theme.FontSize.Body);
        var chevron = new Path
        {
            Data = Geometry.Parse(pullDown ? "M0,0 L3.5,3.5 L7,0" : "M0,3.5 L3,0.5 L6,3.5 M0,7.5 L3,10.5 L6,7.5"),
            StrokeThickness = 1.3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(pullDown ? 6 : 8, pullDown ? 1 : 0, 0, 0),
        };
        chevron.SetResourceReference(Shape.StrokeProperty, Theme.Keys.Primary);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(chevron);
        row.Children.Add(label);
        var button = new Border
        {
            Child = row,
            Height = small ? 20 : 24,
            Padding = new Thickness(small ? 8 : 10, 0, small ? 7 : 9, 0),
            CornerRadius = new CornerRadius(small ? 5 : 6),
            BorderThickness = new Thickness(Theme.IsDark ? 0 : 0.5),
            Effect = MacTokens.BezelShadow(),
            Focusable = true,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        }.Bg(MacTokens.PopUp);
        button.SetResourceReference(Border.BorderBrushProperty, MacTokens.PopUpStroke);
        if (width is { } w) button.Width = w;
        if (accessibleName != null) System.Windows.Automation.AutomationProperties.SetName(button, accessibleName);
        void Open()
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MinWidth = button.ActualWidth };
            foreach (var entry in entries)
            {
                var item = new MenuItem { Header = entry.Title, IsCheckable = false, IsChecked = entry.Checked, FontFamily = Theme.UiFont, FontSize = Theme.FontSize.Body };
                item.Click += (_, _) => entry.Action();
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; Open(); };
        button.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter or Key.Down) { e.Handled = true; Open(); } };
        return button;
    }
}

/// <summary>
/// macOS segmented <c>Picker</c> sized to its content: equal segments (the widest label), the
/// selection a raised plate, thin dividers between unselected neighbours (AppKit draws them).
/// </summary>
public static class MacSegmented
{
    public static Border Make<T>(IReadOnlyList<(T Value, string Title)> items, T selected, Action<T> onSelect, bool small = false)
    {
        var grid = new UniformGrid { Rows = 1 };
        var index = items.ToList().FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected));
        for (var i = 0; i < items.Count; i++)
        {
            var (value, title) = items[i];
            var isSelected = i == index;
            var label = Ui.Text(title, small ? Theme.FontSize.Subheadline : Theme.FontSize.Body);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.TextTrimming = TextTrimming.None;
            var cell = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var plate = new Border
            {
                CornerRadius = new CornerRadius(small ? 4.5 : 5),
                Child = label,
                Padding = new Thickness(small ? 10 : 12, 0, small ? 10 : 12, 0),
                Effect = isSelected ? MacTokens.BezelShadow() : null,
            };
            if (isSelected) plate.Bg(MacTokens.SegmentSelected);
            cell.Children.Add(plate);
            // A divider sits between two unselected segments only.
            if (i > 0 && !isSelected && i - 1 != index)
            {
                var divider = new Border { Width = 1, Height = small ? 10 : 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-1, 0, 0, 0) }
                    .Bg(MacTokens.SegmentDivider);
                cell.Children.Add(divider);
            }
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; onSelect(value); };
            grid.Children.Add(cell);
        }
        return new Border
        {
            Child = grid,
            Height = small ? 20 : 24,
            Padding = new Thickness(1.5),
            CornerRadius = new CornerRadius(small ? 6 : 7),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        }.Bg(MacTokens.SegmentTrack);
    }
}

/// <summary>AppKit push buttons: bordered (regular / small) and <c>.borderedProminent</c>.</summary>
public static class MacButton
{
    public static Button Make(string title, Action action, bool prominent = false, bool small = false, bool enabled = true,
        bool busy = false)
    {
        UIElement content = busy ? MacSpinner.Make(small ? 10 : 12) : Ui.Text(title, small ? Theme.FontSize.Subheadline : Theme.FontSize.Body);
        if (!busy && prominent && content is TextBlock t) t.Foreground = Brushes.White;
        if (content is TextBlock tb) tb.TextTrimming = TextTrimming.None;
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(small ? 8 : 10, 0, small ? 8 : 10, 0),
            Height = small ? 19 : 22,
            MinWidth = busy ? (small ? 40 : 52) : 0,
            Cursor = Cursors.Hand,
            Template = Template(small ? 5 : 6),
            IsEnabled = enabled && !busy,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = prominent ? null : MacTokens.BezelShadow(),
        };
        b.SetResourceReference(Control.BackgroundProperty, prominent ? Theme.Keys.Accent : MacTokens.Button);
        b.SetResourceReference(Control.BorderBrushProperty, prominent ? Theme.Keys.Accent : MacTokens.ButtonStroke);
        b.BorderThickness = new Thickness(Theme.IsDark || prominent ? 0 : 0.5);
        b.Click += (_, _) => action();
        return b;
    }

    private static readonly Dictionary<double, ControlTemplate> Templates = new();

    private static ControlTemplate Template(double radius)
    {
        if (Templates.TryGetValue(radius, out var cached)) return cached;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
        template.Triggers.Add(disabled);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7));
        template.Triggers.Add(pressed);
        template.Seal();
        Templates[radius] = template;
        return template;
    }
}

/// <summary>SwiftUI <c>ProgressView().controlSize(.small)</c>: a spinning arc.</summary>
public static class MacSpinner
{
    public static FrameworkElement Make(double size)
    {
        var r = size / 2 - 1;
        var arc = new Path
        {
            Data = Geometry.Parse(FormattableString.Invariant($"M{size / 2},1 A{r},{r} 0 1 1 1,{size / 2}")),
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = size, Height = size,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        arc.SetResourceReference(Shape.StrokeProperty, Theme.Keys.Secondary);
        var rotate = new RotateTransform();
        arc.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        return arc;
    }
}

/// <summary>
/// SwiftUI <c>TextField</c> / <c>SecureField</c> with <c>.textFieldStyle(.roundedBorder)</c>: a bordered
/// rounded box, accent ring on focus, placeholder in tertiary.
/// </summary>
public static class MacField
{
    public static Border Text(string text, string placeholder, Action<string> onChange, out TextBox box,
        bool multiline = false, double fontSize = Theme.FontSize.Body, bool monospaced = false)
    {
        box = new TextBox
        {
            Text = text,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = fontSize,
            FontFamily = monospaced ? RecapFonts.Mono : Theme.UiFont,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = false,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        box.SetResourceReference(Control.ForegroundProperty, Theme.Keys.Primary);
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, Theme.Keys.Primary);
        var input = box;
        var hint = Placeholder(placeholder, fontSize);
        hint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) =>
        {
            hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            onChange(input.Text);
        };
        if (multiline)
        {
            // SwiftUI's vertical TextField: Return submits, Shift/Option+Return breaks the line.
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
                var caret = input.CaretIndex;
                input.Text = input.Text.Insert(caret, "\n");
                input.CaretIndex = caret + 1;
                e.Handled = true;
            };
        }
        return Frame(box, hint, multiline);
    }

    public static Border Secure(string placeholder, Action<string> onChange, out PasswordBox box, string initial = "")
    {
        box = new PasswordBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = Theme.FontSize.Body,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            PasswordChar = '•',
        };
        if (initial.Length > 0) box.Password = initial;
        box.SetResourceReference(Control.ForegroundProperty, Theme.Keys.Primary);
        box.SetResourceReference(PasswordBox.CaretBrushProperty, Theme.Keys.Primary);
        var input = box;
        var hint = Placeholder(placeholder, Theme.FontSize.Body);
        hint.Visibility = initial.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.PasswordChanged += (_, _) =>
        {
            hint.Visibility = input.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            onChange(input.Password);
        };
        return Frame(box, hint, multiline: false);
    }

    private static TextBlock Placeholder(string text, double size)
    {
        var t = Ui.Text(text, size, Theme.Keys.Tertiary);
        t.IsHitTestVisible = false;
        t.VerticalAlignment = VerticalAlignment.Top;
        t.Margin = new Thickness(2, 0, 0, 0);
        return t;
    }

    private static Border Frame(Control input, TextBlock hint, bool multiline)
    {
        input.Margin = new Thickness(0);
        hint.Margin = new Thickness(2, 0, 0, 0);
        var grid = new Grid();
        grid.Children.Add(hint);
        grid.Children.Add(input);
        if (!multiline) hint.VerticalAlignment = VerticalAlignment.Center;
        var frame = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, multiline ? 3 : 0, 5, multiline ? 3 : 0),
            MinHeight = 22,
        }.Bg(MacTokens.Field);
        frame.SetResourceReference(Border.BorderBrushProperty, MacTokens.FieldStroke);
        input.GotKeyboardFocus += (_, _) => frame.SetResourceReference(Border.BorderBrushProperty, Theme.Keys.Accent);
        input.LostKeyboardFocus += (_, _) => frame.SetResourceReference(Border.BorderBrushProperty, MacTokens.FieldStroke);
        frame.MouseLeftButtonDown += (_, _) => input.Focus();
        return frame;
    }
}

/// <summary>
/// A modal sheet standing in for <c>NSAlert</c>: icon-less title, message, buttons right-aligned with
/// the first button leftmost-trailing like AppKit. Returns the index of the button pressed
/// (Esc = <paramref name="cancelIndex"/>, Return = <paramref name="defaultIndex"/>).
/// </summary>
public static class MacAlert
{
    public static int Show(string title, string message, IReadOnlyList<string> buttons, int? defaultIndex = 0, int? cancelIndex = null,
        bool warning = false)
    {
        MacTokens.Ensure();
        var result = cancelIndex ?? buttons.Count - 1;
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, SizeToContent = SizeToContent.Height, Width = 280,
            ShowInTaskbar = true, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, Title = title,
        };
        var icon = Ui.Icon(warning ? Ui.Glyphs.Warning : "\uE946", 28, warning ? Theme.Keys.Orange : Theme.Keys.Accent);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var titleText = Ui.Text(title, Theme.FontSize.Headline, weight: FontWeights.Bold).Wrapping();
        titleText.TextAlignment = TextAlignment.Center;
        var body = Ui.Text(message, Theme.FontSize.Subheadline).Wrapping();
        body.TextAlignment = TextAlignment.Center;
        var stack = Ui.VStack(6, icon, titleText, body);
        var buttonStack = Ui.VStack(6);
        buttonStack.Margin = new Thickness(0, 10, 0, 0);
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = MacButton.Make(buttons[i], () => { result = index; window.Close(); }, prominent: i == defaultIndex);
            b.Height = 24;
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (i == defaultIndex) b.IsDefault = true;
            if (i == cancelIndex) b.IsCancel = true;
            buttonStack.Children.Add(b);
        }
        stack.Children.Add(buttonStack);
        var card = new Border
        {
            Child = stack, Padding = new Thickness(18, 18, 18, 16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
        }.Bg(Theme.Keys.Background);
        card.SetResourceReference(Border.BorderBrushProperty, Theme.Keys.Separator);
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) window.DragMove(); };
        window.Content = card;
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { result = cancelIndex ?? result; window.Close(); } };
        window.ShowDialog();
        return result;
    }
}
