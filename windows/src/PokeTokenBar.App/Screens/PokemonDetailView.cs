using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>The detail page's SwiftUI <c>@State</c>; a new one per opened species (<c>.id(collectionID)</c>).</summary>
internal sealed class PokemonDetailState(string collectionID)
{
    public readonly string CollectionID = collectionID;
    public string SelectedInstanceID = "";
    public PokemonForm? SelectedForm;
    public bool? SelectedShiny;
    /// <summary>The page's <c>.task</c> has run (initial selection + detail/name loads).</summary>
    public bool Started;
}

/// <summary>
/// Port of <c>PokemonDetailView</c>: a scrollable species + individual page. A Pokédex species may aggregate
/// several catches, so the pickers choose the exact persisted individual while the immutable PokéAPI sections
/// (base stats, species data, move list) stay shared.
/// </summary>
internal static class PokemonDetailView
{
    public static FrameworkElement Build(AppServices s, CompanionStore.DexSpecies species, PokemonDetailState st,
        Action onBack, Action refresh)
    {
        var store = s.Companion;
        var l = store.L;
        var forms = store.FormSpeciesList(species.Id);
        var displayed = forms.FirstOrDefault(f => f.Form == st.SelectedForm)
                        ?? forms.FirstOrDefault(store.IsRepresentative)
                        ?? forms.FirstOrDefault() ?? species;
        var allIndividuals = store.PokemonIndividuals(species.Id, displayed.Form);
        var displayedShiny = st.SelectedShiny is { } want && (want ? displayed.IsShiny : displayed.HasNormal)
            ? want
            : allIndividuals.FirstOrDefault()?.IsShiny ?? displayed.IsShiny;
        var individuals = allIndividuals.Where(e => e.IsShiny == displayedShiny).ToList();
        var individual = individuals.FirstOrDefault(e => e.Id == st.SelectedInstanceID) ?? individuals.FirstOrDefault();

        var page = new Page(s, species, st, displayed, displayedShiny, individuals, individual, refresh);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var back = Ui.HStack(4, DexUi.Icon(DexUi.Glyph.ChevronLeft, 12, Theme.Keys.Secondary),
            Ui.Text(l.Back, Theme.FontSize.Body, Theme.Keys.Secondary));
        back.OnClick(onBack);
        var number = Ui.Text($"#{species.Id}", Theme.FontSize.Callout, Theme.Keys.Secondary);
        var header = Ui.HStack(0, back, Ui.Spacer(), number);
        root.Children.Add(header);

        var body = Ui.VStack(12);
        if (PokemonForm.HasForms(species.Id)) body.Children.Add(page.FormPicker(forms));
        if (displayed.HasNormal && displayed.IsShiny) body.Children.Add(page.AppearancePicker());
        body.Children.Add(page.IdentityHeader());
        if (individuals.Count > 1) body.Children.Add(page.IndividualPicker());
        if (individuals.Count == 0)
            body.Children.Add(Ui.Text(l.DexAppearancePreview, Theme.FontSize.Callout, Theme.Keys.Secondary).Wrapping());
        var moveLabels = new List<TextBlock>();
        if (store.PokemonDetailsByID.TryGetValue(species.Id, out var details))
        {
            if (individual is { Profile: { } profile })
                body.Children.Add(page.IndividualSection(individual, profile, details));
            else
                body.Children.Add(page.BaseStatsSection(details));
            body.Children.Add(page.SpeciesSection(details));
            body.Children.Add(page.MovesSection(details, moveLabels));
        }
        else if (store.FailedPokemonDetailIDs.Contains(species.Id))
        {
            var text = Ui.Text(l.PokemonDetailsUnavailable, Theme.FontSize.Body, Theme.Keys.Secondary);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            var retry = Buttons.Small(l.Retry, () => _ = store.LoadPokemonDetails(species.Id));
            retry.HorizontalAlignment = HorizontalAlignment.Center;
            var failed = Ui.VStack(8, text, retry);
            failed.Margin = new Thickness(0, 24, 0, 24);
            body.Children.Add(failed);
        }
        else
        {
            var loading = Ui.HStack(6, Ui.Spacer(), DexUi.Spinner(), Ui.Text(l.LoadingPokemonDetails, Theme.FontSize.Body, Theme.Keys.Secondary), Ui.Spacer());
            loading.Margin = new Thickness(0, 30, 0, 30);
            body.Children.Add(loading);
        }
        body.Margin = new Thickness(0, 0, 0, 8);
        var scroller = DexUi.OverlayScroller(body);
        scroller.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);
        DexNames.LoadWhenVisible(scroller, moveLabels);

        if (!st.Started)
        {
            st.Started = true;
            st.SelectedForm ??= displayed.Form;
            if (st.SelectedInstanceID.Length == 0) st.SelectedInstanceID = individuals.FirstOrDefault()?.Id ?? "";
            root.Loaded += (_, _) =>
            {
                _ = store.LoadPokemonDetails(species.Id);
                if (PokemonForm.HasForms(species.Id)) _ = LoadFormNames(store, species.Id, refresh);
            };
        }
        return root;
    }

    /// <summary>Form labels: the store's loader when the app wires one, and the shared name cache either way.</summary>
    private static async Task LoadFormNames(CompanionStore store, int speciesID, Action refresh)
    {
        var resources = PokemonForm.NameResources(speciesID);
        _ = store.LoadFormNames(resources);
        var missing = resources.Where(r => !DexNames.IsLoaded(r)).ToList();
        if (missing.Count == 0) return;
        await Task.WhenAll(missing.Select(DexNames.Load));
        refresh();
    }

    /// <summary>Loaded store form names merged with the shared name cache.</summary>
    private static IReadOnlyDictionary<PokemonNameResource, Dictionary<string, string>> FormNames(CompanionStore store)
    {
        var merged = new Dictionary<PokemonNameResource, Dictionary<string, string>>(DexNames.Loaded);
        foreach (var (k, v) in store.FormNames) merged[k] = v;
        return merged;
    }

    /// <summary>Unown's 28 letters fill seven columns; named forms get wider cells for their labels.</summary>
    public static int FormPickerColumns(int formCount) => formCount > 8 ? (formCount > 20 ? 7 : 6) : Math.Min(formCount, 4);

    private sealed class Page(AppServices s, CompanionStore.DexSpecies species, PokemonDetailState st,
        CompanionStore.DexSpecies displayed, bool displayedShiny, List<DexEntry> individuals, DexEntry? individual, Action refresh)
    {
        private CompanionStore Store => s.Companion;
        private L L => Store.L;
        private AppLanguage Language => Store.Language;

        public FrameworkElement FormPicker(List<CompanionStore.DexSpecies> forms)
        {
            var allForms = PokemonForm.Forms(species.Id);
            var selected = displayed.Form;
            var columns = FormPickerColumns(allForms.Count);
            var names = FormNames(Store);
            var cells = new List<UIElement?>();
            foreach (var form in allForms)
            {
                var owned = forms.FirstOrDefault(f => f.Form == form);
                var label = PokemonForm.Label(species.Id, form, Language, names) ?? "";
                var sprite = new SpriteView(species.Id, 36, shiny: owned?.IsShiny == true, form: form);
                var spriteBox = new Grid { Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center, Children = { sprite } };
                if (owned?.IsShiny == true)
                {
                    var spark = DexUi.Sparkles(Theme.FontSize.Subheadline);
                    spark.HorizontalAlignment = HorizontalAlignment.Right;
                    spark.VerticalAlignment = VerticalAlignment.Top;
                    spriteBox.Children.Add(spark);
                }
                // Wide grids hold short labels (letters, types); longer names wrap on two lines.
                var text = DexUi.Lines(Ui.Text(label, Theme.FontSize.Subheadline, Theme.Keys.Primary, FontWeights.SemiBold),
                    columns > 4 ? 1 : 2, reserve: columns <= 4);
                text.TextAlignment = TextAlignment.Center;
                text.Margin = new Thickness(2, 0, 2, 0);
                var content = new StackPanel { Children = { spriteBox, text }, Opacity = owned == null ? 0.25 : 1 };
                var isRep = owned != null && Store.IsRepresentative(owned);
                var cell = DexUi.Rounded(content, isRep ? DexTheme.Tint(Theme.Keys.Accent, 0.16) : DexTheme.Tint(Theme.Keys.Secondary, 0.06),
                    6, new Thickness(0, 3, 0, 3), selected == form ? Theme.Keys.Accent : null, selected == form ? 1.5 : 0);
                System.Windows.Automation.AutomationProperties.SetName(cell, $"{species.Name} [{label}]");
                System.Windows.Automation.AutomationProperties.SetItemStatus(cell,
                    owned == null ? L.FormNotCollected : owned.IsShiny ? L.DexShinyLabel : "");
                cells.Add(cell.Plain(() =>
                {
                    st.SelectedForm = form;
                    st.SelectedInstanceID = "";
                    st.SelectedShiny = null;
                    refresh();
                }, enabled: owned != null));
            }
            return Ui.VStack(6,
                Ui.Text(L.FormsCollected(forms.Count, allForms.Count), Theme.FontSize.Callout, Theme.Keys.Primary, FontWeights.SemiBold),
                DexUi.FlexGrid(cells, columns, 3, 4));
        }

        public FrameworkElement AppearancePicker()
        {
            var picker = SegmentedControl.Make([("normal", L.DexNormalLabel), ("shiny", $"✨ {L.DexShinyLabel}")],
                displayedShiny ? "shiny" : "normal", id =>
                {
                    st.SelectedShiny = id == "shiny";
                    st.SelectedInstanceID = "";
                    refresh();
                });
            return LabeledControl(L.DexAppearance, picker, stretch: true);
        }

        /// <summary>macOS form row: the picker's label on the left, the control after it.</summary>
        private static FrameworkElement LabeledControl(string label, FrameworkElement control, bool stretch)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var text = Ui.Text(label, Theme.FontSize.Body);
            text.Margin = new Thickness(0, 0, 8, 0);
            grid.Children.Add(text);
            control.HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        public FrameworkElement IdentityHeader()
        {
            var sp = displayed;
            var sprite = new SpriteView(sp.Id, 104, animated: true, shiny: displayedShiny, form: sp.Form);
            var title = Ui.Text(DisplayName(sp), Theme.FontSize.Title3, Theme.Keys.Primary, FontWeights.Bold);
            var rarity = Ui.Text(L.RarityLabel(sp.Rarity), Theme.FontSize.Callout, Theme.Keys.Secondary, FontWeights.SemiBold);
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.TextWrapping = TextWrapping.Wrap;
            title.TextTrimming = TextTrimming.None;
            title.VerticalAlignment = VerticalAlignment.Bottom;
            rarity.VerticalAlignment = VerticalAlignment.Bottom;
            rarity.Margin = new Thickness(8, 0, 0, 1.5);   // first-text-baseline with the 15 pt title
            Grid.SetColumn(rarity, 1);
            titleRow.Children.Add(title);
            titleRow.Children.Add(rarity);

            var info = Ui.VStack(5, titleRow);
            if (Store.PokemonDetailsByID.TryGetValue(sp.Id, out var details))
            {
                var types = TypeBadges(details.Types);
                types.HorizontalAlignment = HorizontalAlignment.Left;
                info.Children.Add(types);
                // Alternate forms show the default form's PokéAPI data; say so.
                if (!PokemonForm.IsDefault(sp.Form, sp.Id))
                    info.Children.Add(Ui.Text(L.DefaultFormDataNote, Theme.FontSize.Caption2, Theme.Keys.Secondary).Wrapping());
            }
            if (displayedShiny)
                info.Children.Add(Ui.HStack(4, DexUi.Sparkles(Theme.FontSize.Callout), Ui.Text(L.DexShinyLabel, Theme.FontSize.Callout)));
            if (individual != null && Store.IsActiveDexEntry(individual))
                info.Children.Add(Ui.Text(L.DexRaising, Theme.FontSize.Callout, Theme.Keys.Accent));
            var isRep = Store.IsRepresentative(sp);
            info.Children.Add(CollectionView.RepresentativeButton(L, isRep,
                () => Store.SetRepresentativeSpeciesID(isRep ? null : sp.Id, sp.Form)));
            foreach (FrameworkElement child in info.Children)
                if (child != titleRow) child.HorizontalAlignment = HorizontalAlignment.Left;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sprite.VerticalAlignment = VerticalAlignment.Center;
            info.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(sprite);
            Grid.SetColumn(info, 2);
            row.Children.Add(info);
            return row;
        }

        /// <summary>The form page's title ("Rotom [Heat Rotom]") with whichever form names have loaded.</summary>
        private string DisplayName(CompanionStore.DexSpecies sp)
        {
            if (sp.Form == null || sp.Names == null) return sp.Name;
            var raw = Language.ResolveName(sp.Names) ?? $"#{sp.Id}";
            return PokemonForm.DisplayName(raw, sp.Id, sp.Form, Language, FormNames(Store));
        }

        /// <summary>Port of <c>TypeBadges</c>: one badge per type with its localized, uppercased name.</summary>
        private Stack TypeBadges(IReadOnlyList<string> types)
        {
            var row = Ui.HStack(5);
            foreach (var type in types)
            {
                var items = new[] { DexNames.Of(PokemonNameResource.ResourceKind.Type, type) };
                var badge = Badge.Make(DexNames.Text(items, Language).ToUpper(CultureInfo.CurrentCulture),
                    PokemonTypeColors.Brush(type) ?? Theme.Brush(Theme.Keys.Gray), size: 11, typeShadow: true, hPad: 7, vPad: 2);
                if (badge.Child is TextBlock label)
                    DexNames.Request(label, items, Language, t => t.ToUpper(CultureInfo.CurrentCulture));
                row.Children.Add(badge);
            }
            return row;
        }

        public FrameworkElement IndividualPicker()
        {
            var current = individual?.Id ?? "";
            var index = individuals.FindIndex(e => e.Id == current);
            var text = Ui.Text(index >= 0 ? IndividualLabel(individuals[index], index) : "", Theme.FontSize.Body);
            var chevrons = new StackPanel
            {
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    DexUi.Icon(DexUi.Glyph.ChevronUp, 7, Theme.Keys.Primary, bold: true),
                    DexUi.Icon(DexUi.Glyph.ChevronDown, 7, Theme.Keys.Primary, bold: true),
                },
            };
            var popup = DexUi.Rounded(Ui.HStack(0, text, chevrons), Theme.Keys.Fill, 6, new Thickness(10, 3, 8, 4));
            popup.HorizontalAlignment = HorizontalAlignment.Left;
            popup.OnClick(() => DexUi.ShowMenu(popup, DexUi.Menu(individuals.Select((e, i) =>
                (IndividualLabel(e, i), e.Id == current, (Action)(() =>
                {
                    st.SelectedInstanceID = e.Id;
                    refresh();
                }))))));
            return LabeledControl(L.PokemonIndividual, popup, stretch: false);
        }

        private string IndividualLabel(DexEntry entry, int index)
        {
            var appearance = entry.IsShiny ? $"✨ {L.DexShinyLabel}" : L.DexNormalLabel;
            var date = entry.CaughtAt is { } at ? DexRelativeTime.NumericDate(at, Language)
                : Store.IsActiveDexEntry(entry) ? L.DexRaising : "—";
            return $"#{index + 1} · {appearance} · Lv. {entry.Profile?.Level ?? 5} · {date}";
        }

        public FrameworkElement IndividualSection(DexEntry entry, PokemonProfile profile, PokemonDetails details)
        {
            var abilityValue = profile.AbilityName is { } name
                ? DexNames.Label([DexNames.Of(PokemonNameResource.ResourceKind.Ability, name,
                    profile.AbilityIsHidden ? " · " + L.HiddenAbility : "")], Language, Theme.FontSize.Callout, weight: FontWeights.SemiBold)
                : Ui.Text("—", Theme.FontSize.Callout, Theme.Keys.Primary, FontWeights.SemiBold);
            var section = Ui.VStack(8,
                Title(L.PokemonIndividual),
                Left(Ui.HStack(12,
                    ValuePair(L.Level, $"{profile.Level}"),
                    ValuePair(L.Gender, L.GenderLabel(profile.Gender)),
                    ValuePair(L.Nature, entry.Nature?.Name(Language) ?? "—"))),
                Ui.VStack(1, Ui.Text(L.Ability, Theme.FontSize.Subheadline, Theme.Keys.Secondary), abilityValue),
                StatsSection(PokemonStatCalculator.Stats(details, profile, entry.Nature)),
                Title(L.ActiveMoves));
            if (profile.Moves.Count == 0)
                section.Children.Add(Ui.Text(L.NoLevelMoves, Theme.FontSize.Callout, Theme.Keys.Secondary).Wrapping());
            else
                foreach (var move in profile.Moves)
                    section.Children.Add(Ui.HStack(8,
                        DexNames.Label([DexNames.Of(PokemonNameResource.ResourceKind.Move, move.Name)], Language, Theme.FontSize.Callout),
                        Ui.Spacer(),
                        Ui.Text($"Lv. {move.LearnedAtLevel}", Theme.FontSize.Callout, Theme.Keys.Secondary)));
            return Card(section);
        }

        public FrameworkElement BaseStatsSection(PokemonDetails details)
        {
            var section = Ui.VStack(6, Title(L.BaseStats));
            foreach (var stat in PokemonStatCalculator.Order)
                if (details.BaseStats.TryGetValue(stat, out var value))
                    section.Children.Add(StatRow(stat, value, null, 300));
            return Card(section);
        }

        private FrameworkElement StatsSection(IReadOnlyList<PokemonComputedStat> stats)
        {
            var max = PokemonStatCalculator.DisplayScaleMaximum(stats.Select(x => x.Value).ToList());
            var section = Ui.VStack(5, Title(L.ActualStats));
            foreach (var stat in stats) section.Children.Add(StatRow(stat.Name, stat.Value, stat.Iv, max));
            return section;
        }

        private FrameworkElement StatRow(string name, int value, int? iv, int scaleMaximum)
        {
            // The macOS linear progress view is a 20 pt tall control around a 7 pt bar.
            var grid = new Grid { MinHeight = 20 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            if (iv != null) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.Children.Add(Ui.Text(L.StatLabel(name), Theme.FontSize.Callout));
            var bar = new StatBar(value / (double)Math.Max(1, scaleMaximum)) { Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(bar, 1);
            grid.Children.Add(bar);
            var number = Ui.Text($"{value}", Theme.FontSize.Callout, monospacedDigits: true);
            number.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(number, 2);
            grid.Children.Add(number);
            if (iv is { } ivValue)
            {
                var ivText = Ui.Text($"IV {ivValue}", Theme.FontSize.Callout, Theme.Keys.Secondary);
                ivText.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(ivText, 3);
                grid.Children.Add(ivText);
            }
            return grid;
        }

        public FrameworkElement SpeciesSection(PokemonDetails details)
        {
            var abilities = DexNames.Label(details.Abilities.Select(a =>
                    DexNames.Of(PokemonNameResource.ResourceKind.Ability, a.Name, a.IsHidden ? $" ({L.Hidden})" : "")).ToList(),
                Language, Theme.FontSize.Callout, Theme.Keys.Secondary).Wrapping();
            return Card(Ui.VStack(7,
                Title(L.SpeciesData),
                Left(Ui.HStack(14,
                    ValuePair(L.Height, string.Format(CultureInfo.InvariantCulture, "{0:0.0} m", details.Height / 10.0)),
                    ValuePair(L.Weight, string.Format(CultureInfo.InvariantCulture, "{0:0.0} kg", details.Weight / 10.0)),
                    ValuePair(L.BaseStatTotal, $"{details.BaseStatTotal}"))),
                Title(L.PossibleAbilities),
                abilities));
        }

        /// <summary>The complete move list; names load as rows scroll near the viewport.</summary>
        public FrameworkElement MovesSection(PokemonDetails details, List<TextBlock> lazyLabels)
        {
            var section = Ui.VStack(6, Title(L.CompleteMoveList(details.Moves.Count)));
            foreach (var move in details.Moves)
            {
                var name = DexNames.Label([DexNames.Of(PokemonNameResource.ResourceKind.Move, move.Name)], Language,
                    Theme.FontSize.Callout, loadNow: false);
                lazyLabels.Add(name);
                var methods = Ui.Text(string.Join(" · ", move.LearnMethods.Select(L.MoveMethod).Distinct()),
                    Theme.FontSize.Callout, Theme.Keys.Secondary).Wrapping();
                methods.TextAlignment = TextAlignment.Right;
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                name.VerticalAlignment = VerticalAlignment.Top;
                methods.VerticalAlignment = VerticalAlignment.Top;
                methods.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(methods, 1);
                row.Children.Add(name);
                row.Children.Add(methods);
                section.Children.Add(row);
                section.Children.Add(Ui.Divider());
            }
            return Card(section);
        }

        private static TextBlock Title(string text) =>
            Ui.Text(text, Theme.FontSize.Callout, Theme.Keys.Secondary, FontWeights.SemiBold);

        private static FrameworkElement ValuePair(string label, string value) =>
            Ui.VStack(1, Ui.Text(label, Theme.FontSize.Subheadline, Theme.Keys.Secondary),
                Ui.Text(value, Theme.FontSize.Callout, Theme.Keys.Primary, FontWeights.SemiBold));

        private static T Left<T>(T element) where T : FrameworkElement
        {
            element.HorizontalAlignment = HorizontalAlignment.Left;
            return element;
        }

        /// <summary>Swift <c>detailCard()</c>.</summary>
        private static FrameworkElement Card(FrameworkElement content) =>
            DexUi.Rounded(content, DexTheme.Tint(Theme.Keys.Secondary, 0.07), 9, new Thickness(9));
    }

    /// <summary>macOS linear <c>ProgressView(value:total:)</c> tinted with the accent color.</summary>
    private sealed class StatBar : Grid
    {
        public StatBar(double fraction)
        {
            Height = 7;
            VerticalAlignment = VerticalAlignment.Center;
            var track = new Border { CornerRadius = new CornerRadius(3.5) };
            track.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Fill);
            var fill = new Border { CornerRadius = new CornerRadius(3.5), HorizontalAlignment = HorizontalAlignment.Left };
            fill.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Accent);
            Children.Add(track);
            Children.Add(fill);
            SizeChanged += (_, _) => fill.Width = Math.Max(Height, ActualWidth * Math.Clamp(fraction, 0, 1));
        }
    }
}
