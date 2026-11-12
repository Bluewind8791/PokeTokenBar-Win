using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Port of the store and copy half of EggPrayerTests.swift (#466): praying over the egg only counts, never touches a
/// roll, and its count shows in the hatch notification and the hover line. The persistence tests are in
/// SaveFieldsTests; the floating pet's click routing and particles are in the App tests.
/// </summary>
public class EggPrayerTests
{
    private static readonly EvoLine Line = Lines.Make(1, Lines.Node(1, Lines.Node(2, Lines.Node(3))));

    /// <summary>Egg at the hatch threshold; <paramref name="pending"/> = species already rolled (the prefetched case).</summary>
    private static CompanionStore HatchReadyEgg(bool pending) =>
        new(new StubProvider(Line), clock: () => Lines.FixedNow, rng: new SeededRng(7), dittoDisguiseRollingEnabled: false,
            settings: new AppSettings(null), filePath: StateFiles.WriteText(
                "{\"installBaselineSet\":true,\"usedSinceInstall\":10000000,\"spentTokens\":0,\"lastDate\":\"d\",\"active\":null,"
                + $"\"dex\":[],\"collectedFinals\":[],\"eggUsage\":{PokemonBalance.EggHatchThreshold}"
                + (pending ? ",\"pendingHatchID\":1" : "") + "}"));

    private static CompanionStore ActiveStoreWithWallet() =>
        new(new StubProvider(Line), clock: () => Lines.FixedNow, rng: new SeededRng(7), dittoDisguiseRollingEnabled: false,
            settings: new AppSettings(null), filePath: StateFiles.WriteText(
                "{\"installBaselineSet\":true,\"usedSinceInstall\":5000000000,\"spentTokens\":0,\"lastDate\":\"d\","
                + "\"active\":{\"baseID\":10,\"pathIDs\":[10],\"stageIndex\":0,\"usedAtStage\":200000000,\"rarity\":\"common\","
                + "\"totalForms\":3,\"isShiny\":false},\"dex\":[],\"collectedFinals\":[]}"));

    /// <summary>The hatched individual with the one field prayers may differ in, and the random id, normalised.</summary>
    private static string RollSignature(CompanionStore store)
    {
        var mon = Assert.IsType<MonState>(store.State.Active).Clone();
        mon.Prayers = null;
        if (mon.Profile != null) mon.Profile.InstanceID = "";
        return StateFiles.Json(mon);
    }

    // MARK: Pray()

    [Fact]
    public void PrayCounts() => MainActor.Run(() =>
    {
        var store = HatchReadyEgg(pending: true);
        store.Pray(); store.Pray(); store.Pray();
        Assert.Equal(new PrayerRecord(3), store.State.EggPrayers);
    });

    [Fact]
    public void PrayIsIgnoredWhileRaisingAPokemon() => MainActor.Run(() =>
    {
        var store = ActiveStoreWithWallet();
        store.Pray();
        Assert.Null(store.State.EggPrayers);
    });

    // MARK: No effect on the roll

    [Fact]
    public void PrayersDoNotChangeTheHatchResult() => MainActor.Run(async () =>
    {
        foreach (var pending in new[] { true, false })
        {
            var plain = HatchReadyEgg(pending);
            var prayed = HatchReadyEgg(pending);
            for (var i = 0; i < 50; i++) prayed.Pray();
            await plain.HatchIfNeeded();
            await prayed.HatchIfNeeded();
            Assert.NotNull(plain.State.Active);
            // pending=…: prayers must not alter species/rarity/shiny/nature/form/profile.
            Assert.Equal(RollSignature(plain), RollSignature(prayed));
            Assert.Equal(new PrayerRecord(50), prayed.State.Active?.Prayers);
        }
    });

    // MARK: Copy on hatch, reset on every new egg (with Pray(); SaveFieldsTests sets the field directly)

    [Fact]
    public void HatchCopiesPrayersAndClearsEggScope() => MainActor.Run(async () =>
    {
        var store = HatchReadyEgg(pending: true);
        store.Pray(); store.Pray();
        await store.HatchIfNeeded();
        Assert.Equal(new PrayerRecord(2), store.State.Active?.Prayers);
        Assert.Null(store.State.EggPrayers);
    });

    [Fact]
    public void BuyEggStartsWithoutPrayers() => MainActor.Run(() =>
    {
        var store = ActiveStoreWithWallet();
        Assert.True(store.BuyFreshEgg());
        Assert.Null(store.State.EggPrayers);
        store.Pray();
        Assert.Equal(1, store.State.EggPrayers?.Count);
    });

    [Fact]
    public void PrayingRaisesChangedSoTheHoverTextFollows() => MainActor.Run(() =>
    {
        var store = HatchReadyEgg(pending: true);
        var changes = 0;
        store.Changed += () => changes++;
        store.Pray();
        Assert.Equal(1, changes);
    });

    // MARK: Text

    [Fact]
    public void PrayerHatchNotificationFollowsTheOutcome()
    {
        L ko = new(AppLanguage.Ko), en = new(AppLanguage.En);
        Assert.Equal("기도 37번… 하늘이 진심이었다. 이로치 뮤!", ko.NotifPrayerHatchBody("뮤", 37, true, Rarity.Legendary));
        Assert.Equal("37 prayers… heaven went all in. Shiny Mew!", en.NotifPrayerHatchBody("Mew", 37, true, Rarity.Legendary));
        Assert.Equal("기도 37번… 통했다?! 뮤가 나왔어요!", ko.NotifPrayerHatchBody("뮤", 37, false, Rarity.Legendary));
        Assert.Equal("37 prayers… it worked?! Mew hatched!", en.NotifPrayerHatchBody("Mew", 37, false, Rarity.Legendary));
        Assert.Equal("기도 37번에 이로치까지?! 피카츄가 나왔어요!", ko.NotifPrayerHatchBody("피카츄", 37, true, Rarity.Rare));
        Assert.Equal("37 prayers AND it's shiny?! Pikachu hatched!", en.NotifPrayerHatchBody("Pikachu", 37, true, Rarity.Common));
        foreach (var rarity in new[] { Rarity.Uncommon, Rarity.Rare })
        {
            Assert.Equal("기도 37번 끝에 피카츄가 나왔어요. 반은 통한 걸로 칩시다.", ko.NotifPrayerHatchBody("피카츄", 37, false, rarity));
            Assert.Equal("37 prayers later: Pikachu. Let's call it half a miracle.", en.NotifPrayerHatchBody("Pikachu", 37, false, rarity));
        }
        Assert.Equal("기도 3번 끝에 꼬렛이 나왔어요. 하늘도 오늘 바빴나 봐요.", ko.NotifPrayerHatchBody("꼬렛", 3, false, Rarity.Common));
        Assert.Equal("3 prayers later: Rattata. Heaven must be swamped today.", en.NotifPrayerHatchBody("Rattata", 3, false, Rarity.Common));
    }

    [Fact]
    public void EveryLanguageHasACopyForEveryPrayerOutcome()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var l = new L(language);
            foreach (var (shiny, rarity) in new[] { (true, Rarity.Legendary), (false, Rarity.Legendary), (true, Rarity.Rare),
                         (false, Rarity.Rare), (false, Rarity.Common) })
                Assert.Contains("5", l.NotifPrayerHatchBody("X", 5, shiny, rarity));
            foreach (var n in new[] { 1, 3, 30, 70, 130, 200, 400 })
            {
                Assert.NotEmpty(l.PrayerQuip(n));
                Assert.Contains($"{n}", l.PrayerHoverLine(n));
            }
        }
    }

    [Fact]
    public void QuipIndexAdvancesEveryThirtyPrayersAndStopsAtTheLast()
    {
        Assert.Null(L.PrayerQuipIndex(0));   // nothing to say before the first prayer
        Assert.Null(L.PrayerQuipIndex(-1));
        foreach (var (prayers, index) in new[] { (1, 0), (29, 0), (30, 1), (59, 1), (60, 2), (299, 9), (300, 10), (10_000, 10) })
            Assert.Equal(index, L.PrayerQuipIndex(prayers));
        Assert.Equal(10, L.PrayerQuipIndex(long.MaxValue));   // a clamped save count does not overflow
        Assert.Equal(11, L.PrayerQuipCount);
    }

    [Fact]
    public void EveryQuipExistsInEveryLanguageAndTheyAreAllDifferent()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var l = new L(language);
            var quips = Enumerable.Range(0, L.PrayerQuipCount).Select(i => l.PrayerQuip(i * L.PrayersPerQuip + 1)).ToList();
            Assert.All(quips, q => Assert.NotEmpty(q));
            Assert.Equal(quips.Count, quips.Distinct().Count());
        }
    }

    [Fact]
    public void HoverLineCopy()
    {
        L ko = new(AppLanguage.Ko), en = new(AppLanguage.En);
        Assert.Equal("", ko.PrayerHoverLine(0));
        Assert.Equal("기도 1회 · 일단 한번 빌어봄", ko.PrayerHoverLine(1));
        Assert.Equal("기도 30회 · 하늘이 아직 확인 안 함", ko.PrayerHoverLine(30));
        Assert.Equal("기도 150회 · 하늘 대기열 3,812번째", ko.PrayerHoverLine(150));
        Assert.Equal("기도 240회 · 하늘: \"접수했습니다. 검토 후 답변드릴게요\"", ko.PrayerHoverLine(240));
        Assert.Equal("기도 299회 · 알이 방금 \"그만…\"이라고 한 것 같음", ko.PrayerHoverLine(299));
        Assert.Equal("1 prayer · just trying it out", en.PrayerHoverLine(1));
        Assert.Equal("31 prayers · heaven hasn't checked yet", en.PrayerHoverLine(31));
        Assert.Equal("120 prayers · heaven's servers are lagging", en.PrayerHoverLine(120));
        Assert.Equal("180 prayers · your palms are wearing thin", en.PrayerHoverLine(180));
        Assert.Equal("300 prayers · if a legendary hatches, that's pure skill", en.PrayerHoverLine(300));
    }

    [Fact]
    public void DetailCopy()
    {
        Assert.Equal("기도 37번 받고 태어남", new L(AppLanguage.Ko).PrayerBornWith(37));
        Assert.Equal("Born after 37 prayers", new L(AppLanguage.En).PrayerBornWith(37));
        Assert.Equal("기도가 하늘에 닿았어요. 아마도요.", new L(AppLanguage.Ko).PrayerReachedHeaven);
        Assert.Equal("Your prayers reached the heavens. Probably.", new L(AppLanguage.En).PrayerReachedHeaven);
        foreach (var language in Enum.GetValues<AppLanguage>())
            Assert.NotEmpty(new L(language).PrayerReachedHeaven);
    }
}
