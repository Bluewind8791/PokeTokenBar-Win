using PokeTokenBar.App;
using PokeTokenBar.Core;

namespace PokeTokenBar.App.Tests;

/// <summary>
/// Port of the App halves of PetEventLineTests.swift and SulkTests.swift (#466): when the event callout pops, the
/// hover text's event / sulk / prayer parts, and the back sprites' URLs, cache keys and front fallback order.
/// </summary>
public class PetEventLineTests
{
    private static bool Pop(int? seq, int? seen, bool visible = true, bool bubble = false) =>
        FloatingPetController.ShouldPopEvent(seq, seen, visible, bubble);

    [Fact]
    public void PopsOnlyForANewEventWhileVisibleAndWithoutABubble()
    {
        Assert.True(Pop(1, null));                    // the first event
        Assert.True(Pop(2, 1));                       // a newer event
        Assert.False(Pop(1, 1));                      // the same event is not popped twice
        Assert.False(Pop(null, null));                // no event
        Assert.False(Pop(2, 1, visible: false));      // the pet is hidden
        Assert.False(Pop(2, 1, bubble: true));        // a limit alert or hearts bubble wins
        Assert.Equal(TimeSpan.FromSeconds(6), FloatingPetController.EventPopDuration);
    }

    [Fact]
    public void HeartsBlockTheEventPopThroughTheBubbleGate() =>
        Assert.False(Pop(2, 1, bubble: FloatingPetController.IsShowingBubble(alertActive: false, heartsShowing: true)));

    [Fact]
    public void HoverTextPutsTheEventBetweenTheUsageLineAndThePrayerLine()
    {
        var l = new L(AppLanguage.En);
        var plain = FloatingPetController.HoverTooltip(1, null, LimitPace.LimitDisplayMode.Used, l);
        var full = FloatingPetController.HoverTooltip(1, null, LimitPace.LimitDisplayMode.Used, l, eventLine: "EVENT", prayers: 3);
        Assert.Equal(plain + "\nEVENT\n" + l.PrayerHoverLine(3), full);
        Assert.Equal(plain, FloatingPetController.HoverTooltip(1, null, LimitPace.LimitDisplayMode.Used, l, eventLine: null));
    }
}

public class SulkHoverTests
{
    [Fact]
    public void HoverTextShowsHoursBelowFortyEightAndDaysFromThere()
    {
        var l = new L(AppLanguage.En);
        string Tooltip(double hours) =>
            FloatingPetController.HoverTooltip(0, null, LimitPace.LimitDisplayMode.Used, l, sulkIdle: TimeSpan.FromHours(hours));
        Assert.Equal(l.FloatingPetHoverTokensOnly("0"), Tooltip(0));
        Assert.EndsWith(l.FloatingPetHoverSulkHours(3), Tooltip(3));
        Assert.EndsWith(l.FloatingPetHoverSulkHours(24), Tooltip(24));
        Assert.EndsWith(l.FloatingPetHoverSulkHours(47), Tooltip(47.9));
        Assert.EndsWith(l.FloatingPetHoverSulkDays(2), Tooltip(48));
        Assert.EndsWith(l.FloatingPetHoverSulkDays(4), Tooltip(100));
    }

    [Fact]
    public void SulkSuffixFollowsTheLimitPercent()
    {
        var l = new L(AppLanguage.En);
        var text = FloatingPetController.HoverTooltip(1, 42, LimitPace.LimitDisplayMode.Used, l, sulkIdle: TimeSpan.FromHours(5));
        Assert.Equal(l.FloatingPetHoverWithLimit("1", "42%") + " · " + l.FloatingPetHoverSulkHours(5), text);
    }
}

public class BackSpriteTests
{
    private const string Base = "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon";

    [Fact]
    public void BackUrls()
    {
        const string gif = Base + "/versions/generation-v/black-white/animated";
        Assert.Equal($"{gif}/back/25.gif", SpriteStore.SpriteUrl(25, animated: true, shiny: false, back: true));
        Assert.Equal($"{gif}/back/shiny/25.gif", SpriteStore.SpriteUrl(25, animated: true, shiny: true, back: true));
        Assert.Equal($"{Base}/back/25.png", SpriteStore.SpriteUrl(25, animated: false, shiny: false, back: true));
        Assert.Equal($"{Base}/back/shiny/25.png", SpriteStore.SpriteUrl(25, animated: false, shiny: true, back: true));
        Assert.Equal($"{gif}/25.gif", SpriteStore.SpriteUrl(25, animated: true, shiny: false));   // front is unchanged
    }

    [Fact]
    public void BackCacheKeyDiffersFromFrontAndFrontKeyIsUnchanged()
    {
        foreach (var animated in new[] { false, true })
            foreach (var shiny in new[] { false, true })
                Assert.NotEqual(SpriteStore.CacheKey(25, animated, shiny), SpriteStore.CacheKey(25, animated, shiny, back: true));
        Assert.Equal("25-s", SpriteStore.CacheKey(25, animated: false, shiny: false));
        Assert.Equal("25-back-sha", SpriteStore.CacheKey(25, animated: true, shiny: true, back: true));
    }

    [Fact]
    public void TrayIdentityKeyIncludesBack() =>
        Assert.NotEqual(TraySprite.MenuSpriteKey(25, false, 0.4, null), TraySprite.MenuSpriteKey(25, false, 0.4, null, back: true));

    /// <summary>A missing back sprite falls back to the front one (shiny before plain within each facing).</summary>
    [Fact]
    public void BackIsTriedFirstThenTheFront()
    {
        Assert.Equal([(true, true), (true, false), (false, true), (false, false)], SpriteStore.Variants(shiny: true, back: true));
        Assert.Equal([(true, false), (false, false)], SpriteStore.Variants(shiny: false, back: true));
        Assert.Equal([(false, true), (false, false)], SpriteStore.Variants(shiny: true, back: false));
        Assert.Equal([(false, false)], SpriteStore.Variants(shiny: false, back: false));
    }
}

/// <summary>Port of the #466 part of StatusItemMenuTests.swift: the "Show back view" row, checked while always facing back.</summary>
public class StatusItemMenuTests
{
    [Fact]
    public void EntriesInOrder()
    {
        var l = new L(AppLanguage.En);
        var entries = StatusItemMenu.Entries(l, 0, null, floatingPetEnabled: true);
        Assert.Equal(
            [StatusItemMenu.MenuAction.Refresh, StatusItemMenu.MenuAction.OpenDex, StatusItemMenu.MenuAction.OpenSettings,
             StatusItemMenu.MenuAction.ToggleFloatingPet, StatusItemMenu.MenuAction.ToggleBackView, StatusItemMenu.MenuAction.Quit],
            entries.Where(e => e?.Action != null).Select(e => e!.Action!.Value));
        Assert.Equal(l.FloatingPetHideLabel, entries.Single(e => e?.Action == StatusItemMenu.MenuAction.ToggleFloatingPet)!.Title);
        Assert.Equal(l.BackViewLabel, entries.Single(e => e?.Action == StatusItemMenu.MenuAction.ToggleBackView)!.Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackViewRowIsCheckedWhenAlwaysFacingBack(bool on)
    {
        var entries = StatusItemMenu.Entries(new L(AppLanguage.En), 0, null, floatingPetEnabled: false, alwaysFacesBack: on);
        var row = entries.Single(e => e?.Action == StatusItemMenu.MenuAction.ToggleBackView)!;
        Assert.Equal(on, row.IsChecked);
        Assert.All(entries.Where(e => e != null && e.Action != StatusItemMenu.MenuAction.ToggleBackView), e => Assert.False(e!.IsChecked));
    }
}
