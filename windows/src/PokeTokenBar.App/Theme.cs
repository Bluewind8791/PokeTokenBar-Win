using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PokeTokenBar.App;

/// <summary>
/// Design tokens. The values are the macOS system colors and SwiftUI text sizes the original app
/// renders with, so the Windows popover reads the same. Light/dark follows the Windows app mode
/// (the macOS app follows the system appearance the same way).
///
/// Every brush is published as an application resource under the key in <see cref="Keys"/>;
/// views reference them with <c>DynamicResource</c> (XAML) or <see cref="Brush"/> (code), so a
/// theme switch repaints open windows.
/// </summary>
public static class Theme
{
    public static class Keys
    {
        public const string Background = "Theme.Background";
        public const string Primary = "Theme.Primary";
        public const string Secondary = "Theme.Secondary";
        public const string Tertiary = "Theme.Tertiary";
        public const string Separator = "Theme.Separator";
        public const string Fill = "Theme.Fill";             // Color.primary.opacity(0.08)
        public const string FillStrong = "Theme.FillStrong"; // Color.secondary.opacity(0.2)
        public const string Accent = "Theme.Accent";
        public const string AccentSoft = "Theme.AccentSoft"; // accentColor.opacity(0.18)
        public const string Green = "Theme.Green";
        public const string Yellow = "Theme.Yellow";
        public const string Orange = "Theme.Orange";
        public const string OrangeSoft = "Theme.OrangeSoft"; // orange.opacity(0.1)
        public const string Red = "Theme.Red";
        public const string Blue = "Theme.Blue";
        public const string Teal = "Theme.Teal";
        public const string Gray = "Theme.Gray";
        public const string ControlBackground = "Theme.ControlBackground";
        public const string ControlSelected = "Theme.ControlSelected";
        /// <summary>The activity grid's shades, index = level 0…4 (<c>ActivityHeatmapMetrics.contributionGreens</c>).</summary>
        public static readonly string[] ContributionGreens =
            [.. Enumerable.Range(0, 5).Select(level => $"Theme.ContributionGreen{level}")];
    }

    /// <summary>SwiftUI macOS text styles in points (= WPF DIPs at 100%).</summary>
    public static class FontSize
    {
        public const double Caption2 = 10;
        public const double Caption = 10.5;
        public const double Footnote = 10.5;
        public const double Subheadline = 11;
        public const double Callout = 12;
        public const double Body = 13;
        public const double Headline = 13;
        public const double Title3 = 15;
        public const double Title2 = 17;
        public const double Title = 22;
    }

    public const double PopoverWidth = 360;
    public const double PopoverPadding = 14;
    public const double ScrollerInset = 12;
    public const double ContentWidth = PopoverWidth - PopoverPadding * 2;
    public const double ScrollContentWidth = ContentWidth - ScrollerInset;

    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Install(Application app)
    {
        Apply(app.Resources, ReadSystemIsDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            app.Dispatcher.Invoke(() =>
            {
                var dark = ReadSystemIsDark();
                if (dark == IsDark) return;
                Apply(app.Resources, dark);
                Changed?.Invoke();
            });
        };
    }

    /// <summary>For renders and tests: install a fixed appearance into any resource dictionary.</summary>
    public static void Apply(ResourceDictionary resources, bool dark)
    {
        IsDark = dark;
        Color C(uint rgb, byte a = 255) => Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        Color Ink(byte a) => dark ? Color.FromArgb(a, 255, 255, 255) : Color.FromArgb(a, 0, 0, 0);
        var accent = dark ? C(0x0A84FF) : C(0x007AFF);
        var orange = dark ? C(0xFF9F0A) : C(0xFF9500);
        var map = new Dictionary<string, Color>
        {
            [Keys.Background] = dark ? C(0x282828) : C(0xECECEC),
            [Keys.Primary] = Ink(dark ? (byte)0xD9 : (byte)0xD9),
            [Keys.Secondary] = Ink(dark ? (byte)0x8C : (byte)0x80),
            [Keys.Tertiary] = Ink(dark ? (byte)0x40 : (byte)0x42),
            [Keys.Separator] = Ink(dark ? (byte)0x1A : (byte)0x1A),
            [Keys.Fill] = Ink(0x14),
            [Keys.FillStrong] = Ink(dark ? (byte)0x1C : (byte)0x1A),
            [Keys.Accent] = accent,
            [Keys.AccentSoft] = Color.FromArgb(0x2E, accent.R, accent.G, accent.B),
            [Keys.Green] = dark ? C(0x30D158) : C(0x34C759),
            [Keys.Yellow] = dark ? C(0xFFD60A) : C(0xFFCC00),
            [Keys.Orange] = orange,
            [Keys.OrangeSoft] = Color.FromArgb(0x1A, orange.R, orange.G, orange.B),
            [Keys.Red] = dark ? C(0xFF453A) : C(0xFF3B30),
            [Keys.Blue] = dark ? C(0x0A84FF) : C(0x007AFF),
            [Keys.Teal] = dark ? C(0x40C8E0) : C(0x30B0C7),
            [Keys.Gray] = dark ? C(0x98989D) : C(0x8E8E93),
            [Keys.ControlBackground] = Ink(dark ? (byte)0x1A : (byte)0x0F),
            [Keys.ControlSelected] = dark ? C(0x5A5A5A) : C(0xFFFFFF),
        };
        // The contribution-graph greens, light / dark: each appearance has its own scale so level 1 never melts
        // into the background. Not the accent color, which would read as a provider color.
        (uint Light, uint Dark)[] greens =
            [(0xEBEDF0, 0x161B22), (0x9BE9A8, 0x0E4429), (0x40C463, 0x006D32), (0x30A14E, 0x26A641), (0x216E39, 0x39D353)];
        for (var level = 0; level < greens.Length; level++)
            map[Keys.ContributionGreens[level]] = C(dark ? greens[level].Dark : greens[level].Light);
        foreach (var (key, color) in map)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
    }

    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Magenta;

    /// <summary>Swift <c>PaceTier.color</c>.</summary>
    public static string PaceKey(Core.PaceTier tier) => tier switch
    {
        Core.PaceTier.WayUnder => Keys.Blue,
        Core.PaceTier.Under => Keys.Teal,
        Core.PaceTier.OnPace => Keys.Green,
        Core.PaceTier.SlightlyOver => Keys.Yellow,
        Core.PaceTier.Over => Keys.Orange,
        _ => Keys.Red,
    };

    /// <summary>Swift <c>PaceTier.percentColor</c>: yellow text is unreadable, so it falls back to primary.</summary>
    public static string PacePercentKey(Core.PaceTier tier) =>
        tier == Core.PaceTier.SlightlyOver ? Keys.Primary : PaceKey(tier);

    private static bool ReadSystemIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
