using System.Windows;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of the Bag tab (BagView.swift): owned item cards with inline-confirmed use. The empty bag shows
/// an animated Snorlax (the Collection's Pikachu pattern).
/// </summary>
public static class BagView
{
    /// <summary>Per-card <c>@State</c>.</summary>
    private sealed class CardState
    {
        public bool Confirming;
        public int CandyCount = 1;
    }

    public static FrameworkElement Build(AppServices s)
    {
        var store = s.Companion;
        var owned = store.OwnedItems;
        if (owned.Count == 0) return EmptyState(s);
        var state = ScreenState<CardState>.For(s.Nav, PopoverTab.Bag);
        var list = Ui.VStack(8);
        foreach (var (kind, count) in owned)
            list.Children.Add(new CardHost(h => ItemCard(s, kind, count, state[kind.RawValue()], h)));
        // Fixed height — same as Collection, so reopening the popover does not shrink it to fit.
        return PopoverRoot.Scrolling(list);
    }

    /// <summary>Empty bag — animated Snorlax (143) and a hint that names no item (room for more items).</summary>
    private static FrameworkElement EmptyState(AppServices s)
    {
        var l = s.L;
        var snorlax = new SpriteView(143, 96, animated: true) { HorizontalAlignment = HorizontalAlignment.Center };
        var title = Ui.Text(l.BagEmptyTitle, Theme.FontSize.Callout, Theme.Keys.Primary, FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var shop = ShopUi.Button(l.Shop, () => s.Nav.Tab = PopoverTab.Shop);
        shop.HorizontalAlignment = HorizontalAlignment.Center;
        return Ui.VStack(10, snorlax, title, shop).Padding(0, 28, 0, 28);
    }

    /// <summary>One item — icon, name, count, description, and an inline-confirmed use.</summary>
    private static FrameworkElement ItemCard(AppServices s, ItemKind kind, int count, CardState st, CardHost host)
    {
        var store = s.Companion;
        var l = s.L;
        var maxCandies = Math.Max(1, store.MaxRareCandyUseCount);
        // Clamp when the usable count drops (Swift onChange(of: maxRareCandyUseCount)).
        st.CandyCount = Math.Min(st.CandyCount, maxCandies);
        var selected = st.CandyCount;
        var name = l.ItemName(kind.RawValue());

        var nameRow = ShopUi.Row(
            Ui.HStack(6,
                Ui.Text(name, Theme.FontSize.Callout, Theme.Keys.Primary, FontWeights.SemiBold),
                // Passive items have no count (bought once, permanent).
                kind.IsPassive() ? null : Ui.Text($"×{count}", Theme.FontSize.Caption, Theme.Keys.Secondary, FontWeights.Bold, monospacedDigits: true)),
            4,
            kind == ItemKind.RareCandy && store.CanUseRareCandy
                ? ShopUi.Stepper(selected, 1, maxCandies, v => { st.CandyCount = v; host.Refresh(); }, l.ItemName(ItemKind.RareCandy.RawValue()))
                : null);

        var info = Ui.VStack(2, nameRow, ShopUi.Wrapped(l.ItemDescription(kind.RawValue()), Theme.FontSize.Caption, Theme.Keys.Secondary));
        var card = Ui.VStack(8, ShopUi.IconHeader(ShopUi.ItemIcon(kind, 30), info));

        if (kind == ItemKind.RareCandy && store.PlanRareCandyUse(selected) is { } preview)
        {
            if (preview.Graduates)
                card.Children.Add(Ui.Text(l.CandyGraduates, Theme.FontSize.Caption2, Theme.Keys.Secondary));
            if (preview.DiscardedXP > 0)
                card.Children.Add(ShopUi.Wrapped(l.CandyDiscardedXP(TokenFormatter.Compact(preview.DiscardedXP)), Theme.FontSize.Caption2, Theme.Keys.Orange));
            else if (preview.Evolves && !preview.Graduates)
                card.Children.Add(ShopUi.Wrapped(l.CandyCarryoverXP(TokenFormatter.Compact(preview.CarryoverXP)), Theme.FontSize.Caption2, Theme.Keys.Secondary));
        }
        card.Children.Add(UseControls(s, kind, st, host, selected));
        return ShopUi.Card(card);
    }

    private static FrameworkElement UseControls(AppServices s, ItemKind kind, CardState st, CardHost host, int selected)
    {
        var store = s.Companion;
        var l = s.L;
        if (kind.IsPassive())
            // Passive (Shiny Charm): an always-on effect instead of a use button.
            return ShopUi.SealLine(l.ShinyCharmEffectHint, 6);

        // Usable now? Candy needs the line loaded; mint only needs an active Pokémon.
        var canUse = kind switch
        {
            ItemKind.RareCandy => store.CanUseRareCandy,
            ItemKind.Mint => store.CanUseMint,
            _ => false,
        };
        if (!canUse)
            // Egg (not hatched) / no active Pokémon / (candy only) line not loaded — disabled with the reason.
            return Ui.Text(store.IsEgg ? l.UseAfterHatch : l.UseNeedsPokemon, Theme.FontSize.Caption2, Theme.Keys.Tertiary);

        if (st.Confirming)
            return ShopUi.Row(
                ShopUi.Lines(l.UseOnCurrent(store.DisplayName), Theme.FontSize.Caption2, Theme.Keys.Secondary, 1),
                8,
                ShopUi.Button(kind == ItemKind.RareCandy ? $"{l.Use} ×{selected}" : l.Use, () => UseNow(s, kind, st, selected), prominent: true),
                ShopUi.Borderless(l.Cancel, () => { st.Confirming = false; host.Refresh(); }));

        // Effect hint ("+XP" / "random nature").
        var hint = kind == ItemKind.RareCandy
            ? $"+{TokenFormatter.Compact(selected * RareCandy.Xp)} XP"
            : l.MintEffectHint;
        return ShopUi.Row(
            Ui.Text(hint, Theme.FontSize.Caption2, Theme.Keys.Tertiary, monospacedDigits: true),
            8,
            ShopUi.Button(l.UseItem, () => { st.Confirming = true; host.Refresh(); }));
    }

    /// <summary>Use, then always switch to Home: the evolution/graduation celebration, the "+XP" and the
    /// nature-change toast all play in Home's companion header.</summary>
    private static void UseNow(AppServices s, ItemKind kind, CardState st, int selected)
    {
        st.Confirming = false;
        switch (kind)
        {
            case ItemKind.RareCandy: s.Companion.UseRareCandy(selected); break;
            case ItemKind.Mint: s.Companion.UseMint(); break;
            // Shiny Charm is passive — nothing to use.
        }
        s.Nav.Tab = PopoverTab.Home;
    }
}
