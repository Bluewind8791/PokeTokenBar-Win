using System.Windows;
using System.Windows.Controls;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>Port of <c>DifficultyDraft</c>: local editing state. Leaving Settings discards it without touching the store.</summary>
internal sealed class DifficultyDraft
{
    public double Growth;
    public double Shop;

    public DifficultyDraft(CompanionStore companion)
    {
        Growth = companion.GrowthDifficulty;
        Shop = companion.ShopDifficulty;
    }

    public bool Differs(CompanionStore companion) => Growth != companion.GrowthDifficulty || Shop != companion.ShopDifficulty;

    public static DifficultyDraft Save(DifficultyDraft draft, CompanionStore companion)
    {
        companion.SetGrowthDifficulty(draft.Growth);
        companion.SetShopDifficulty(draft.Shop);
        return new DifficultyDraft(companion);
    }
}

/// <summary>Port of <c>DifficultySettingsSection</c>: two log-scale sliders applied only by Save.</summary>
public static partial class SettingsView
{
    private static FrameworkElement DifficultyGroup(AppServices s, SettingsState st)
    {
        var l = s.L;
        var companion = s.Companion;
        var draft = st.Difficulty;
        var card = Card(
            DifficultyRow(l, l.DifficultyGrowthLabel, draft.Growth, l.DifficultyGrowthLowLabel, l.DifficultyGrowthHighLabel,
                v => { draft.Growth = v; st.Rebuild(); }),
            Ui.Divider(),
            DifficultyRow(l, l.DifficultyShopLabel, draft.Shop, l.DifficultyShopLowLabel, l.DifficultyShopHighLabel,
                v => { draft.Shop = v; st.Rebuild(); }));
        var header = Ui.Text(l.DifficultySectionTitle.ToUpper(System.Globalization.CultureInfo.CurrentCulture), Theme.FontSize.Caption,
            Theme.Keys.Secondary, FontWeights.SemiBold);
        header.Margin = new Thickness(4, 0, 0, 0);
        var section = Ui.VStack(6, header, card);
        if (draft.Differs(companion))
        {
            var save = MacButton.Make(l.Save, () => { st.Difficulty = DifficultyDraft.Save(st.Difficulty, companion); st.Rebuild(); }, prominent: true);
            save.HorizontalAlignment = HorizontalAlignment.Right;
            System.Windows.Automation.AutomationProperties.SetAutomationId(save, "saveDifficulty");
            section.Children.Add(save);
        }
        return section;
    }

    private static FrameworkElement DifficultyRow(L l, string label, double value, string low, string high, Action<double> commit)
    {
        var valueText = Ui.Text(l.DifficultyValue(value), Theme.FontSize.Caption, monospacedDigits: true);
        valueText.Width = 52;
        valueText.TextAlignment = TextAlignment.Right;
        var slider = new MacSlider(0, 1, PokemonBalance.DifficultyPosition(value),
            p => commit(PokemonBalance.Difficulty(p)),
            p => valueText.Text = l.DifficultyValue(PokemonBalance.Difficulty(p)),
            accessibleName: label);
        System.Windows.Automation.AutomationProperties.SetHelpText(slider, l.DifficultyScaleHint(low, high));
        var name = Ui.Text(label, Theme.FontSize.Callout);
        name.Width = 76;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        slider.Margin = new Thickness(10, 0, 10, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(valueText);
        // Insets match the name (76 + 10) and value (52 + 10) columns so the labels sit under the slider.
        var scale = Ui.HStack(0, Ui.Text(low, Theme.FontSize.Caption2, Theme.Keys.Secondary), Ui.Spacer(), Ui.Text(high, Theme.FontSize.Caption2, Theme.Keys.Secondary));
        scale.Margin = new Thickness(86, 0, 62, 0);
        var column = Ui.VStack(2, grid, scale);
        column.Margin = new Thickness(12, 8, 12, 8);
        return column;
    }
}
