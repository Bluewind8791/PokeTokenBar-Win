using System.Windows;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of the Shop tab (ShopView.swift): spend used tokens (wallet = usedSinceInstall − spentTokens) on
/// items and eggs. Confirmation is inline (the button morphs) — never a modal: in the original an orphaned
/// sheet left behind by the transient popover swallowed later clicks.
/// </summary>
public static class ShopView
{
    /// <summary>Per-card <c>@State</c>: item cards use Confirming/Quantity, egg cards use Stage.</summary>
    private sealed class CardState
    {
        public bool Confirming;
        public int Quantity = 1;
        public EggStage Stage = EggStage.Idle;
    }

    private enum EggStage { Idle, Confirm, PreciousConfirm }

    public static FrameworkElement Build(AppServices s)
    {
        var store = s.Companion;
        var l = s.L;
        var state = ScreenState<CardState>.For(s.Nav, PopoverTab.Shop);
        var list = Ui.VStack(10, WalletHeader(store, l));
        // ShopEntries = items for sale + the three eggs merged into one list by ascending price. Eggs are
        // always listed (instant actions, not ItemKinds); in the egg state the egg card only disables buying.
        foreach (var entry in store.ShopEntries)
        {
            if (entry.Item is { } kind)
                list.Children.Add(new CardHost(h => ItemCard(s, kind, state[$"item-{kind.RawValue()}"], h)));
            else
                list.Children.Add(new CardHost(h => EggCard(s, entry.EggTier, state[$"egg-{entry.EggTier?.RawValue() ?? "none"}"], h)));
        }
        // Fixed height — same as Collection/Bag, so reopening the popover does not shrink it to fit.
        return PopoverRoot.Scrolling(list);
    }

    private static FrameworkElement WalletHeader(CompanionStore store, L l)
    {
        var amount = Ui.Text(TokenFormatter.Compact(store.AvailableTokens), 24, Theme.Keys.Primary, FontWeights.Bold, monospacedDigits: true);
        amount.FontFamily = Theme.DisplayFont;
        // SF's 24 pt line is ~29 pt; Segoe's default (~32) made the header noticeably taller.
        amount.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        amount.LineHeight = 29;
        return ShopUi.Card(Ui.VStack(2,
            Ui.Text(l.SpendableTokens, Theme.FontSize.Caption, Theme.Keys.Secondary),
            amount,
            Ui.Text(l.ShopHint, Theme.FontSize.Caption2, Theme.Keys.Tertiary)));
    }

    /// <summary>
    /// One item for sale — icon, name, description, owned count, then price / buy with inline confirm.
    /// Consumables get a quantity stepper (the Bag's batch candy pattern) to buy up to the balance at once.
    /// </summary>
    private static FrameworkElement ItemCard(AppServices s, ItemKind kind, CardState st, CardHost host)
    {
        var store = s.Companion;
        var l = s.L;
        var price = store.Price(kind) ?? 0;
        var maxQuantity = Math.Max(1, store.MaxBuyCount(kind));
        // Clamp when the balance drops under the chosen count (Swift onChange(of: maxBuyCount)).
        st.Quantity = Math.Min(st.Quantity, maxQuantity);
        var selected = st.Quantity;
        // Passive items are a one-time buy: no quantity. Stepper only when 2+ are affordable.
        var showsQuantity = !kind.IsPassive() && store.MaxBuyCount(kind) > 1;
        var name = l.ItemName(kind.RawValue());

        var owned = store.ItemCount(kind);
        var nameRow = ShopUi.Row(
            Ui.HStack(6,
                Ui.Text(name, Theme.FontSize.Callout, Theme.Keys.Primary, FontWeights.SemiBold),
                owned > 0 && !kind.IsPassive()
                    ? Ui.Text(l.OwnedCount(owned), Theme.FontSize.Caption2, Theme.Keys.Secondary, FontWeights.Bold, monospacedDigits: true)
                    : null),
            4,
            showsQuantity ? ShopUi.Stepper(selected, 1, maxQuantity, v => { st.Quantity = v; host.Refresh(); }, name) : null);

        var info = Ui.VStack(2, nameRow, ShopUi.Wrapped(l.ItemDescription(kind.RawValue()), Theme.FontSize.Caption, Theme.Keys.Secondary));
        // No outer Spacer: the name row takes the spare width so the stepper sits at the card's right edge.
        return ShopUi.Card(Ui.VStack(8,
            ShopUi.IconHeader(ShopUi.ItemIcon(kind, 30), info),
            BuyControls(s, kind, st, host, price, selected, name)));
    }

    private static FrameworkElement BuyControls(AppServices s, ItemKind kind, CardState st, CardHost host, long price, int selected, string name)
    {
        var store = s.Companion;
        var l = s.L;
        if (kind.IsPassive() && store.ItemCount(kind) > 0)
            // Passive (Shiny Charm, …): bought once, so "owned" instead of a buy button.
            return ShopUi.SealLine(l.OwnedAlready, 5);

        if (st.Confirming)
        {
            // Name with the chosen count ("Rare Candy ×3"); plain name for one.
            var quantityName = selected > 1 ? $"{name} ×{selected}" : name;
            return ShopUi.Row(
                ShopUi.Lines(l.BuyConfirm(quantityName), Theme.FontSize.Caption2, Theme.Keys.Secondary, 1),
                8,
                ShopUi.Button(selected > 1 ? $"{l.Buy} ×{selected}" : l.Buy, () =>
                {
                    st.Confirming = false;
                    if (store.Buy(kind, selected)) st.Quantity = 1;
                    host.Refresh();
                }, prominent: true),
                ShopUi.Borderless(l.Cancel, () => { st.Confirming = false; host.Refresh(); }));
        }

        // The price is the total for the chosen count — show the full spend before confirming.
        var priceText = Ui.Text($"{l.ShopPriceLabel} {TokenFormatter.Compact(price * selected)}", Theme.FontSize.Caption2, Theme.Keys.Tertiary, monospacedDigits: true);
        FrameworkElement trailing = store.CanBuy(kind)
            ? ShopUi.Button(l.Buy, () => { st.Confirming = true; host.Refresh(); })
            : Ui.Text(l.NotEnoughTokens, Theme.FontSize.Caption2, Theme.Keys.Tertiary);
        return ShopUi.Row(priceText, 8, trailing);
    }

    /// <summary>
    /// Egg card — buying discards the current Pokémon for a new egg; <paramref name="tier"/> is the
    /// guaranteed rarity floor (null = plain egg). Two-step inline confirm: one step normally, one more for
    /// a shiny/legendary companion. On success, switch to Home to show the new egg. In the egg state the
    /// card stays listed with a disabled buy button and the reason underneath.
    /// Guaranteed eggs are told apart only by the rarity badge — there is a single egg sprite.
    /// </summary>
    private static FrameworkElement EggCard(AppServices s, Rarity? tier, CardState st, CardHost host)
    {
        var store = s.Companion;
        var l = s.L;
        var eggName = l.EggName(tier?.RawValue());

        // Cropped, squared egg: laid out at 30 (aligned with item icons) but drawn at 26 so it looks less bulky.
        var egg = new SpriteView(null, 26) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var eggBox = new System.Windows.Controls.Grid { Width = 30, Height = 30 };
        eggBox.Children.Add(egg);

        var nameRow = Ui.HStack(6,
            Ui.Text(eggName, Theme.FontSize.Callout, Theme.Keys.Primary, FontWeights.SemiBold),
            // Same label and colour as the Pokédex chip, so the shop's rarity reads like the dex.
            tier is { } t ? Badge.Keyed(l.RarityLabel(t.RawValue()).ToUpperInvariant(), PokemonTypeColors.RarityKey(t)) : null);
        nameRow.HorizontalAlignment = HorizontalAlignment.Left;

        var info = Ui.VStack(2,
            nameRow,
            ShopUi.Wrapped(l.EggDescription(tier?.RawValue()), Theme.FontSize.Caption, Theme.Keys.Secondary),
            // Right under "send off": what is lost, then what stays. Not on the price line — that spot is for
            // the button and the locked-hint. Only when there is someone to release.
            store.HasActive ? ShopUi.Wrapped(l.EggReleaseNote, Theme.FontSize.Caption2, Theme.Keys.Tertiary) : null);
        // Unlike item cards the name row has no gap of its own, so the info column is left-aligned (outer Spacer).
        info.HorizontalAlignment = HorizontalAlignment.Left;
        return ShopUi.Card(Ui.VStack(8, ShopUi.IconHeader(eggBox, info), EggControls(s, tier, eggName, st, host)));
    }

    private static FrameworkElement EggControls(AppServices s, Rarity? tier, string eggName, CardState st, CardHost host)
    {
        var store = s.Companion;
        var l = s.L;
        void SetStage(EggStage stage) { st.Stage = stage; host.Refresh(); }

        // Reroll, then go Home to show the new egg (same pattern as using a bag item).
        void Commit()
        {
            st.Stage = EggStage.Idle;
            if (store.BuyEgg(tier)) s.Nav.Tab = PopoverTab.Home;
            else host.Refresh();
        }

        switch (st.Stage)
        {
            case EggStage.Confirm:
                return ShopUi.Row(
                    ShopUi.Lines(l.EggConfirm(store.DisplayName, eggName), Theme.FontSize.Caption2, Theme.Keys.Secondary, 2),
                    8,
                    // A shiny or legendary companion gets one more warning; otherwise buy right away.
                    ShopUi.Button(l.Buy, () =>
                    {
                        if (store.IsHighValueCompanion) SetStage(EggStage.PreciousConfirm);
                        else Commit();
                    }, prominent: true),
                    ShopUi.Borderless(l.Cancel, () => SetStage(EggStage.Idle)));

            case EggStage.PreciousConfirm:
                var legendary = store.Rarity == Rarity.Legendary;
                var discard = ShopUi.Button(store.CurrentIsShiny && !legendary ? l.FreshEggDiscardShiny : l.FreshEggDiscardValuable,
                    Commit, prominent: true);
                discard.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, Theme.Keys.Orange);
                return ShopUi.Row(
                    ShopUi.Lines(legendary ? l.FreshEggLegendaryWarning : l.FreshEggShinyWarning,
                        Theme.FontSize.Caption2, Theme.Keys.Orange, 2, FontWeights.SemiBold),
                    8,
                    discard,
                    ShopUi.Borderless(l.Cancel, () => SetStage(EggStage.Idle)));

            default:
                var price = Ui.Text($"{l.ShopPriceLabel} {TokenFormatter.Compact(store.Price(ShopEntry.ForEgg(tier)))}",
                    Theme.FontSize.Caption2, Theme.Keys.Tertiary, monospacedDigits: true);
                FrameworkElement trailing;
                if (!store.HasActive)
                    // Egg state: nothing to reroll, so only buying is blocked (CanBuyEgg's gate). A disabled button
                    // shows "it is in the shop"; the reason goes on the line below.
                    trailing = ShopUi.Button(l.Buy, () => { }, enabled: false);
                else if (store.CanBuyEgg(tier))
                    trailing = ShopUi.Button(l.Buy, () => SetStage(EggStage.Confirm));
                else
                    trailing = Ui.Text(l.NotEnoughTokens, Theme.FontSize.Caption2, Theme.Keys.Tertiary);
                return Ui.VStack(4,
                    ShopUi.Row(price, 8, trailing),
                    store.HasActive ? null : ShopUi.Wrapped(l.EggShopLockedHint, Theme.FontSize.Caption2, Theme.Keys.Tertiary));
        }
    }
}
