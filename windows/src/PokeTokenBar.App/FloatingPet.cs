using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>FloatingPetController</c> / <c>FloatingPetView</c> (FloatingPetPanel.swift): an opt-in,
/// always-on-top, non-activating desktop pet. Drag to move (position remembered), click to open the
/// popover, right-click for Open / Hide, hover for today's usage, and a speech bubble for limit alerts.
/// </summary>
public sealed class FloatingPetController : IDisposable
{
    private const string OriginXKey = "floatingPetOriginX";
    private const string OriginYKey = "floatingPetOriginY";
    private const double BubbleHeadroom = 72, BubbleMinWidth = 180, BubbleHorizontalPadding = 8;
    private const double BubbleContentWidth = BubbleMinWidth - BubbleHorizontalPadding * 2;
    /// <summary>Movement under ~4 pt between press and release is a click, not a drag.</summary>
    private const double ClickThresholdSquared = 16;

    private readonly UsageStore _store;
    private readonly CompanionStore _companion;
    private readonly PowerMonitor _power;
    private readonly AppSettings _settings;
    private readonly Action _openPopover;
    private Window? _window;
    private readonly ContentControl _bubbleSlot = new() { Focusable = false };
    private readonly ContentControl _spriteSlot = new() { Focusable = false, HorizontalAlignment = HorizontalAlignment.Center };
    private string? _spriteKey;
    /// <summary>True between press and release: a refresh must not snap the window back mid-drag.</summary>
    private bool _pressed;
    private readonly WinEventProc _onForeground;
    private IntPtr _foregroundHook;

    public FloatingPetController(UsageStore store, CompanionStore companion, PowerMonitor power, Action openPopover, AppSettings? settings = null)
    {
        _store = store;
        _companion = companion;
        _power = power;
        _openPopover = openPopover;
        _settings = settings ?? AppSettings.Current;
        _onForeground = (_, _, _, _, _, _, _) => KeepOnTop();
        _store.Changed += Sync;
        _companion.Changed += Sync;
        _power.Changed += Sync;
        Sync();
    }

    public static (double Width, double Height) PanelSize(double petSize, bool showingBubble) =>
        showingBubble ? (Math.Max(petSize, BubbleMinWidth), petSize + BubbleHeadroom) : (petSize, petSize);

    public static string HoverTooltip(long todayTokens, double? limitUtilization, LimitPace.LimitDisplayMode mode, L l)
    {
        var usage = TokenFormatter.Grouped(todayTokens);
        if (limitUtilization is not { } pct) return l.FloatingPetHoverTokensOnly(usage);
        var text = TokenFormatter.Percent(UsageStore.DisplayPercent(pct, mode));
        return l.FloatingPetHoverWithLimit(usage, mode == LimitPace.LimitDisplayMode.Remaining ? l.PercentRemaining(text) : text);
    }

    private void Sync()
    {
        // Display off → the window and its animation timer go away (macOS setDisplayAwake); on → rebuilt.
        if (!_store.FloatingPetEnabled || !_power.DisplayOn) { Hide(); return; }
        Show();
    }

    private void Show()
    {
        var petSize = _store.FloatingPetSize;
        var alert = _store.CurrentBubbleAlert;
        var subject = _companion.RepresentativeSubject;
        var l = new L(_companion.Language);
        var window = _window ??= MakeWindow();

        // Energy saver shows a still sprite (FloatingPetController.shouldAnimate(lowPower:)).
        var animated = !_power.LowPower;
        // Rebuild the sprite only when the subject, size or animation changes (it owns an animation timer).
        var key = $"{subject.SpeciesID}-{subject.IsShiny}-{subject.Form?.RawValue}-{petSize}-{_store.AnimationQuality}-{animated}";
        if (key != _spriteKey)
        {
            _spriteKey = key;
            _spriteSlot.Content = new SpriteView(subject.SpeciesID, petSize, animated: animated, shiny: subject.IsShiny, form: subject.Form,
                minFrameDelay: TimeSpan.FromSeconds(_store.AnimationQuality.FrameFloor()));
        }
        _bubbleSlot.Content = alert == null ? null : SpeechBubble(alert, l);
        _bubbleSlot.Visibility = alert == null ? Visibility.Collapsed : Visibility.Visible;

        if (_pressed) { if (!window.IsVisible) window.Show(); return; }
        var (w, h) = PanelSize(petSize, alert != null);
        var pet = PetOrigin(petSize);
        window.Width = w;
        window.Height = h;
        // The pet keeps its place; a bubble grows the window upward and centered over it.
        window.Left = pet.X - Math.Max(0, (w - petSize) / 2);
        window.Top = pet.Y - (h - petSize);
        window.ToolTip = alert == null ? HoverTooltip(_store.TodayTotalTokens, _store.HighestLimitUtilization, _store.LimitDisplayMode, l) : null;
        if (!window.IsVisible) window.Show();
        KeepOnTop();
    }

    private void Hide()
    {
        if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        // Detach the reusable slots so the next window can host them.
        if (_window?.Content is Stack st) st.Children.Clear();
        _window?.Close();
        _window = null;
        _spriteKey = null;
    }

    /// <summary>Top-left of the pet square in DIPs; default: bottom-right of the primary work area, 24 pt in.</summary>
    private Point PetOrigin(double petSize)
    {
        var work = SystemParameters.WorkArea;
        if (_settings.GetDouble(OriginXKey) is { } x && _settings.GetDouble(OriginYKey) is { } y)
        {
            var onScreen = x + petSize > SystemParameters.VirtualScreenLeft && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                && y + petSize > SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (onScreen) return new Point(x, y);
        }
        return new Point(work.Right - petSize - 24, work.Bottom - petSize - 24);
    }

    private Window MakeWindow()
    {
        var root = new Stack { Orientation = Orientation.Vertical, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        root.Children.Add(_bubbleSlot);
        root.Children.Add(_spriteSlot);
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Content = root,
        };
        ToolTipService.SetInitialShowDelay(w, 300);
        w.SourceInitialized += (_, _) => MakeNonActivating(w);
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _onForeground, 0, 0, WinEventOutOfContext);

        Point? down = null;
        Point originAtDown = default;
        var dragged = false;
        w.MouseLeftButtonDown += (_, e) =>
        {
            down = w.PointToScreen(e.GetPosition(w));
            originAtDown = new Point(w.Left, w.Top);
            dragged = false;
            _pressed = true;
            w.CaptureMouse();
        };
        w.MouseMove += (_, e) =>
        {
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = w.PointToScreen(e.GetPosition(w));
            var dpi = VisualTreeHelper.GetDpi(w);
            var dx = (now.X - start.X) / dpi.DpiScaleX;
            var dy = (now.Y - start.Y) / dpi.DpiScaleY;
            if (dx * dx + dy * dy >= ClickThresholdSquared) dragged = true;
            if (!dragged) return;
            w.Left = originAtDown.X + dx;
            w.Top = originAtDown.Y + dy;
        };
        w.MouseLeftButtonUp += (_, _) =>
        {
            w.ReleaseMouseCapture();
            _pressed = false;
            if (down == null) return;
            down = null;
            if (dragged) SaveOrigin(w);
            else _openPopover();
        };
        w.MouseRightButtonUp += (_, _) =>
        {
            var l = new L(_companion.Language);
            var menu = new ContextMenu();
            var open = new MenuItem { Header = l.FloatingPetMenuOpen };
            open.Click += (_, _) => _openPopover();
            var hide = new MenuItem { Header = l.FloatingPetMenuHide };
            hide.Click += (_, _) => _store.FloatingPetEnabled = false;
            menu.Items.Add(open);
            menu.Items.Add(hide);
            menu.IsOpen = true;
        };
        return w;
    }

    private void SaveOrigin(Window w)
    {
        var petSize = _store.FloatingPetSize;
        var (width, height) = PanelSize(petSize, _store.CurrentBubbleAlert != null);
        _settings.Set(OriginXKey, w.Left + Math.Max(0, (width - petSize) / 2));
        _settings.Set(OriginYKey, w.Top + (height - petSize));
    }

    /// <summary>Port of <c>SpeechBubbleView</c>: title (red when critical) + up to two body lines, tail pointing down.</summary>
    private static FrameworkElement SpeechBubble(LimitAlert alert, L l)
    {
        var title = Ui.Text(alert.IsCritical ? l.NotifCritical : l.NotifWarning, 11, alert.IsCritical ? Theme.Keys.Red : Theme.Keys.Primary, FontWeights.Bold);
        var body = Ui.Text(alert.Body(l), 10, Theme.Keys.Secondary).Wrapping();
        body.MaxHeight = 2 * 10 * 1.35;   // two lines, like lineLimit(2)
        body.TextTrimming = TextTrimming.CharacterEllipsis;
        var card = new Border
        {
            Child = Ui.VStack(2, title, body),
            Width = BubbleContentWidth + BubbleHorizontalPadding * 2,
            Padding = new Thickness(BubbleHorizontalPadding, 6, BubbleHorizontalPadding, 6),
            CornerRadius = new CornerRadius(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Opacity = 0.15, BlurRadius = 8, ShadowDepth = 2, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Background);
        var tail = new Polygon { Points = [new(10, 0), new(20, 0), new(15, 6)], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -1, 0, 0) };
        tail.SetResourceReference(Shape.FillProperty, Theme.Keys.Background);
        var bubble = Ui.VStack(0, card, tail);
        bubble.HorizontalAlignment = HorizontalAlignment.Center;
        return bubble;
    }

    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080, WsExTopmost = 0x00000008;
    private const uint GwHwndPrev = 3, EventSystemForeground = 0x0003, WinEventOutOfContext = 0;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    /// <summary>
    /// Windows can drop the pet into the normal z-order band when another app takes the foreground,
    /// leaving WS_EX_TOPMOST set but the window under ordinary windows (an NSPanel's floating level
    /// never does this). Checked on every foreground change and refresh. It re-raises only when a
    /// non-topmost window sits above the pet, so the pet never jumps over the popover or other
    /// topmost windows.
    /// </summary>
    private void KeepOnTop()
    {
        if (_window == null) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        // Topmost windows form one contiguous band at the top, so any normal window above means demoted.
        for (var h = GetWindow(hwnd, GwHwndPrev); h != IntPtr.Zero; h = GetWindow(h, GwHwndPrev))
        {
            if ((GetWindowLong(h, GwlExStyle) & WsExTopmost) != 0) continue;
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            return;
        }
    }

    /// <summary>Like a non-activating NSPanel: clicks don't steal focus, and it stays out of Alt+Tab.</summary>
    private static void MakeNonActivating(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow);
    }

    public void Dispose()
    {
        _store.Changed -= Sync;
        _companion.Changed -= Sync;
        _power.Changed -= Sync;
        Hide();
    }
}
