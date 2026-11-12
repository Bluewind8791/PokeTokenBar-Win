using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PokeTokenBar.App;
using PokeTokenBar.Core;

namespace PokeTokenBar.App.Tests;

/// <summary>
/// Port of FloatingPetPettingTests.swift (#466): the pure stroke detector (fed the (x, time) samples real strokes
/// produce), the heart bubble's priority, art and anchoring. Plus the energy rule for the new animations: in energy
/// saver nothing of hearts, particles or the wobble animates.
/// </summary>
public class PettingDetectorTests
{
    private const double Stroke = 14;

    /// <summary>Feeds samples every 50 ms along the given x waypoints; returns how many samples triggered.</summary>
    private static int Triggers(IEnumerable<double> waypoints, PettingDetector detector, double step = 0.05)
    {
        double time = 100;
        var count = 0;
        foreach (var x in waypoints)
        {
            if (detector.Feed(x, time)) count++;
            time += step;
        }
        return count;
    }

    private static PettingDetector Detector(double minStroke = Stroke) => new() { MinStroke = minStroke };

    [Fact]
    public void WiggleTriggers() =>
        // Four 15 pt turns = 4 reversals; every reversal from the third on fires, so a longer wiggle keeps the bubble up.
        Assert.Equal(2, Triggers([10, 25, 40, 25, 10, 25, 40, 25, 10, 25, 40], Detector()));

    [Fact]
    public void StraightPassDoesNotTrigger() =>
        Assert.Equal(0, Triggers(Enumerable.Range(0, 21).Select(i => i * 10.0), Detector()));

    [Fact]
    public void SlowBackAndForthDoesNotTrigger() =>
        // 30 pt strokes at 0.8 s per stroke: three reversals span 1.6 s, outside the 1.2 s window.
        Assert.Equal(0, Triggers([10, 40, 10, 40, 10, 40, 10, 40], Detector(), step: 0.8));

    [Fact]
    public void LongPauseResetsState()
    {
        var detector = Detector();
        Assert.Equal(0, Triggers([10, 40, 10, 40], detector));   // only 2 reversals so far
        var time = 100 + PettingDetector.Window + 1;
        // After the pause one more reversal must not complete the old count.
        Assert.False(detector.Feed(10, time));
        Assert.False(detector.Feed(40, time + 0.05));
        Assert.False(detector.Feed(10, time + 0.1));
    }

    [Fact]
    public void JitterBelowMinimumStrokeDoesNotTrigger() =>
        Assert.Equal(0, Triggers(Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? 10.0 : 18.0), Detector()));

    /// <summary>After one real stroke has set a direction, small jitter at its end must not count as reversals.</summary>
    [Fact]
    public void JitterAfterAStrokeDoesNotTrigger() =>
        Assert.Equal(0, Triggers(new double[] { 10, 40 }.Concat(Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? 33.0 : 40.0)), Detector()));

    [Fact]
    public void MinimumStrokeScalesTheThreshold()
    {
        // 20 pt strokes: enough for a 14 pt minimum, too short for a 40 pt one (big pet).
        double[] wiggle = [10, 30, 10, 30, 10, 30, 10];
        Assert.True(Triggers(wiggle, Detector(14)) > 0);
        Assert.Equal(0, Triggers(wiggle, Detector(40)));
    }

    [Fact]
    public void ResetForgetsReversals()
    {
        var detector = Detector();
        Assert.Equal(0, Triggers([10, 40, 10, 40], detector));
        detector.Reset();   // mouse leave or mouse down
        Assert.Equal(0, Triggers([10, 40, 10], detector));   // one more reversal alone must not trigger
    }
}

public class StrokeHeartBubbleTests
{
    [Fact]
    public void StrokeShowsHeartsUnlessALimitAlertIsActive()
    {
        Assert.True(FloatingPetController.HeartsShowing(afterStrokeWithAlertActive: false));
        Assert.False(FloatingPetController.HeartsShowing(afterStrokeWithAlertActive: true));
    }

    [Fact]
    public void HeartsDurationIsAboutTwoSeconds() => Assert.Equal(TimeSpan.FromMilliseconds(1800), FloatingPetController.HeartDuration);

    [Fact]
    public void HeartsOrAlertReserveBubbleHeadroom()
    {
        Assert.True(FloatingPetController.IsShowingBubble(alertActive: false, heartsShowing: true));
        Assert.True(FloatingPetController.IsShowingBubble(alertActive: true, heartsShowing: false));
        Assert.False(FloatingPetController.IsShowingBubble(alertActive: false, heartsShowing: false));
        Assert.Equal((180, 96 + 72), FloatingPetController.PanelSize(96, showingBubble: true));
    }

    [Fact]
    public void ToolTipIsClearedOnlyWhileHeartsShow()
    {
        Assert.Null(FloatingPetController.NativeToolTip("Today: 1 tokens", heartsShowing: true));
        Assert.Equal("Today: 1 tokens", FloatingPetController.NativeToolTip("Today: 1 tokens", heartsShowing: false));
    }

    [Fact]
    public void HeartBubbleArtRowsShareOneWidth() =>
        Assert.All(HeartBubbleArt.Rows, row => Assert.Equal(HeartBubbleArt.Rows[0].Length, row.Length));

    [Fact]
    public void BubbleHeartAndTailAreSymmetricExceptShadeAndHighlight()
    {
        foreach (var row in HeartBubbleArt.Rows)
        {
            var plain = new string(row.Select(c => "RDH".Contains(c) ? 'R' : c).ToArray());
            Assert.Equal(plain, new string(plain.Reverse().ToArray()));
        }
    }

    [Fact]
    public void TailTipSitsAtTheHorizontalCenter()
    {
        var tip = HeartBubbleArt.Rows[^1];
        Assert.Equal(HeartBubbleArt.Rows[0].Length / 2, tip.IndexOf('O'));
        Assert.Equal(1, tip.Count(c => c == 'O'));   // the tail ends in a single pixel
    }

    [Fact]
    public void HeartBubbleFitsThePanelAndHeadroom()
    {
        Assert.True(HeartBubbleArt.Width <= FloatingPetController.BubbleMinWidth);
        // Worst case: the sprite touches its box top (visible top 0), so the bubble sits highest.
        Assert.True(HeartBubbleArt.Height - HeartBubbleArt.TailOverlap <= FloatingPetController.BubbleHeadroom);
        var top = SpriteVisibleTop.BubbleTopOffset(0, HeartBubbleArt.Height, HeartBubbleArt.TailOverlap);
        Assert.True(top >= -FloatingPetController.BubbleHeadroom);   // never clipped by the window top
    }

    // MARK: Anchoring to the visible sprite

    [Fact]
    public void TallSpriteTopIsNearTheBoxTop() =>
        // 36×66 canvas fills the 96 pt box height; the sprite starts 5% below the canvas top.
        Assert.Equal(4.8, SpriteVisibleTop.Inset(96, 36, 66, 0.05), 3);

    [Fact]
    public void WideShortSpriteTopSitsBelowTheBoxTop() =>
        // 60×30 canvas fits 96×48: 24 pt of empty box above, then 10% of the canvas is transparent.
        Assert.Equal(24 + 4.8, SpriteVisibleTop.Inset(96, 60, 30, 0.1), 3);

    [Fact]
    public void EggTopUsesItsOwnOpaqueBounds() =>
        Assert.Equal(9.6, SpriteVisibleTop.Inset(96, 30, 30, 0.1), 3);

    [Fact]
    public void BubbleTailLandsJustInsideTheVisibleTop()
    {
        var top = SpriteVisibleTop.BubbleTopOffset(24, HeartBubbleArt.Height, 3);
        Assert.Equal(24 + 3, top + HeartBubbleArt.Height, 3);   // tail tip 3 pt below the visible top
    }

    [Fact]
    public void BubbleOffsetSnapsToWholePoints()
    {
        var top = SpriteVisibleTop.BubbleTopOffset(3.84, 32, 3);
        Assert.Equal(Math.Round(top), top);
        Assert.Equal(-25, top);
    }

    [Fact]
    public void AnchorIsTheMedianFrame()
    {
        // Rotom-Heat-like: most frames start ~0.3 down the canvas, a few jump to the top.
        Assert.Equal(0.3, SpriteVisibleTop.AnchorFraction([0, 0, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3]), 3);
        Assert.Equal(0.2, SpriteVisibleTop.AnchorFraction([0.2]), 3);
        Assert.Equal(0, SpriteVisibleTop.AnchorFraction([]));
    }

    /// <summary>10×10 test image from per-pixel opaque flags; row 0 is the top.</summary>
    private static BitmapSource Image(Func<int, int, bool> opaque)
    {
        const int side = 10;
        var pixels = new byte[side * side * 4];
        for (var row = 0; row < side; row++)
            for (var column = 0; column < side; column++)
                if (opaque(row, column))
                {
                    var i = (row * side + column) * 4;
                    pixels[i + 2] = 255;
                    pixels[i + 3] = 255;
                }
        return BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, pixels, side * 4);
    }

    [Fact]
    public void OpaqueTopFractionScansTheFirstVisibleRow() =>
        Assert.Equal(0.3, SpriteVisibleTop.OpaqueTopFraction(Image((row, column) => row == 3 && column == 4)), 3);

    [Fact]
    public void SideColumnsDoNotDecideTheAnchor() =>
        // Tall side columns (raised arms) from row 0; the centre (horn / head) only starts at row 5.
        Assert.Equal(0.5, SpriteVisibleTop.OpaqueTopFraction(Image((row, column) =>
            column is 0 or 9 || (column is >= 4 and <= 5 && row >= 5))), 3);

    [Fact]
    public void EmptyCentralBandFallsBackToTheFullWidth() =>
        Assert.Equal(0.2, SpriteVisibleTop.OpaqueTopFraction(Image((row, column) => column is 0 or 9 && row >= 2)), 3);

    [Fact]
    public void EveryArtCellHasAColorOrIsTransparent()
    {
        var cells = HeartBubbleArt.Rows.SelectMany(r => r).ToHashSet();
        Assert.Equal(new HashSet<char> { 'O', 'W', 'R', 'D', 'H', '.' }, cells);
        foreach (var cell in cells.Where(c => c != '.')) Assert.NotNull(HeartBubbleArt.ColorFor(cell));
        Assert.Null(HeartBubbleArt.ColorFor('.'));
    }

    [Fact]
    public void HeartsLabelIsLocalizedInEveryLanguage()
    {
        var labels = Enum.GetValues<AppLanguage>().Select(lang => new L(lang).FloatingPetHeartsLabel).ToList();
        Assert.DoesNotContain("", labels);
        Assert.True(labels.Distinct().Count() > 1);
    }
}

/// <summary>
/// Energy rule (defect-log "에너지 (상시 표시 애니메이션)"): the floating pet goes static in energy saver. The new
/// one-shot animations (heart pop-in, prayer float, egg wobble) must not run there; with motion allowed they do.
/// </summary>
public class PetViewEnergyTests
{
    private static void Sta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            // As on the app's UI thread: awaits and EmojiImages' continuations come back here.
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>A pet over an empty sprite directory, so nothing is read from the user's cache.</summary>
    private static (PetView Pet, SpriteView Sprite, PrayerBurst Burst) Pet(bool animated)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ptb-pet-{Guid.NewGuid():N}");
        var sprite = new SpriteView(null, 96, store: new SpriteStore(dir));
        var burst = new PrayerBurst();
        return (new PetView(sprite, 96, animated, new L(AppLanguage.En), burst), sprite, burst);
    }

    private static FrameworkElement Heart(PetView pet) =>
        Descendants(pet).OfType<FrameworkElement>()
            .Single(e => System.Windows.Automation.AutomationProperties.GetName(e) == new L(AppLanguage.En).FloatingPetHeartsLabel);

    private static Canvas Particles(PetView pet) =>
        pet.Children.OfType<Canvas>().First(c => c.Width == 96);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeartsPopInOnlyWithMotion(bool animated) => Sta(() =>
    {
        var (pet, _, _) = Pet(animated);
        Assert.False(pet.HeartsShowing);
        pet.HeartsShowing = true;
        Assert.Equal(Visibility.Visible, Heart(pet).Visibility);
        Assert.Equal(animated, Heart(pet).HasAnimatedProperties);
        pet.HeartsShowing = false;   // removal is instant
        Assert.Equal(Visibility.Collapsed, Heart(pet).Visibility);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrayersFloatAndWobbleOnlyWithMotion(bool animated) => Sta(() =>
    {
        var (pet, sprite, burst) = Pet(animated);
        burst.Spawn(0.5);
        var particle = Assert.Single(Particles(pet).Children.OfType<UIElement>());
        Assert.Equal(animated, particle.HasAnimatedProperties);
        Assert.Equal(animated, ((RotateTransform)sprite.RenderTransform).HasAnimatedProperties);
        // Still shown in energy saver (the click is acknowledged), half way up and half faded.
        if (!animated) Assert.Equal(0.5, particle.Opacity, 3);
        burst.Clear();
        Assert.Empty(Particles(pet).Children);
    });

    [Fact]
    public void ADetachedPetIgnoresTheBurst() => Sta(() =>
    {
        var (pet, _, burst) = Pet(animated: true);
        pet.Detach();
        burst.Spawn(0.5);
        Assert.Empty(Particles(pet).Children);
    });
}
