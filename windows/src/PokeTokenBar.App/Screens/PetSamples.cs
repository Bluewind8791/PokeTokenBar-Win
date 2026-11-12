using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Floating-pet renders for #466 (egg prayers, petting hearts, sulking, event pops): the pet square the controller
/// shows (<see cref="PetView"/>) with its hover / event callout above it, on a desktop-grey backdrop. The live pet is a
/// window of its own; these build the same pieces into one element. Sample numbers match the Home sample.
/// </summary>
public static partial class SampleData
{
    private const long PetSampleTokens = 1_980_000;
    private const double PetSampleLimit = 42;

    /// <summary>The egg with three 🙏 frozen mid-flight at fixed spots (the live ones use a random x) and 37 prayers.</summary>
    public static FrameworkElement PetEggPrayers(RenderHarness.Context c)
    {
        var l = new L(c.Language);
        var pet = new PetView(new SpriteView(null, 128), 128, animated: false, l);
        foreach (var (fraction, progress) in new[] { (0.2, 0.3), (0.55, 0.55), (0.85, 0.8) }) pet.AddStillPrayer(fraction, progress);
        return Desktop(c, PetColumn(pet, Hover(l, prayers: 37)));
    }

    /// <summary>A stroked pet: the pixel heart bubble on the sprite's visible top (no callout while hearts show).</summary>
    public static FrameworkElement PetHearts(RenderHarness.Context c)
    {
        var l = new L(c.Language);
        var egg = new PetView(new SpriteView(null, 96), 96, animated: false, l) { HeartsShowing = true };
        var pikachu = new PetView(new SpriteView(25, 96, animated: true), 96, animated: false, l) { HeartsShowing = true };
        return Desktop(c, Ui.HStack(32, PetColumn(egg, null), PetColumn(pikachu, null)));
    }

    /// <summary>The same Pokémon before and after three idle hours: front sprite vs back sprite and the sulk suffix.</summary>
    public static FrameworkElement PetSulk(RenderHarness.Context c)
    {
        var l = new L(c.Language);
        var normal = new PetView(new SpriteView(25, 96, animated: true), 96, animated: false, l);
        var sulking = new PetView(new SpriteView(25, 96, animated: true, back: true), 96, animated: false, l);
        return Desktop(c, Ui.VStack(16,
            PetColumn(normal, Hover(l)),
            PetColumn(sulking, Hover(l, sulkIdle: TimeSpan.FromHours(5)))));
    }

    /// <summary>An evolution line popped by itself above the pet (6 s on the live pet).</summary>
    public static FrameworkElement PetEvent(RenderHarness.Context c)
    {
        var l = new L(c.Language);
        var (from, to) = c.Language switch
        {
            AppLanguage.Ko => ("피츄", "피카츄"),
            AppLanguage.Ja => ("ピチュー", "ピカチュウ"),
            _ => ("Pichu", "Pikachu"),
        };
        var pet = new PetView(new SpriteView(25, 96, animated: true), 96, animated: false, l);
        return Desktop(c, PetColumn(pet, Hover(l, eventLine: l.NotifEvolveBody(from, to))));
    }

    /// <summary>An agent works (#457): the pet's two squash frames side by side (the live pet flips between them twice a
    /// second; energy saver and Windows animation effects off keep the first).</summary>
    public static FrameworkElement PetAgentWorking(RenderHarness.Context c)
    {
        var l = new L(c.Language);
        AgentSquash Frame(bool squashed) =>
            new(FloatingPetController.AgentBobInterval) { Child = new PetView(new SpriteView(25, 96, animated: true), 96, animated: false, l), Squashed = squashed };
        return Desktop(c, Ui.HStack(32, Frame(squashed: false), Frame(squashed: true)));
    }

    /// <summary>Agents waiting: a labeled agent with two more waiting and a message, the unlabeled default, and a label
    /// too wide for the title line (cut, status and count kept).</summary>
    public static FrameworkElement PetAgentWaiting(RenderHarness.Context c)
    {
        var l = new L(c.Language);
        PetView Pet() => new(new SpriteView(25, 96, animated: true), 96, animated: false, l);
        var wide = AgentActivityEvent.SanitizedLabel(new string('W', 60));
        return Desktop(c, Ui.VStack(16,
            AgentColumn(Pet(), new FloatingPetController.Bubble.AgentWaiting("my-web-app", SampleAgentQuestion(c.Language), 2), l),
            AgentColumn(Pet(), new FloatingPetController.Bubble.AgentWaiting(null, null, 0), l),
            AgentColumn(Pet(), new FloatingPetController.Bubble.AgentWaiting(wide, null, 7), l)));
    }

    /// <summary>An agent finished: the labeled done bubble with a message the agent sent (cut to two lines), and the
    /// unlabeled default. The live pet shows it next to the cursor, then walks home.</summary>
    public static FrameworkElement PetAgentDone(RenderHarness.Context c)
    {
        var l = new L(c.Language);
        PetView Pet() => new(new SpriteView(25, 96, animated: true), 96, animated: false, l);
        var message = AgentActivityEvent.SanitizedMessage(c.Language switch
        {
            AppLanguage.Ko => "테스트 128개 통과, 빌드 성공. PR 설명을 확인하고 머지해 주세요. 리뷰어 두 명이 기다리고 있어요.",
            AppLanguage.Ja => "テスト128件成功、ビルド成功。PRの説明を確認してマージしてください。レビュアー2人が待っています。",
            _ => "All 128 tests pass and the build is green. Check the PR description and merge it; two reviewers are waiting.",
        });
        return Desktop(c, Ui.VStack(16,
            AgentColumn(Pet(), new FloatingPetController.Bubble.AgentDone("api", message), l),
            AgentColumn(Pet(), new FloatingPetController.Bubble.AgentDone(null, null), l)));
    }

    private static string SampleAgentQuestion(AppLanguage lang) => lang switch
    {
        AppLanguage.Ko => "어느 브랜치에 머지할까요?",
        AppLanguage.Ja => "どのブランチにマージしますか？",
        _ => "Which branch should I merge into?",
    };

    /// <summary>The pet window's layout: the bubble 8 pt above the pet, both inside the window's width and headroom.</summary>
    private static FrameworkElement AgentColumn(PetView pet, FloatingPetController.Bubble bubble, L l)
    {
        var (width, height) = FloatingPetController.PanelSize(96, showingBubble: true);
        var column = new Stack { Orientation = Orientation.Vertical, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, Width = width, Height = height };
        column.Children.Add(FloatingPetController.SpeechBubble(FloatingPetController.BubbleContent(bubble, l)));
        pet.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(pet);
        return column;
    }

    /// <summary>Home with the companion sulking: back sprite in the card and the sulk status line.</summary>
    public static FrameworkElement HomeSulk(RenderHarness.Context c)
    {
        var s = HomeServices(c, HomeSample.Full);
        s.Companion.Update(s.Store.TodayTokensByProvider, LocalUsageReader.LocalDay(Now), s.Store.MonthTotalTokens, BurnTier.Idle,
            limitWarning: false, hasUsageData: true, timeSinceLastUsage: TimeSpan.FromHours(5));
        return Live.View(s, () => HomeView.Build(s));
    }

    /// <summary>A Pokédex detail page of an individual hatched from an egg prayed over 37 times.</summary>
    public static FrameworkElement DexDetailPrayers(RenderHarness.Context c)
    {
        var s = CollectionServices(c, withForms: false);
        foreach (var entry in s.Companion.State.Dex.Where(e => e.FinalID == 15)) entry.Prayers = new PrayerRecord(37);
        PreloadDetail(s.Companion, 15);
        s.Nav.OpenDexEntry("15");
        return PopoverRoot.Build(s);
    }

    private static string Hover(L l, TimeSpan sulkIdle = default, string? eventLine = null, long prayers = 0) =>
        FloatingPetController.HoverTooltip(PetSampleTokens, PetSampleLimit, LimitPace.LimitDisplayMode.Used, l, sulkIdle, eventLine, prayers);

    /// <summary>Callout centred 6 pt above the pet, as the live tooltip is placed; room for the heart bubble above the sprite.</summary>
    private static FrameworkElement PetColumn(PetView pet, string? callout)
    {
        var column = new Stack { Orientation = Orientation.Vertical, Spacing = 6, VerticalAlignment = VerticalAlignment.Bottom };
        if (callout != null)
        {
            var bubble = HoverCallout.Make(callout);
            bubble.HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(bubble);
        }
        else column.Children.Add(new Border { Height = HeartBubbleArt.Height });
        pet.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(pet);
        return column;
    }

    /// <summary>A desktop-grey backdrop (light / dark), like the macOS pet renders.</summary>
    private static FrameworkElement Desktop(RenderHarness.Context c, FrameworkElement content)
    {
        content.HorizontalAlignment = HorizontalAlignment.Center;
        var grey = (byte)(c.Dark ? 0x29 : 0xC7);
        return new Border
        {
            Child = content, Padding = new Thickness(24), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(grey, grey, grey)),
        };
    }
}
