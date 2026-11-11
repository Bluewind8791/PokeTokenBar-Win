using System.Windows;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Shop/Bag sample states for <see cref="RenderHarness"/>. Each starts from <see cref="Services"/> and edits
/// the in-memory companion state (the save lives in a temp file), then opens the popover on a tab.
/// </summary>
public static partial class SampleData
{
    /// <summary>Like the README shop screenshot: egg state (eggs listed but locked), 500M to spend.</summary>
    public static FrameworkElement ShopReference(RenderHarness.Context c) => OnTab(c, PopoverTab.Shop, s =>
    {
        var st = s.Companion.State;
        st.Active = null;
        st.SpentTokens = st.UsedSinceInstall - 500_000_000;
    });

    /// <summary>Active shiny Pokémon, items owned, Shiny Charm bought: owned counts, steppers, "Owned" seal.</summary>
    public static FrameworkElement ShopStocked(RenderHarness.Context c) => OnTab(c, PopoverTab.Shop, s =>
    {
        var st = s.Companion.State;
        st.SpentTokens = st.UsedSinceInstall - 2_600_000_000;
        st.Inventory[ItemKind.RareCandy.RawValue()] = 3;
        st.Inventory[ItemKind.Mint.RawValue()] = 2;
        st.Inventory[ItemKind.ShinyCharm.RawValue()] = 1;
        LoadLine(s.Companion);
    });

    /// <summary>
    /// Like the README bag screenshot: Charmander at stage 1 of 3 with 60M banked, so one candy evolves it and
    /// carries 35M over; 3 candies and 2 mints. The evolution line loads from PokéAPI (cached) first —
    /// candy use needs it.
    /// </summary>
    public static FrameworkElement BagReference(RenderHarness.Context c) => OnTab(c, PopoverTab.Bag, s =>
    {
        var st = s.Companion.State;
        st.Active = new MonState(4, [4], plannedPathIDs: [4, 5, 6], stageIndex: 0, usedAtStage: 60_000_000,
            rarity: Rarity.Common, totalForms: 3, nature: PokemonNature.Adamant);
        st.Inventory[ItemKind.RareCandy.RawValue()] = 3;
        st.Inventory[ItemKind.Mint.RawValue()] = 2;
        LoadLine(s.Companion);
    });

    /// <summary>Every item in the bag; the sample's single-stage Rotom is 50M short of graduating, so one candy
    /// graduates it and discards the rest (graduation + orange discarded-XP lines).</summary>
    public static FrameworkElement BagFull(RenderHarness.Context c) => OnTab(c, PopoverTab.Bag, s =>
    {
        var st = s.Companion.State;
        st.Active!.UsedAtStage = 2_950_000_000;
        st.Inventory[ItemKind.RareCandy.RawValue()] = 40;
        st.Inventory[ItemKind.Mint.RawValue()] = 1;
        st.Inventory[ItemKind.ShinyCharm.RawValue()] = 1;
        LoadLine(s.Companion);
    });

    /// <summary>
    /// Inline confirm states, reached through the real handlers: Mint stepper ×3 then Buy (confirm "Buy ×3"),
    /// plain egg Buy (confirm), uncommon egg Buy → Buy with a shiny companion (the second, orange warning).
    /// </summary>
    public static FrameworkElement ShopConfirm(RenderHarness.Context c) => ShopConfirm(c, scrollToEnd: false);

    /// <summary><see cref="ShopConfirm"/> scrolled to the end, where the shiny warning row is.</summary>
    public static FrameworkElement ShopWarning(RenderHarness.Context c) => ShopConfirm(c, scrollToEnd: true);

    private static FrameworkElement ShopConfirm(RenderHarness.Context c, bool scrollToEnd)
    {
        var root = ShopStocked(c);
        var s = (AppServices)root.Tag;
        if (s.Companion.State.Active is { } mon) mon.IsShiny = true;
        StepUp(root, times: 2);
        Click(root, b => b.Content as string == s.L.Buy);                                  // Mint
        Click(root, b => b.Content as string == s.L.Buy, skip: 1);                         // plain egg (after Rare Candy)
        Click(root, b => b.Content as string == s.L.Buy, skip: 2);                         // uncommon egg
        Click(root, b => b.Content as string == s.L.Buy && IsProminent(b), skip: 1);       // its confirm → warning
        if (scrollToEnd)
        {
            // After the harness's settle time has let late store rebuilds through (they reset the offset).
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Descendants<System.Windows.Controls.ScrollViewer>(root).FirstOrDefault()?.ScrollToEnd();
            };
            timer.Start();
        }
        return root;
    }

    /// <summary>Bag confirm: candy stepper ×2, then Use (confirm row with "Use ×2").</summary>
    public static FrameworkElement BagConfirm(RenderHarness.Context c)
    {
        var root = BagReference(c);
        var s = (AppServices)root.Tag;
        StepUp(root, times: 1);
        Click(root, b => b.Content as string == s.L.UseItem);
        return root;
    }

    private static bool IsProminent(System.Windows.Controls.Button b) => b.Foreground == System.Windows.Media.Brushes.White;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static void Click(FrameworkElement root, Func<System.Windows.Controls.Button, bool> pick, int skip = 0)
    {
        var button = Descendants<System.Windows.Controls.Button>(root).Where(pick).Skip(skip).FirstOrDefault();
        button?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    /// <summary>Presses the first stepper's up half (the first stepper hit area holding a chevron).</summary>
    private static void StepUp(FrameworkElement root, int times)
    {
        for (var i = 0; i < times; i++)
        {
            var up = Descendants<System.Windows.Controls.Border>(root).FirstOrDefault(b => b.Child is System.Windows.Shapes.Path);
            up?.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        }
    }

    private static FrameworkElement OnTab(RenderHarness.Context c, PopoverTab tab, Action<AppServices> edit)
    {
        var s = Services(c);
        edit(s);
        s.Nav.Tab = tab;
        var root = PopoverRoot.Build(s);
        root.Tag = s;   // lets the confirm samples reach the services
        return root;
    }

    /// <summary>Kicks the companion's line load (the app does it on the first usage update) and waits for it.</summary>
    private static void LoadLine(CompanionStore companion)
    {
        companion.Update(new Dictionary<string, long> { ["claude_code"] = 0 }, LocalUsageReader.LocalDay(Now), 0,
            BurnTier.Idle, limitWarning: false, hasUsageData: true);
        var until = DateTime.UtcNow.AddSeconds(15);
        Pump(Task.Run(async () =>
        {
            while (companion.CurrentLine == null && DateTime.UtcNow < until) await Task.Delay(50);
        }));
    }
}
