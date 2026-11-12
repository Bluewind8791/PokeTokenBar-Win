using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>Base 1 is a common three-stage line (the disguise); 132 is Ditto.</summary>
file sealed class EventProvider : IPokeProviding
{
    public static readonly EvoLine Bulb = new(1, Lines.Node(1, Lines.Node(2, Lines.Node(3))), Rarity.Common,
        Lines.Names((1, "en", "Bulb"), (1, "ko", "이상해씨"), (2, "en", "Ivy"), (2, "ko", "이상해풀"), (3, "en", "Venu"), (3, "ko", "이상해꽃")));
    private static readonly EvoLine Ditto = new(132, Lines.Node(132), Rarity.Rare, Lines.Names((132, "en", "Ditto")));

    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(baseSpeciesID == PokemonOdds.DittoSpeciesID ? Ditto : Bulb);

    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(1, 255)]);
}

/// <summary>
/// Port of the store half of PetEventLineTests.swift (#466): the floating pet pops its callout once with the latest
/// hatch / evolution / graduation / Ditto line, which the store records in <see cref="CompanionStore.LatestEvent"/>.
/// The pop decision and the hover text are in the App tests.
/// </summary>
public class PetEventLineTests
{
    private static CompanionStore Store(Func<DateTimeOffset> clock, ulong seed = 7, bool dittoRolling = false)
    {
        var store = new CompanionStore(new EventProvider(), clock: clock, filePath: StateFiles.TempPath("event-line"),
            rng: new SeededRng(seed), dittoDisguiseRollingEnabled: dittoRolling, settings: new AppSettings(null));
        store.SetLanguage(AppLanguage.En);
        return store;
    }

    // MARK: What the store records

    [Fact]
    public void HatchAndEvolutionAreRecordedAndTheNewerReplacesTheOlder() => MainActor.Run(async () =>
    {
        var current = Lines.FixedNow;
        var store = Store(() => current);
        Assert.Null(store.LatestEvent);

        await store.Hatch(1);
        Assert.Equal("Bulb hatched from the egg!", store.LatestEvent?.Text);
        Assert.Equal(Lines.FixedNow, store.LatestEvent?.At);
        var hatchSeq = store.LatestEvent?.Seq;

        current = Lines.FixedNow.AddSeconds(120);
        store.ApplyUsage(PokemonBalance.PhaseThreshold(Rarity.Common, 3, 0));
        Assert.Equal("Bulb evolved into Ivy!", store.LatestEvent?.Text);
        Assert.Equal(current, store.LatestEvent?.At);
        Assert.NotEqual(hatchSeq, store.LatestEvent?.Seq);   // a new event gets a new id
    });

    [Fact]
    public void GraduationIsRecordedToo() => MainActor.Run(async () =>
    {
        var store = Store(() => Lines.FixedNow);
        await store.Hatch(1);
        for (var stage = 0; stage < 3; stage++) store.ApplyUsage(PokemonBalance.PhaseThreshold(Rarity.Common, 3, stage));
        Assert.Equal(new L(AppLanguage.En).NotifGraduateBody("Venu"), store.LatestEvent?.Text);
    });

    /// <summary>Recorded before the notification setting is checked: with notifications off the pet still pops.</summary>
    [Fact]
    public void EventsAreRecordedWithNotificationsOff() => MainActor.Run(async () =>
    {
        var settings = new AppSettings(null);
        settings.Set(CompanionStore.CompanionNotificationsKey, false);
        var store = new CompanionStore(new EventProvider(), clock: () => Lines.FixedNow, filePath: StateFiles.TempPath("event-line"),
            rng: new SeededRng(7), dittoDisguiseRollingEnabled: false, settings: settings);
        store.SetLanguage(AppLanguage.En);
        var notified = 0;
        store.CompanionNotification += (_, _) => notified++;
        await store.Hatch(1);
        Assert.Equal(0, notified);
        Assert.Equal("Bulb hatched from the egg!", store.LatestEvent?.Text);
    });

    /// <summary>Candy grants notify but never pop on the pet (macOS <c>showsOnPet: false</c>).</summary>
    [Fact]
    public void CandyGrantsDoNotPopOnThePet() => MainActor.Run(async () =>
    {
        var store = Store(() => Lines.FixedNow);
        await store.Hatch(1);
        var hatch = store.LatestEvent;
        var notified = new List<string>();
        store.CompanionNotification += (title, _) => notified.Add(title);
        store.GrantCandies([], limitsReady: true);   // seed
        store.GrantCandies([new CandyWindow("test.session.0", "T", WindowClass.Session, 100)], limitsReady: true);
        Assert.NotEmpty(notified);   // the grant itself notified
        Assert.Equal(hatch, store.LatestEvent);
    });

    // MARK: The evolution line names both Pokémon

    [Fact]
    public void EvolutionLineNamesBothWithTheRightKoreanParticles()
    {
        var ko = new L(AppLanguage.Ko);
        Assert.Equal("피카츄가 라이츄로 진화했어요!", ko.NotifEvolveBody("피카츄", "라이츄"));
        Assert.Equal("리자드가 리자몽으로 진화했어요!", ko.NotifEvolveBody("리자드", "리자몽"));
        Assert.Equal("이상해씨가 이상해풀로 진화했어요!", ko.NotifEvolveBody("이상해씨", "이상해풀"));
        Assert.Equal("Pichu evolved into Pikachu!", new L(AppLanguage.En).NotifEvolveBody("Pichu", "Pikachu"));
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var text = new L(language).NotifEvolveBody("AAA", "BBB");
            Assert.True(text.Contains("AAA") && text.Contains("BBB"), $"{language} drops a name");
        }
    }

    // MARK: A disguised Ditto must not be spoiled by the event line

    /// <summary>The first seed whose hatch is a shiny, disguised Ditto (rolls: shiny, nature, Ditto disguise). Swift
    /// pins 2466 for the same RNG; searching keeps the test independent of that constant.</summary>
    private static ulong ShinyDisguisedDittoSeed()
    {
        for (ulong seed = 0; seed < 1_000_000; seed++)
        {
            var rng = new SeededRng(seed);
            var shiny = CompanionStore.RollsShiny(rng.Next(), charmOwned: false);
            rng.Next();
            if (shiny && CompanionStore.DittoDisguiseHit(Rarity.Common, 3, rng.Next())) return seed;
        }
        throw new InvalidOperationException("no seed");
    }

    [Fact]
    public void DisguisedDittoHatchEventShowsTheDisguiseAndHidesShiny() => MainActor.Run(async () =>
    {
        var seed = ShinyDisguisedDittoSeed();
        var plain = Store(() => Lines.FixedNow, seed, dittoRolling: true);
        await plain.Hatch(1);
        Assert.NotNull(plain.State.Active?.DittoDisguise);
        Assert.True(plain.State.Active?.IsShiny);
        Assert.Equal(new L(AppLanguage.En).NotifHatchBody("Bulb"), plain.LatestEvent?.Text);   // same line as the notification

        var prayed = Store(() => Lines.FixedNow, seed, dittoRolling: true);
        prayed.Pray(); prayed.Pray();
        await prayed.Hatch(1);
        Assert.Equal(new L(AppLanguage.En).NotifPrayerHatchBody("Bulb", 2, false, Rarity.Common), prayed.LatestEvent?.Text);
        Assert.DoesNotContain("Ditto", prayed.LatestEvent?.Text);
        Assert.DoesNotContain("shiny", prayed.LatestEvent?.Text.ToLowerInvariant());   // a disguised shiny stays hidden
    });

    [Fact]
    public void RevealEventReplacesTheHatchLineAndNeverShowsAnEvolution() => MainActor.Run(async () =>
    {
        var store = Store(() => Lines.FixedNow, ShinyDisguisedDittoSeed(), dittoRolling: true);
        await store.Hatch(1);
        store.ApplyUsage(130_000_000);   // past the first evolution threshold: the disguise breaks instead of evolving
        Assert.True(await MainActor.WaitUntil(() => store.State.Active?.DittoRevealed == true, 2));
        Assert.Equal(new L(AppLanguage.En).NotifShinyDittoRevealBody("Bulb"), store.LatestEvent?.Text);
        Assert.DoesNotContain("evolved", store.LatestEvent?.Text);
    });
}
