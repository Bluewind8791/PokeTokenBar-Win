using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>CompanionHeader</c> (CompanionView.swift): the current Pokémon, its growth, and the
/// hatch/evolution celebrations at the top of Home.
/// </summary>
public static class CompanionHeader
{
    // Celebrations play once per sequence number. These outlive one build: the popover rebuilds the
    // header on every store change, and SwiftUI's @State survived those re-renders.
    private static int _seenSeq = -1, _seenCandySeq = -1, _seenMintSeq = -1;
    // A load notifies the store, and that rebuilds this view: without a throttle a failing request
    // (offline) would retry on every rebuild. SwiftUI's .task ran once per appearance instead.
    private static readonly Dictionary<string, DateTime> LastRequest = [];
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private static bool ShouldRequest(string key)
    {
        var now = DateTime.UtcNow;
        if (LastRequest.TryGetValue(key, out var last) && now - last < RetryAfter) return false;
        LastRequest[key] = now;
        return true;
    }

    /// <param name="onOpenDexEntry">Opens a species' Pokédex page; null leaves the sprites inert.</param>
    public static FrameworkElement Build(CompanionStore store, Action<string>? onOpenDexEntry)
    {
        var l = store.L;
        // Computed once per render and shared by the big sprite and the evolution line.
        var dexLinks = onOpenDexEntry == null ? new Dictionary<int, string>() : store.DexLinkTargets;
        var eggImminent = store.IsEgg && store.EggProgress >= 0.9;

        var sprite = SpriteBox(store, eggImminent, dexLinks, onOpenDexEntry);
        var info = InfoColumn(store, l, eggImminent);

        // HStack(spacing: 12) { sprite; info; Spacer() } — the info column takes the rest of the row.
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sprite.VerticalAlignment = VerticalAlignment.Center;
        info.VerticalAlignment = VerticalAlignment.Center;
        info.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetColumn(info, 1);
        row.Children.Add(sprite);
        row.Children.Add(info);

        var column = Ui.VStack(6, row);
        if (store.HasActive && store.LineNodes is { Count: > 0 } nodes)
        {
            // Without a width a branching line (Eevee) overflows and clips the whole popover sideways.
            column.Children.Add(EvoLineView.Build(nodes, l.UnknownNextEvolution, store.Language, store.CurrentIsShiny,
                Theme.ScrollContentWidth, store.CurrentForm, dexLinks, onOpenDexEntry));
        }
        if (store.JustGraduated is { } g)
            column.Children.Add(Ui.Text(l.Graduated(g), Theme.FontSize.Caption2, Theme.Keys.Orange));

        LoadMetadata(store);
        return column;
    }

    /// <summary>
    /// Types come from PokéAPI details (cached after the first fetch) — same ID as the sprite, so a disguised
    /// Ditto shows its disguise's types. The title names the form, so its names load too.
    /// </summary>
    private static void LoadMetadata(CompanionStore store)
    {
        if (!store.HasActive || store.CurrentSpeciesID is not { } id) return;
        if (!store.PokemonDetailsByID.ContainsKey(id) && ShouldRequest($"details-{id}"))
            _ = store.LoadPokemonDetails(id);
        if (ShouldRequest($"forms-{id}-{store.CurrentForm?.RawValue}"))
            _ = store.LoadActiveFormNames();
        if (store.PokemonDetailsByID.TryGetValue(id, out var details))
        {
            var missing = details.Types.Select(TypeResource).Where(r => !store.FormNames.ContainsKey(r)
                && ShouldRequest("type-" + r.Name)).ToList();
            if (missing.Count > 0) _ = store.LoadFormNames(missing);
        }
    }

    private static PokemonNameResource TypeResource(string type) => new(PokemonNameResource.ResourceKind.Type, type);

    /// <summary>Swift <c>PokemonNameLabel(.type, …)</c>: the localized type name, the identifier until it loads.</summary>
    private static string TypeName(CompanionStore store, string type) =>
        store.FormNames.TryGetValue(TypeResource(type), out var names) && store.Language.ResolveName(names) is { } name
            ? name : PokemonNameLocalization.Identifier(type);

    private static FrameworkElement SpriteBox(CompanionStore store, bool eggImminent, Dictionary<int, string> dexLinks,
        Action<string>? onOpenDexEntry)
    {
        var sprite = new SpriteView(store.CurrentSpeciesID, 76, bob: true, animated: true, shiny: store.CurrentIsShiny,
            form: store.CurrentForm, back: store.FacesBack);
        var box = new Border
        {
            Width = 76, Height = 76, CornerRadius = new CornerRadius(12), Child = sprite,
            Background = HomeLayout.Tint(Theme.Keys.Primary, 0.07),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var rotate = new RotateTransform();
        var scale = new ScaleTransform(1, 1);
        box.RenderTransform = new TransformGroup { Children = { rotate, scale } };
        // Egg about to hatch (90%+): it wobbles.
        if (eggImminent)
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-5, 5, TimeSpan.FromSeconds(0.35))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });

        var flash = new Border { CornerRadius = new CornerRadius(12), Background = Brushes.White, Opacity = 0, IsHitTestVisible = false };
        var effects = new Grid { IsHitTestVisible = false, ClipToBounds = false };
        var stack = new Grid { Width = 76, Height = 76 };
        stack.Children.Add(box);
        stack.Children.Add(flash);
        stack.Children.Add(effects);

        PlayCelebrationIfNeeded(store, scale, flash, effects);
        ShowCandyXPIfNeeded(store, effects);
        ShowMintIfNeeded(store, effects);

        // Outermost, so the celebration overlays above stay intact. An egg has no species and no link.
        var target = store.CurrentSpeciesID is { } id ? dexLinks.GetValueOrDefault(id) : null;
        return DexEntryLink.Wrap(stack, target, store.L.DexOpenEntryHint, 12, onOpenDexEntry);
    }

    private static FrameworkElement InfoColumn(CompanionStore store, L l, bool eggImminent)
    {
        var column = Ui.VStack(4);
        var title = Ui.HStack(6, Ui.Text(store.DisplayName, Theme.FontSize.Callout, weight: FontWeights.SemiBold));
        if (store.CurrentIsShiny) title.Children.Add(Emoji("✨", 11));
        if (store.Rarity is { } r)
            title.Children.Add(Badge.Keyed(l.RarityLabel(r.RawValue()).ToUpperInvariant(), PokemonTypeColors.RarityKey(r)));
        title.HorizontalAlignment = HorizontalAlignment.Left;
        column.Children.Add(title);

        if (store.HasActive && store.CurrentSpeciesID is { } id && store.PokemonDetailsByID.TryGetValue(id, out var details))
        {
            var types = Badge.Types(details.Types.Select(t => (t, TypeName(store, t))));
            types.HorizontalAlignment = HorizontalAlignment.Left;
            column.Children.Add(types);
            if (!PokemonForm.IsDefault(store.CurrentForm, id))
                column.Children.Add(Ui.Text(l.DefaultFormDataNote, Theme.FontSize.Caption2, Theme.Keys.Secondary).Wrapping());
        }

        if (store.HasActive)
        {
            // Stage + nature (the individual's identity fixed at hatch).
            var nature = store.CurrentNature is { } n ? " · " + n.Name(store.Language) : "";
            var stage = Ui.HStack(5, Ui.Text(store.StageText + nature, Theme.FontSize.Caption2, Theme.Keys.Secondary));
            if (store.GrowthMultiplier is { } multiplier)
                stage.Children.Add(Badge.Make(l.GrowthBoost(multiplier), Theme.Brush(Theme.Keys.Orange), solid: false));
            stage.HorizontalAlignment = HorizontalAlignment.Left;
            column.Children.Add(stage);
            column.Children.Add(new LabeledProgressBar(store.Progress,
                LimitPace.ProgressDetail(store.State.Active?.UsedAtStage ?? 0, store.Threshold)));
            if (store.TokensToNext > 0)
            {
                var amount = TokenFormatter.Compact(store.TokensToNext);
                column.Children.Add(Ui.Text(store.IsFinalStage ? l.ToGraduation(amount) : l.ToNextEvolution(amount),
                    Theme.FontSize.Caption2, Theme.Keys.Tertiary));
            }
        }
        else
        {
            // Incubation — progress to hatching; wording and color change when it is close.
            var line = Ui.HStack(6, Ui.Text(eggImminent ? l.EggImminent : l.EggIncubating, Theme.FontSize.Caption2,
                eggImminent ? Theme.Keys.Orange : Theme.Keys.Secondary));
            // A guaranteed-tier egg says what it holds — the only signal, since every egg has one sprite.
            if (store.EggGuarantee is { } guarantee)
                line.Children.Add(Badge.Keyed(l.EggGuaranteeHint(guarantee.RawValue()), PokemonTypeColors.RarityKey(guarantee)));
            line.HorizontalAlignment = HorizontalAlignment.Left;
            column.Children.Add(line);
            column.Children.Add(new LabeledProgressBar(store.EggProgress,
                LimitPace.ProgressDetail(store.State.EggUsage, store.EggHatchThreshold)));
            if (store.IsEgg && store.IsHatchRetryDelayed)
                column.Children.Add(Ui.Text(l.EggHatchDelayed, Theme.FontSize.Caption2, Theme.Keys.Orange).Wrapping());
            else
                column.Children.Add(Ui.Text(l.EggToHatch(TokenFormatter.Compact(store.EggTokensToHatch)),
                    Theme.FontSize.Caption2, Theme.Keys.Tertiary));
            // First run (nothing banked yet): one line so a still egg does not look broken.
            if (!store.EggStarted)
                column.Children.Add(Ui.Text(l.EggFirstRunHint(TokenFormatter.Compact(store.EggHatchThreshold)),
                    Theme.FontSize.Caption2, Theme.Keys.Tertiary).Wrapping());
        }
        column.Children.Add(Ui.Text(StatusLine(store), Theme.FontSize.Caption2, Theme.Keys.Secondary));
        return column;
    }

    private static string StatusLine(CompanionStore store)
    {
        var l = store.L;
        return store.DisplayState switch
        {
            CompanionStateKind.Egg => l.StatusEgg,
            CompanionStateKind.Idle => l.StatusIdle,
            CompanionStateKind.Working => l.StatusWorking,
            CompanionStateKind.Focus => l.StatusFocus,
            CompanionStateKind.Tired => l.StatusTired,
            CompanionStateKind.Sleep => l.StatusSleep,
            CompanionStateKind.Sulk => l.StatusSulk,
            _ => store.JustEvolvedTo is { } name ? l.StatusEvolved(name) : l.StatusGrew,
        };
    }

    // MARK: Celebrations

    /// <summary>
    /// Hatch/evolution, once: a white flash fading out and a spring pop; a shiny hatch adds a ✨ burst, a Ditto
    /// reveal a 🎭 burst. Consumed after the effect ends (not at once as on macOS): consuming notifies the
    /// store, and the rebuild that follows would replace this header mid-animation.
    /// </summary>
    private static void PlayCelebrationIfNeeded(CompanionStore store, ScaleTransform scale, Border flash, Grid effects)
    {
        if (store.CurrentCelebration is not { } c || store.CelebrationSeq == _seenSeq) return;
        _seenSeq = store.CelebrationSeq;
        flash.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.85, 0, TimeSpan.FromSeconds(0.8)) { EasingFunction = new QuadraticEase() });
        var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromSeconds(0.6)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        if (c.Kind == CompanionStore.CelebrationKind.Hatch && c.Shiny)
            Burst(effects, Emoji("✨", 22), HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, -6, -6, 0), 0.3);
        if (c.Kind == CompanionStore.CelebrationKind.DittoReveal)
        {
            Burst(effects, Emoji("🎭", 26), HorizontalAlignment.Center, VerticalAlignment.Top, new Thickness(0, -12, 0, 0), 0.25);
            if (c.Shiny) Burst(effects, Emoji("✨", 22), HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, -6, -6, 0), 0.45);
        }
        After(3.1, store.ConsumeCelebration);
    }

    /// <summary>Rare candy "+XP" once (also for partial progress without evolving).</summary>
    private static void ShowCandyXPIfNeeded(CompanionStore store, Grid effects)
    {
        if (store.CandyFeedbackAmount <= 0 || store.CandyFeedbackSeq == _seenCandySeq) return;
        _seenCandySeq = store.CandyFeedbackSeq;
        var label = Ui.Text($"+{TokenFormatter.Compact(store.CandyFeedbackAmount)} XP", Theme.FontSize.Caption, Theme.Keys.Orange, FontWeights.Bold);
        var pill = Capsule.Round(new Border { Child = label, Padding = new Thickness(6, 2, 6, 2), Background = HomeLayout.Tint(Theme.Keys.Background, 0.92) });
        Burst(effects, pill, HorizontalAlignment.Center, VerticalAlignment.Top, new Thickness(0, -16, 0, 0), 0, hold: 1.3, slide: true);
        After(1.8, store.ConsumeCandyFeedback);
    }

    /// <summary>Mint: a short sparkle, no text — same one-shot contract as the candy.</summary>
    private static void ShowMintIfNeeded(CompanionStore store, Grid effects)
    {
        if (store.MintFeedbackNature == null || store.MintFeedbackSeq == _seenMintSeq) return;
        _seenMintSeq = store.MintFeedbackSeq;
        var sparkles = new Grid();
        foreach (var (size, x, y) in new[] { (22.0, -11.0, -9.0), (15.0, 13.0, 5.0), (12.0, 1.0, 13.0) })
        {
            var s = Emoji("✨", size);
            s.HorizontalAlignment = HorizontalAlignment.Center;
            s.RenderTransform = new TranslateTransform(x, y);
            sparkles.Children.Add(s);
        }
        Burst(effects, sparkles, HorizontalAlignment.Center, VerticalAlignment.Center, new Thickness(0), 0, hold: 0.9);
        After(1.3, store.ConsumeMintFeedback);
    }

    /// <summary>Scale+fade in (after <paramref name="delay"/>), hold, fade out — the SwiftUI transitions.</summary>
    private static void Burst(Grid host, FrameworkElement element, HorizontalAlignment h, VerticalAlignment v, Thickness margin,
        double delay, double hold = 2.6, bool slide = false)
    {
        element.HorizontalAlignment = h;
        element.VerticalAlignment = v;
        element.Margin = margin;
        element.Opacity = 0;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(slide ? 1 : 0.2, slide ? 1 : 0.2);
        var move = new TranslateTransform(0, slide ? 8 : 0);
        var group = new TransformGroup();
        if (element.RenderTransform is TranslateTransform own) group.Children.Add(own);
        group.Children.Add(scale);
        group.Children.Add(move);
        element.RenderTransform = group;
        host.Children.Add(element);
        var begin = TimeSpan.FromSeconds(delay);
        var spring = new ElasticEase { Oscillations = 1, Springiness = 5 };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            BeginTime = begin,
            KeyFrames =
            {
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.2))),
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold))),
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold + 0.45))),
            },
        });
        if (slide)
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromSeconds(0.35)) { BeginTime = begin, EasingFunction = spring });
        else
        {
            var grow = new DoubleAnimation(0.2, 1, TimeSpan.FromSeconds(0.4)) { BeginTime = begin, EasingFunction = spring };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    private static void After(double seconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    /// <summary>An emoji glyph. WPF draws emoji in monochrome (no color font support).</summary>
    public static TextBlock Emoji(string text, double size) =>
        new() { Text = text, FontSize = size, FontFamily = new FontFamily("Segoe UI Emoji"), VerticalAlignment = VerticalAlignment.Center };
}

/// <summary>
/// Port of <c>DexEntryLink</c>: makes a sprite open its Pokédex page. With no target (egg, uncaught species,
/// "?" node) the content is returned untouched — no hover highlight, hand cursor or tooltip — so only real
/// links look clickable. The highlight is a background, so it never changes the layout.
/// </summary>
public static class DexEntryLink
{
    public static FrameworkElement Wrap(FrameworkElement content, string? target, string hint, double cornerRadius,
        Action<string>? open)
    {
        if (target == null || open == null) return content;
        var link = new Border { Child = content, CornerRadius = new CornerRadius(cornerRadius), Background = Brushes.Transparent };
        var hover = HomeLayout.Tint(Theme.Keys.Primary, 0.1);
        link.MouseEnter += (_, _) => link.Background = hover;
        link.MouseLeave += (_, _) => link.Background = Brushes.Transparent;
        System.Windows.Automation.AutomationProperties.SetName(link, hint);
        return link.OnClick(() => open(target)).Help(hint);
    }
}

/// <summary>
/// Port of <c>EvoLineView</c>: the evolution line (done → current → future preview). A branching line is
/// long (Eevee: 9 nodes ≈ 472 pt); past <c>maxWidth</c> it scrolls sideways with edge fades and chevrons
/// instead of widening the popover.
/// </summary>
public static class EvoLineView
{
    private const double Spacing = 2;
    /// <summary>Arrow slot = thumbnail × this; fixed, so the row width does not depend on the glyph.</summary>
    private const double ArrowRatio = 0.25;
    private const double NameSlack = 6;
    private const double FadeWidth = 24;

    /// <summary>Row width from the same formula as the layout (regression-tested on macOS).</summary>
    public static double RowWidth(int count, double thumb, bool hasNames)
    {
        if (count <= 0) return 0;
        var column = thumb + (hasNames ? NameSlack : 0);
        var arrows = (count - 1) * thumb * ArrowRatio;
        var gaps = (2 * count - 2) * Spacing;
        return count * column + arrows + gaps;
    }

    public static bool NeedsScroll(int count, double thumb, bool hasNames, double maxWidth) =>
        double.IsFinite(maxWidth) && maxWidth > 0 && RowWidth(count, thumb, hasNames) > maxWidth;

    /// <summary>Index a chevron press scrolls to: one screenful from the current left-most cell.</summary>
    public static int PageTarget(bool forward, double scrollX, int count, double thumb, bool hasNames, double maxWidth)
    {
        if (count <= 0) return 0;
        var stride = thumb + (hasNames ? NameSlack : 0) + thumb * ArrowRatio + Spacing * 2;
        var visible = Math.Max(1, (int)(maxWidth / stride));
        var first = Math.Max(0, (int)Math.Round(scrollX / stride));
        return Math.Min(Math.Max(0, first + (forward ? visible : -visible)), count - 1);
    }

    /// <summary>Which dex page a node opens — only species already in the dex.</summary>
    public static string? DexLinkTarget(EvoLineItem node, IReadOnlyDictionary<int, string> links) =>
        node.Content.SpeciesID is { } id ? links.GetValueOrDefault(id) : null;

    public static FrameworkElement Build(IReadOnlyList<EvoLineItem> nodes, string mysteryLabel, AppLanguage language,
        bool shiny, double maxWidth, PokemonForm? form, IReadOnlyDictionary<int, string> dexLinks,
        Action<string>? onOpenDexEntry, double thumb = 40)
    {
        var row = Row(nodes, mysteryLabel, language, shiny, form, dexLinks, onOpenDexEntry, thumb);
        if (!NeedsScroll(nodes.Count, thumb, false, maxWidth)) return row;
        return ScrollableRow(row, nodes.Count, thumb, maxWidth, language);
    }

    private static Stack Row(IReadOnlyList<EvoLineItem> nodes, string mysteryLabel, AppLanguage language, bool shiny,
        PokemonForm? form, IReadOnlyDictionary<int, string> dexLinks, Action<string>? onOpenDexEntry, double thumb)
    {
        var row = Ui.HStack(Spacing);
        row.HorizontalAlignment = HorizontalAlignment.Left;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (i > 0)
            {
                var arrow = Ui.Icon("", thumb * 0.2, Theme.Keys.Tertiary);   // arrow.right
                arrow.Width = thumb * ArrowRatio;
                arrow.TextAlignment = TextAlignment.Center;
                arrow.VerticalAlignment = VerticalAlignment.Top;
                arrow.Margin = new Thickness(0, thumb * 0.4, 0, 0);   // centred on the sprite
                row.Children.Add(arrow);
            }
            FrameworkElement content;
            if (node.Content.SpeciesID is { } id)
                content = new SpriteView(id, thumb, shiny: shiny, form: form);
            else
            {
                var mystery = new TextBlock
                {
                    Text = "?", FontSize = thumb * 0.55, FontWeight = FontWeights.Bold, FontFamily = Theme.DisplayFont,
                    Width = thumb, Height = thumb, TextAlignment = TextAlignment.Center, LineHeight = thumb,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                };
                mystery.SetResourceReference(TextBlock.ForegroundProperty, Theme.Keys.Primary);
                System.Windows.Automation.AutomationProperties.SetName(mystery, mysteryLabel);
                content = mystery;
            }
            // Future stages are faded (macOS also desaturates them; WPF has no saturation filter).
            if (node.State == EvoLineItemState.Future) content.Opacity = 0.32;
            var cell = new Grid { Width = thumb, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = false };
            cell.Children.Add(content);
            if (node.State == EvoLineItemState.Current)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 4, Height = 4, VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, -2),
                };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, Theme.Keys.Accent);
                cell.Children.Add(dot);
            }
            // After the fixed width, so the link only adds a hover background — column widths stay as measured.
            var target = onOpenDexEntry == null ? null : DexLinkTarget(node, dexLinks);
            var linked = DexEntryLink.Wrap(cell, target, new L(language).DexOpenEntryHint, 6, onOpenDexEntry);
            linked.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(linked);
        }
        return row;
    }

    private static FrameworkElement ScrollableRow(Stack row, int count, double thumb, double maxWidth, AppLanguage language)
    {
        // Fades and chevrons signal the overflow; a scrollbar would also eat row height.
        var scroller = new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Width = maxWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Focusable = false,
        };
        var back = Chevron(false, thumb, language);
        var forward = Chevron(true, thumb, language);
        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        host.Children.Add(scroller);
        host.Children.Add(back);
        host.Children.Add(forward);

        void Update()
        {
            var content = scroller.ExtentWidth > 0 ? scroller.ExtentWidth : RowWidth(count, thumb, false);
            var scrollable = content > maxWidth + 0.5;
            var canBack = scrollable && scroller.HorizontalOffset > 0.5;
            var canForward = scrollable && scroller.HorizontalOffset < content - maxWidth - 0.5;
            back.Visibility = canBack ? Visibility.Visible : Visibility.Collapsed;
            forward.Visibility = canForward ? Visibility.Visible : Visibility.Collapsed;
            // Fade only the edge that has more to show — "continues", not "cut off".
            var f = FadeWidth / maxWidth;
            scroller.OpacityMask = new LinearGradientBrush(new GradientStopCollection
            {
                new(canBack ? Colors.Transparent : Colors.Black, 0),
                new(Colors.Black, f),
                new(Colors.Black, 1 - f),
                new(canForward ? Colors.Transparent : Colors.Black, 1),
            }, new Point(0, 0), new Point(1, 0));
        }
        void Page(bool isForward)
        {
            var index = PageTarget(isForward, scroller.HorizontalOffset, count, thumb, false, maxWidth);
            scroller.ScrollToHorizontalOffset(index * (thumb + thumb * ArrowRatio + Spacing * 2));
        }
        back.OnClick(() => Page(false));
        forward.OnClick(() => Page(true));
        scroller.ScrollChanged += (_, _) => Update();
        Update();
        return host;
    }

    private static Border Chevron(bool forward, double thumb, AppLanguage language)
    {
        var glyph = Ui.Icon(forward ? "" : "", 8, Theme.Keys.Secondary);
        glyph.FontWeight = FontWeights.Bold;
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var button = new Border
        {
            Child = glyph, Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), BorderBrush = HomeLayout.Tint(Theme.Keys.Primary, 0.12),
            HorizontalAlignment = forward ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(forward ? 0 : 1, Math.Max(0, thumb / 2 - 8), forward ? 1 : 0, 0),   // centred on the sprite
        };
        button.SetResourceReference(Border.BackgroundProperty, Theme.Keys.ControlSelected);
        var l = new L(language);
        System.Windows.Automation.AutomationProperties.SetName(button, forward ? l.EvolutionScrollNext : l.EvolutionScrollPrevious);
        return button;
    }
}
