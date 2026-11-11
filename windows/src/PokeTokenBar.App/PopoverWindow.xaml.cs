using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PokeTokenBar.App;

/// <summary>
/// The macOS <c>NSPopover</c> (behavior .transient) as a borderless topmost window anchored to the
/// notification area. It closes when it loses focus, like a transient popover on an outside click.
/// </summary>
public partial class PopoverWindow : Window
{
    public PopoverWindow(FrameworkElement content)
    {
        InitializeComponent();
        Host.Content = content;
        Deactivated += (_, _) => Close();
    }

    /// <summary>Places the window against the taskbar edge nearest the click, inside the work area.</summary>
    public void ShowNearTray(Drawing.Point clickPixels)
    {
        Opacity = 0;
        Show();   // measure first: SizeToContent needs a layout pass
        var screen = Forms.Screen.FromPoint(clickPixels);
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        double ToDip(int px, double scale) => px / scale;
        var w = ActualWidth;
        var h = ActualHeight;
        const double gap = 8;

        // Taskbar edge = the side where the working area is smaller than the screen.
        double left, top;
        if (work.Bottom < bounds.Bottom)        // bottom (default)
        {
            left = ToDip(clickPixels.X, dpi.DpiScaleX) - w / 2;
            top = ToDip(work.Bottom, dpi.DpiScaleY) - h - gap;
        }
        else if (work.Top > bounds.Top)         // top
        {
            left = ToDip(clickPixels.X, dpi.DpiScaleX) - w / 2;
            top = ToDip(work.Top, dpi.DpiScaleY) + gap;
        }
        else if (work.Left > bounds.Left)       // left
        {
            left = ToDip(work.Left, dpi.DpiScaleX) + gap;
            top = ToDip(clickPixels.Y, dpi.DpiScaleY) - h / 2;
        }
        else                                    // right, or auto-hidden taskbar
        {
            left = ToDip(work.Right, dpi.DpiScaleX) - w - gap;
            top = ToDip(clickPixels.Y, dpi.DpiScaleY) - h / 2;
        }
        Left = Math.Clamp(left, ToDip(work.Left, dpi.DpiScaleX) + gap, ToDip(work.Right, dpi.DpiScaleX) - w - gap);
        Top = Math.Clamp(top, ToDip(work.Top, dpi.DpiScaleY) + gap, ToDip(work.Bottom, dpi.DpiScaleY) - h - gap);
        Opacity = 1;
        Activate();
    }
}
