using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PokeTokenBar.App;

/// <summary>
/// The Windows stand-in for the macOS status item: a notification-area icon that animates the
/// companion sprite, opens the popover on left click and the status menu on right click.
/// The tray holds an icon only, so the usage text the menu bar shows lives in the tooltip.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly AppController _app;
    private readonly Forms.NotifyIcon _icon = new();
    private readonly DispatcherTimer _frameTimer = new();
    private IReadOnlyList<(Drawing.Icon Icon, TimeSpan Delay)> _frames = [];
    private int _frameIndex;
    private PopoverWindow? _popover;
    private DateTime _popoverClosedAt;
    private bool _displayOn = true;

    public TrayController(AppController app)
    {
        _app = app;
        _icon.Text = "PokeTokenBar";
        _icon.MouseClick += OnMouseClick;
        _icon.ContextMenuStrip = new Forms.ContextMenuStrip();
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu(_icon.ContextMenuStrip);
        _frameTimer.Tick += (_, _) => Advance();
        SetFrames(TrayFrames.Egg());
        _icon.Visible = true;
    }

    /// <summary>Tooltip text; Windows caps it at 127 characters.</summary>
    public void SetToolTip(string text) => _icon.Text = text.Length > 127 ? text[..127] : text;

    /// <summary>A Windows notification (shown as a toast on Windows 10/11).</summary>
    public void Notify(string title, string body) =>
        _icon.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.None);

    public void SetFrames(IReadOnlyList<(Drawing.Icon Icon, TimeSpan Delay)> frames)
    {
        _frameTimer.Stop();
        _frames = frames;
        _frameIndex = 0;
        Advance();
    }

    private void Advance()
    {
        _frameTimer.Stop();
        if (_frames.Count == 0) return;
        var (icon, delay) = _frames[_frameIndex % _frames.Count];
        _icon.Icon = icon;
        _frameIndex++;
        if (_frames.Count > 1 && ShouldAnimate)
        {
            _frameTimer.Interval = delay;
            _frameTimer.Start();
        }
    }

    /// <summary>
    /// Display off or popover open → the tray animation stops (AppDelegate.menuShouldAnimate): nothing sees it
    /// while the display is off, and the popover animates the same sprite itself (defect-log "에너지" ③).
    /// </summary>
    private bool ShouldAnimate => _displayOn && _popover is not { IsVisible: true };

    public void SetDisplayOn(bool on)
    {
        _displayOn = on;
        SyncAnimation();
    }

    /// <summary>Resumes or stops the frame loop to match <see cref="ShouldAnimate"/>. Idempotent.</summary>
    private void SyncAnimation()
    {
        if (!ShouldAnimate) _frameTimer.Stop();
        else if (!_frameTimer.IsEnabled) Advance();
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) return;
        // Clicking the icon while the popover is open deactivates (closes) it first; without this
        // guard the same click would reopen it at once.
        if ((DateTime.UtcNow - _popoverClosedAt).TotalMilliseconds < 300) return;
        if (_popover is { IsVisible: true }) ClosePopover();
        else ShowPopover(n => n.Reset());
    }

    /// <summary>Opens the popover after applying a navigation route (Swift <c>showPopover(route:)</c>).</summary>
    public void ShowPopover(Action<PopoverNavigation> route)
    {
        route(_app.Nav);
        if (_popover is { IsVisible: true }) { _popover.Activate(); return; }
        _popover = new PopoverWindow(PopoverRoot.Build(_app.Services));
        _popover.Closed += (_, _) => { _popoverClosedAt = DateTime.UtcNow; _popover = null; SyncAnimation(); };
        _popover.ShowNearTray(Forms.Cursor.Position);
        SyncAnimation();
    }

    public void ClosePopover() => _popover?.Close();

    private void BuildMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        foreach (var entry in _app.MenuEntries())
        {
            if (entry is not { } e) { menu.Items.Add(new Forms.ToolStripSeparator()); continue; }
            var item = new Forms.ToolStripMenuItem(e.Title) { Enabled = e.Action != null };
            if (e.Action is { } action) item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    public void Dispose()
    {
        _frameTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Drawn egg used until the PokéAPI egg sprite loads (or when offline).</summary>
internal static class TrayIcons
{
    public static Drawing.Icon Egg(bool up)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var y = up ? 2 : 4;
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(250, 245, 230));
            using var pen = new Drawing.Pen(Drawing.Color.FromArgb(90, 80, 70), 2);
            g.FillEllipse(fill, 7, y, 18, 26);
            g.DrawEllipse(pen, 7, y, 18, 26);
        }
        return IconHandles.Own(bmp.GetHicon());
    }
}
