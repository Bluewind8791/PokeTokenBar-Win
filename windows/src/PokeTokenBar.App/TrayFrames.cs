using PokeTokenBar.Core;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PokeTokenBar.App;

/// <summary>
/// Builds tray icon frames from sprites — the counterpart of <c>menuBarImage</c> / <c>bobFrames</c> /
/// <c>eggFrames</c> in PokeTokenBarApp.swift. The tray slot is square, so the sprite is fitted
/// (aspect kept, nearest-neighbour) and bottom-aligned; "up" lifts it one pixel for the idle bob.
/// </summary>
public static class TrayFrames
{
    private static int IconSize => Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);

    public static Drawing.Icon Icon(Drawing.Bitmap sprite, bool up)
    {
        var size = IconSize;
        using var canvas = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(canvas))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
            var scale = Math.Min((double)size / sprite.Width, (double)(size - 1) / sprite.Height);
            var w = (int)Math.Round(sprite.Width * scale);
            var h = (int)Math.Round(sprite.Height * scale);
            g.DrawImage(sprite, (size - w) / 2, size - h - (up ? 1 : 0), w, h);
        }
        return IconHandles.Own(canvas.GetHicon());
    }

    public static List<(Drawing.Icon, TimeSpan)> Bob(Drawing.Bitmap sprite) =>
        [(Icon(sprite, false), TimeSpan.FromSeconds(0.5)), (Icon(sprite, true), TimeSpan.FromSeconds(0.5))];

    public static List<(Drawing.Icon, TimeSpan)> Animated(IEnumerable<SpriteFrame> frames) =>
        frames.Select(f => (Icon(f.Image, false), f.Delay)).ToList();

    public static List<(Drawing.Icon, TimeSpan)> Egg() =>
        [(TrayIcons.Egg(false), TimeSpan.FromSeconds(0.5)), (TrayIcons.Egg(true), TimeSpan.FromSeconds(0.5))];
}

/// <summary>GetHicon hands out an unmanaged HICON that Icon.FromHandle does not own: clone it, then free it.</summary>
internal static class IconHandles
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Drawing.Icon Own(IntPtr hIcon)
    {
        using var borrowed = Drawing.Icon.FromHandle(hIcon);
        var owned = (Drawing.Icon)borrowed.Clone();
        DestroyIcon(hIcon);
        return owned;
    }
}
