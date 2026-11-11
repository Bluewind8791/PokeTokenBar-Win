using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Offscreen renders for comparing the port against the macOS screenshots in <c>assets/</c> and for
/// PR images: <c>PokeTokenBar.exe --render &lt;screen&gt; &lt;out.png&gt; [--light] [--lang ko]</c>.
/// Screens register a builder in <see cref="Screens"/>; builders use sample data only — they never
/// read the user's logs, credentials or save.
/// </summary>
public static class RenderHarness
{
    public sealed record Context(AppLanguage Language, bool Dark);

    /// <summary>Screen name → builder. Each screen file adds its own entry in a static constructor call.</summary>
    public static readonly Dictionary<string, Func<Context, FrameworkElement>> Screens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["controls"] = ControlsGallery,
        ["popover"] = c => Popover(c, _ => { }),
        ["home"] = c => Popover(c, n => n.Tab = PopoverTab.Home),
        ["shop"] = c => Popover(c, n => n.Tab = PopoverTab.Shop),
        ["bag"] = c => Popover(c, n => n.Tab = PopoverTab.Bag),
        ["home-full"] = c => PopoverRoot.Build(SampleData.HomeServices(c, HomeSample.Full)),
        ["home-zero"] = c => PopoverRoot.Build(SampleData.HomeServices(c, HomeSample.QuietDay)),
        ["home-empty"] = c => PopoverRoot.Build(SampleData.HomeServices(c, HomeSample.Empty)),
        ["home-alerts"] = c => PopoverRoot.Build(SampleData.HomeServices(c, HomeSample.Alerts)),
        ["home-content"] = c => { var s = SampleData.HomeServices(c, HomeSample.Full); return Live.View(s, () => HomeView.Build(s)); },
        ["home-alerts-content"] = c => { var s = SampleData.HomeServices(c, HomeSample.Alerts); return Live.View(s, () => HomeView.Build(s)); },
        ["shop-ref"] = SampleData.ShopReference,
        ["shop-stocked"] = SampleData.ShopStocked,
        ["bag-ref"] = SampleData.BagReference,
        ["bag-full"] = SampleData.BagFull,
        ["shop-confirm"] = SampleData.ShopConfirm,
        ["shop-warning"] = SampleData.ShopWarning,
        ["bag-confirm"] = SampleData.BagConfirm,
        ["collection"] = c => SampleData.CollectionPopover(c, n => n.Tab = PopoverTab.Collection),
        ["catchlog"] = c => SampleData.CollectionPopover(c, n => { n.Tab = PopoverTab.Collection; n.ShowingCollectionLog = true; }),
        ["dexdetail"] = c => SampleData.CollectionPopover(c, n => n.OpenDexEntry("479"), withForms: true, preloadDetailsFor: 479),
        ["dexprofile"] = c => SampleData.CollectionPopover(c, n => n.OpenDexEntry("15"), preloadDetailsFor: 15),
        ["settings"] = c => SampleData.SettingsPopover(c, n => n.ShowSettings = true),
        ["settings-advanced"] = c => SampleData.SettingsPopover(c, n => n.OpenSessionKeySettings(), sessionKey: true),
        ["settings-snapshots"] = c => SampleData.SettingsPopover(c, n => n.ShowSettings = true, preset: s => SettingsView.Preset(s, st => st.SnapshotsExpanded = true)),
        ["recap"] = c => SampleData.SettingsPopover(c, n => n.ShowingRecap = true),
        ["recap-month"] = c => SampleData.SettingsPopover(c, n => n.ShowingRecap = true, preset: s => RecapScreen.Preset(s, RecapScope.Month)),
        ["recap-year"] = c => SampleData.SettingsPopover(c, n => n.ShowingRecap = true, preset: s => RecapScreen.Preset(s, RecapScope.Year)),
        ["recap-empty"] = c => Popover(c, n => n.ShowingRecap = true),
    };

    /// <summary>The whole popover with sample stores, after applying a navigation route.</summary>
    private static FrameworkElement Popover(Context c, Action<PopoverNavigation> route)
    {
        var s = SampleData.Services(c);
        route(s.Nav);
        return PopoverRoot.Build(s);
    }

    public static int Run(string[] args)
    {
        var name = args.ElementAtOrDefault(1);
        var output = args.ElementAtOrDefault(2);
        if (name == null || output == null || !Screens.TryGetValue(name, out var build))
        {
            Console.Error.WriteLine($"usage: --render <{string.Join("|", Screens.Keys)}> <out.png> [--light] [--lang ko]");
            return 2;
        }
        var dark = !args.Contains("--light");
        var langIndex = Array.IndexOf(args, "--lang");
        var lang = langIndex >= 0 ? AppLanguageExtensions.FromRawValue(args.ElementAtOrDefault(langIndex + 1)) ?? AppLanguage.En : AppLanguage.En;
        Theme.Apply(Application.Current.Resources, dark);
        var content = build(new Context(lang, dark));
        Save(Frame(content), output);
        return 0;
    }

    /// <summary>Wraps content the way the popover window does (background, padding, fixed width).</summary>
    public static FrameworkElement Frame(FrameworkElement content, double width = Theme.PopoverWidth)
    {
        var card = new Border { Child = content, Padding = new Thickness(Theme.PopoverPadding), Width = width };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Background);
        return card;
    }

    public static void Save(FrameworkElement element, string path, double scale = 2)
    {
        // Host in an offscreen window so DynamicResource lookups, Loaded events and timers behave as live.
        var window = new Window
        {
            Content = element, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000, AllowsTransparency = true,
            Background = Brushes.Transparent,
        };
        window.Show();
        // Let async sprite loads and layout settle before capturing.
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until)
        {
            Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(50);
        }
        element.UpdateLayout();
        var w = (int)Math.Ceiling(element.ActualWidth * scale);
        var h = (int)Math.Ceiling(element.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        encoder.Save(fs);
        window.Close();
    }

    private static FrameworkElement ControlsGallery(Context c)
    {
        var l = new L(c.Language);
        return Ui.VStack(12,
            SegmentedControl.Make([("home", l.Home), ("shop", l.Shop), ("bag", l.Bag), ("collection", l.Collection)], "home", _ => { }),
            Ui.HStack(12, new SpriteView(479, 84, form: null), Ui.VStack(4,
                Ui.HStack(6, Ui.Text("Rotom", Theme.FontSize.Title3, weight: FontWeights.SemiBold), Badge.Keyed("RARE", Theme.Keys.Blue)),
                Badge.Types([("electric", "Electric"), ("ghost", "Ghost")]),
                Ui.Text("Final form · Hardy", Theme.FontSize.Callout, Theme.Keys.Secondary),
                new LabeledProgressBar(0.08, "8%") { Width = 220, HorizontalAlignment = HorizontalAlignment.Left })),
            Ui.Divider(),
            Ui.HStack(8, Ui.Text("5-hour session", Theme.FontSize.Callout), Ui.Spacer(), Ui.Text("62%", Theme.FontSize.Callout, Theme.Keys.Green)),
            new LimitProgressBar(62, Theme.Keys.Green, 0.7),
            CapsuleTabBar.Make([new("claude_code", "Claude Code"), new("x", "Other")], "claude_code", _ => { }),
            Ui.HStack(8, Buttons.Small(l.Refresh, () => { }), Buttons.Small("Update", () => { }, prominent: true), Ui.Spacer(),
                Buttons.Icon(Ui.Glyphs.Hide, null, () => { }), Buttons.Icon(Ui.Glyphs.Settings, null, () => { }), Buttons.Icon(Ui.Glyphs.Power, null, () => { })));
    }
}
