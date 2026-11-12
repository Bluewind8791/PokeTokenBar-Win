using PokeTokenBar.App;
using PokeTokenBar.Core;

namespace PokeTokenBar.App.Tests;

/// <summary>
/// Port of the floating-pet half of EggPrayerTests.swift (#466): a click prays over the egg (and opens a hatched
/// Pokémon), each prayer spawns a 🙏 that removes itself, and every 30th prayer refreshes usage at most once per 30 s.
/// The store half is in the Core tests.
/// </summary>
public class EggPrayerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private sealed class CallLog
    {
        public int Prays, Emojis, Refreshes, Opens, DidPray;
        /// <summary>The persisted prayer count of the current egg (reset by a test to simulate a new egg).</summary>
        public long EggPrayers;
    }

    private static PetClickRouter MakeRouter(Func<DateTimeOffset> clock, CallLog log) =>
        new(pray: () => { log.Prays++; log.EggPrayers++; }, prayerCount: () => log.EggPrayers, didPray: () => log.DidPray++,
            spawnEmoji: () => log.Emojis++, refreshUsage: () => log.Refreshes++, openPopover: () => log.Opens++, clock: clock);

    private static void Pray(int times, PetClickRouter router)
    {
        for (var i = 0; i < times; i++) router.Handle(subjectIsEgg: true);
    }

    [Fact]
    public void ClickPraysOverAnEggAndOpensThePopoverForAPokemon()
    {
        Assert.Equal(FloatingPetController.ClickAction.Pray, FloatingPetController.ClickActionFor(subjectIsEgg: true));
        Assert.Equal(FloatingPetController.ClickAction.OpenPopover, FloatingPetController.ClickActionFor(subjectIsEgg: false));
    }

    [Fact]
    public async Task BurstSpawnsClampedParticlesAndRemovesThemItself()
    {
        var burst = new PrayerBurst();
        burst.Spawn(-3);
        burst.Spawn(7);
        burst.Spawn(Random.Shared.NextDouble());
        Assert.Equal(3, burst.Particles.Count);   // rapid clicks stack up
        Assert.Equal(3, burst.WobbleCount);
        Assert.Equal(0, burst.Particles[0].XFraction);
        Assert.Equal(1, burst.Particles[1].XFraction);
        Assert.All(burst.Particles, p => Assert.InRange(p.XFraction, 0, 1));
        await Task.Delay(PrayerBurst.Lifetime + TimeSpan.FromMilliseconds(300));
        Assert.Empty(burst.Particles);   // each particle removes itself
        Assert.Equal(3, burst.WobbleCount);
    }

    [Fact]
    public void EmojiStaysInsideThePetFrameAtEverySize()
    {
        for (double petSize = 48; petSize <= 384; petSize += 8)
        {
            var emoji = PrayerBurst.EmojiSize(petSize);
            Assert.True(emoji <= petSize);
            Assert.InRange(emoji, 14, 32);   // legible at 48 pt, modest at 384 pt
            foreach (var fraction in new[] { 0.0, 1.0 })
                foreach (var progress in new[] { 0.0, 1.0 })
                {
                    var at = PrayerBurst.Position(fraction, petSize, progress);
                    Assert.True(at.X - emoji / 2 >= 0 && at.X + emoji / 2 <= petSize, $"{petSize} x");
                    Assert.True(at.Y - emoji / 2 >= 0 && at.Y + emoji / 2 <= petSize, $"{petSize} y");
                }
        }
    }

    [Fact]
    public void HoverTextShowsPrayersOnlyWhenThereAreSome()
    {
        var l = new L(AppLanguage.Ko);
        var plain = FloatingPetController.HoverTooltip(1, null, LimitPace.LimitDisplayMode.Used, l);
        var prayed = FloatingPetController.HoverTooltip(1, null, LimitPace.LimitDisplayMode.Used, l, prayers: 3);
        Assert.DoesNotContain("기도", plain);
        Assert.Equal(plain + "\n기도 3회 · 일단 한번 빌어봄", prayed);
    }

    // MARK: Prayer-triggered usage refresh

    [Fact]
    public void MilestonesAreEveryThirtyPrayers()
    {
        var milestones = new Dictionary<long, long> { [0] = 0, [1] = 0, [29] = 0, [30] = 1, [59] = 1, [60] = 2, [89] = 2, [90] = 3, [120] = 4 };
        foreach (var (count, milestone) in milestones) Assert.Equal(milestone, FloatingPetController.PrayerRefreshMilestone(count));
        Assert.Equal(30, FloatingPetController.PrayersPerRefresh);
        Assert.Equal(long.MaxValue / 30, FloatingPetController.PrayerRefreshMilestone(long.MaxValue));   // huge counts do not trap
        Assert.Equal(0, FloatingPetController.PrayerRefreshMilestone(-5));
    }

    [Fact]
    public void PrayerRefreshFollowsTheMilestones()
    {
        var current = Now;
        var log = new CallLog();
        var router = MakeRouter(() => current, log);
        var floor = FloatingPetController.PrayerRefreshInterval;

        Pray(29, router);
        Assert.Equal(0, log.Refreshes);   // 29 prayers: no refresh
        Pray(1, router);
        Assert.Equal(1, log.Refreshes);   // the 30th prayer refreshes
        current = Now + floor;
        Pray(29, router);
        Assert.Equal(1, log.Refreshes);   // 59 prayers: no new refresh
        Pray(1, router);
        Assert.Equal(2, log.Refreshes);   // the 60th prayer refreshes
        current = Now + floor * 2;
        Pray(29, router);
        Assert.Equal(2, log.Refreshes);
        Pray(1, router);
        Assert.Equal(3, log.Refreshes);   // the 90th prayer refreshes
        Assert.Equal(log.Prays, log.Emojis);   // every click still spawns its emoji
        Assert.Equal(log.Prays, log.DidPray);  // every click rebuilds the hover text
    }

    [Fact]
    public void AMilestoneInsideTheFloorIsRefreshedByTheFirstClickAfterIt()
    {
        var current = Now;
        var log = new CallLog();
        var router = MakeRouter(() => current, log);
        Pray(30, router);
        Assert.Equal(1, log.Refreshes);
        current = Now.AddSeconds(5);
        Pray(30, router);   // reaches 60 only 5 s after the last refresh
        Assert.Equal(1, log.Refreshes);   // the floor holds the 60th-prayer refresh back
        current = Now + FloatingPetController.PrayerRefreshInterval;
        Pray(1, router);
        Assert.Equal(2, log.Refreshes);   // the first click after the floor does not lose the milestone
        Pray(1, router);
        Assert.Equal(2, log.Refreshes);   // and it is not refreshed twice
    }

    [Fact]
    public void ANewEggStartsTheMilestonesOver()
    {
        var current = Now;
        var log = new CallLog();
        var router = MakeRouter(() => current, log);
        Pray(60, router);   // refreshes at 30; the 60th is held back by the floor
        Assert.Equal(1, log.Refreshes);
        current = Now + FloatingPetController.PrayerRefreshInterval * 2;
        log.EggPrayers = 0;   // a new egg: the persisted prayer count starts over
        Pray(29, router);
        Assert.Equal(1, log.Refreshes);
        Pray(1, router);
        Assert.Equal(2, log.Refreshes);   // the new egg's 30th prayer refreshes again
    }

    [Fact]
    public void ClickingAHatchedPokemonOpensThePopoverWithoutPrayingOrRefreshing()
    {
        var log = new CallLog();
        var router = MakeRouter(() => Now, log);
        Pray(29, router);
        for (var i = 0; i < 50; i++) router.Handle(subjectIsEgg: false);
        Assert.Equal(50, log.Opens);
        Assert.Equal(29, log.Prays);
        Assert.Equal(29, log.DidPray);   // hatched clicks do not touch the prayer hover text
        Assert.Equal(0, log.Refreshes);  // hatched-Pokémon clicks never count
        Pray(1, router);
        Assert.Equal(1, log.Refreshes);  // the 30th real prayer still refreshes
    }

    [Fact]
    public void ShouldRefreshOnPrayerDecision()
    {
        var later = Now + FloatingPetController.PrayerRefreshInterval;
        static bool Decide(long count, long last, DateTimeOffset? at, DateTimeOffset moment) =>
            FloatingPetController.ShouldRefreshOnPrayer(count, last, at, moment);
        Assert.False(Decide(29, 0, null, Now));
        Assert.True(Decide(30, 0, null, Now));
        Assert.False(Decide(30, 1, null, Now));   // already refreshed at this milestone
        Assert.False(Decide(30, 0, Now, Now));    // inside the floor
        Assert.True(Decide(30, 0, Now, later));
        Assert.True(Decide(60, 1, Now, later));
    }
}
