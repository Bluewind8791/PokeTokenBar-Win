using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PokeTokenBar.App;

/// <summary>
/// The SwiftUI observation stand-in: a host that re-runs a view builder whenever the usage store,
/// the companion store, the navigation or the update checker reports a change (coalesced to one
/// rebuild per dispatcher turn). Vertical scroll offsets of the rebuilt subtree are carried over,
/// so a refresh while the user is scrolled does not jump the view back to the top.
/// </summary>
public sealed class Live : ContentControl
{
    private readonly Func<FrameworkElement> _build;
    private bool _pending;

    private Live(AppServices s, Func<FrameworkElement> build)
    {
        _build = build;
        Focusable = false;
        Content = build();
        void Changed() => Schedule();
        Loaded += (_, _) =>
        {
            s.Store.Changed += Changed;
            s.Companion.Changed += Changed;
            s.Nav.Changed += Changed;
            s.Updater.Changed += Changed;
        };
        Unloaded += (_, _) =>
        {
            s.Store.Changed -= Changed;
            s.Companion.Changed -= Changed;
            s.Nav.Changed -= Changed;
            s.Updater.Changed -= Changed;
        };
    }

    /// <summary>Wraps a view builder so it stays current.</summary>
    public static Live View(AppServices s, Func<FrameworkElement> build) => new(s, build);

    /// <summary>Rebuild now (for local view state changes, e.g. a toggle inside the view).</summary>
    public void Rebuild() => Schedule();

    private void Schedule()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            _pending = false;
            var offsets = ScrollViewers(Content as DependencyObject).Select(v => v.VerticalOffset).ToList();
            Content = _build();
            if (offsets.Count == 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var viewers = ScrollViewers(Content as DependencyObject).ToList();
                for (var i = 0; i < Math.Min(viewers.Count, offsets.Count); i++) viewers[i].ScrollToVerticalOffset(offsets[i]);
            });
        });
    }

    private static IEnumerable<ScrollViewer> ScrollViewers(DependencyObject? root)
    {
        if (root == null) yield break;
        if (root is ScrollViewer sv && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled) yield return sv;
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
            foreach (var child in ScrollViewers(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }

    /// <summary>Stand-in for a screen that has not been ported yet.</summary>
    public static FrameworkElement Placeholder(string name) =>
        Ui.Text($"{name} — not ported yet", Theme.FontSize.Callout, Theme.Keys.Tertiary).Padding(0, 20, 0, 20);
}
