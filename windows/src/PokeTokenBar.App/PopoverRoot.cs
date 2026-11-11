using System.Windows;
using System.Windows.Controls;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>PopoverView.body</c> / <c>mainContent</c>: Settings or Recap take over the whole popover;
/// otherwise update banner, the four-tab segmented picker, the tab's content, and the footer.
/// Home scrolls inside a fixed 520 pt height (the same height Shop/Bag/Collection use).
/// </summary>
public static class PopoverRoot
{
    public const double TabContentHeight = 520;

    public static FrameworkElement Build(AppServices s) => Live.View(s, () => Content(s));

    private static FrameworkElement Content(AppServices s)
    {
        var nav = s.Nav;
        if (nav.ShowSettings) return SettingsView.Build(s);
        if (nav.ShowingRecap) return RecapScreen.Build(s);

        var l = s.L;
        var picker = SegmentedControl.Make(
            [("home", l.Home), ("shop", l.Shop), ("bag", l.Bag), ("collection", l.Collection)],
            nav.Tab.ToString().ToLowerInvariant(),
            id => nav.Tab = Enum.Parse<PopoverTab>(id, ignoreCase: true));

        FrameworkElement tab = nav.Tab switch
        {
            PopoverTab.Collection => CollectionView.Build(s),
            PopoverTab.Bag => BagView.Build(s),
            PopoverTab.Shop => ShopView.Build(s),
            _ => Scrolling(HomeView.Build(s)),
        };
        return Ui.VStack(12, PopoverChrome.UpdateBanner(s), picker, tab, PopoverChrome.Footer(s));
    }

    /// <summary>
    /// A vertical scroll area with the popover's scroller lane: content keeps a right gutter so the
    /// thin scrollbar never covers right-aligned numbers (Swift <c>reservesScrollerLane</c>).
    /// </summary>
    public static ScrollViewer Scrolling(FrameworkElement content, double height = TabContentHeight)
    {
        content.Margin = new Thickness(0, 0, Theme.ScrollerInset, 0);
        return new ScrollViewer
        {
            Content = content,
            Height = height,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
    }
}
