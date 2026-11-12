using System.Windows;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>RegionTally</c> (CompanionView.swift): a generation's progress capsule, "Kanto 87/151", with 🏅 once the
/// region is complete and ✨ once it is complete in shiny. The rarity capsules' visual language, emphasized by completion
/// instead of color.
/// </summary>
public static class RegionTally
{
    public enum Mark { None, Medal, Sparkles }

    /// <summary>Shiny completion outranks plain completion; nothing before the region is complete.</summary>
    public static Mark MarkFor(DexCompletion completion) =>
        completion.IsShinyComplete ? Mark.Sparkles : completion.IsComplete ? Mark.Medal : Mark.None;

    /// <summary>A region with nothing owned yet cannot be selected (it would only show an empty grid).</summary>
    public static bool IsEnabled(DexCompletion completion) => completion.Owned > 0;

    public static FrameworkElement Build(string label, DexCompletion completion, bool isSelected)
    {
        var accent = completion.IsComplete ? Theme.Keys.Green : Theme.Keys.Secondary;
        var row = Ui.HStack(3);
        switch (MarkFor(completion))
        {
            case Mark.Sparkles: row.Children.Add(DexUi.Sparkles(11)); break;
            case Mark.Medal: row.Children.Add(EmojiImages.View("🏅", 12)); break;
        }
        row.Children.Add(Ui.Text(label, Theme.FontSize.Subheadline, Theme.Keys.Primary,
            isSelected ? FontWeights.SemiBold : FontWeights.Medium));
        row.Children.Add(Ui.Text($"{completion.Owned}/{completion.Total}", Theme.FontSize.Subheadline,
            completion.IsComplete ? accent : Theme.Keys.Secondary, FontWeights.Bold));
        if (isSelected) row.Children.Add(DexUi.Icon(DexUi.Glyph.Check, 9, accent, bold: true));
        var capsule = DexUi.Capsule(row,
            DexTheme.Tint(accent, isSelected ? 0.22 : completion.IsComplete ? 0.14 : 0.08), new Thickness(6, 2, 6, 2),
            isSelected ? accent : DexTheme.Tint(accent, 0.35), isSelected ? 1.5 : 0.5);
        if (completion.Owned == 0) capsule.Opacity = 0.5;
        return capsule;
    }
}

/// <summary>Port of <c>DexBadgeMedal</c>: an earned completion medal in the Pokédex header (purely cosmetic).</summary>
public static class DexBadgeMedal
{
    /// <summary>The medals the header collects: regional ones already show on their region capsule, so only the
    /// National and Unown tiers remain.</summary>
    public static List<DexBadge> HeaderBadges(IEnumerable<DexBadge> earned) =>
        earned.Where(b => b.Kind is not (DexBadgeKind.Region or DexBadgeKind.RegionShiny)).ToList();

    public static string Emoji(DexBadge badge) => badge.Kind switch
    {
        DexBadgeKind.Region => "🏅",
        DexBadgeKind.RegionShiny => "✨",
        DexBadgeKind.Unown => "🔤",
        DexBadgeKind.National => "🌍",
        DexBadgeKind.NationalShiny => "✨",
        _ => throw new ArgumentOutOfRangeException(nameof(badge), badge, null),
    };

    public static FrameworkElement Build(DexBadge badge, L l)
    {
        var emoji = Emoji(badge);
        // ✨ is drawn, like the shiny marks of the dex cells; the other emoji use the color artwork.
        FrameworkElement glyph = emoji == "✨" ? DexUi.Sparkles(13) : EmojiImages.View(emoji, 14);
        var help = l.DexBadgeLabel(badge);
        var medal = DexUi.Capsule(glyph, DexTheme.Tint(Theme.Keys.Yellow, 0.14), new Thickness(5, 2, 5, 2),
            DexTheme.Tint(Theme.Keys.Yellow, 0.5), 0.5);
        System.Windows.Automation.AutomationProperties.SetName(medal, help);
        return medal.Help(help);
    }

    /// <summary>The header's medal strip, or null (no space taken) when none is earned.</summary>
    public static FrameworkElement? Strip(IEnumerable<DexBadge> earned, L l)
    {
        var badges = HeaderBadges(earned);
        if (badges.Count == 0) return null;
        var strip = Ui.HStack(3);
        foreach (var badge in badges) strip.Children.Add(Build(badge, l));
        return strip;
    }
}
