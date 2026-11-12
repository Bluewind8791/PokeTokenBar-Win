using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PokeTokenBar.App;
using PokeTokenBar.Core;
using C = PokeTokenBar.App.FloatingPetController;
using Note = PokeTokenBar.Core.AgentActivitySummary.Note;

namespace PokeTokenBar.App.Tests;

/// <summary>
/// Port of FloatingPetAgentTests.swift (#457, with the #466 additions): the pure helpers behind the floating pet's agent
/// reactions. Unlike macOS, the pet never runs to the cursor on done, so the run tests are not ported.
/// </summary>
public class FloatingPetAgentTests
{
    // MARK: Motion gates

    [Fact]
    public void MotionFollowsEnergySaverAndAnimationEffects()
    {
        Assert.True(C.AllowsMotion(animated: true, reduceMotion: false));
        Assert.False(C.AllowsMotion(animated: false, reduceMotion: false));   // energy saver
        Assert.False(C.AllowsMotion(animated: true, reduceMotion: true));     // Windows animation effects off
        Assert.True(C.ShouldBob(working: true, motionAllowed: true));
        Assert.False(C.ShouldBob(working: true, motionAllowed: false));
        Assert.False(C.ShouldBob(working: false, motionAllowed: true));   // nothing working must not loop
        Assert.True(C.AgentBobInterval > TimeSpan.Zero);   // the loop is an always-visible surface: it needs a cap
        Assert.Equal(TimeSpan.FromSeconds(0.5), C.AgentBobInterval);   // 2 Hz
    }

    // MARK: Bubbles

    [Fact]
    public void LimitAlertOutranksAgentBubbles()
    {
        var alert = new LimitAlert("k", "Claude weekly", IsCritical: true, Utilization: 96);
        var waiting = new AgentActivitySummary { Waiting = new Note("a", "x"), OtherWaitingCount = 2 };
        var both = new AgentActivitySummary { Done = new Note("b", null), Waiting = new Note("a", "x") };
        Assert.Equal(new C.Bubble.Limit(alert), C.BubbleFor(alert, waiting));
        Assert.Equal(new C.Bubble.Limit(alert), C.BubbleFor(alert, new AgentActivitySummary()));
        Assert.Equal(new C.Bubble.Limit(alert), C.BubbleFor(alert, both));   // a limit alert also beats a fresh done
        Assert.Equal(new C.Bubble.AgentWaiting("a", "x", 2), C.BubbleFor(null, waiting));
        Assert.Equal(new C.Bubble.AgentDone("b", null), C.BubbleFor(null, both));   // a fresh done overlays a waiting agent
        Assert.Null(C.BubbleFor(null, new AgentActivitySummary { Working = true }));
        Assert.Null(C.BubbleFor(null, new AgentActivitySummary()));
    }

    [Fact]
    public void AgentSpeechReservesHeadroomAndOutranksHeartsAndEventCallouts()
    {
        foreach (var agent in new[]
                 {
                     new AgentActivitySummary { Waiting = new Note("web", null) },
                     new AgentActivitySummary { Done = new Note("api", null) },
                 })
        {
            var showingBubble = C.IsShowingBubble(alertActive: false, heartsShowing: false, agent);
            Assert.True(showingBubble);   // agent-only speech needs the same window headroom as limit alerts
            Assert.False(C.HeartsShowing(afterStrokeWithAlertActive: false, agent));
            Assert.False(C.ShouldPopEvent(2, 1, petVisible: true, bubbleShowing: showingBubble));
        }
        var working = new AgentActivitySummary { Working = true };
        Assert.True(C.HeartsShowing(afterStrokeWithAlertActive: false, working));   // working only squashes: stroking still works
        Assert.True(C.IsShowingBubble(alertActive: false, heartsShowing: true, working));
        Assert.False(C.IsShowingBubble(alertActive: false, heartsShowing: false, working));
        Assert.False(C.HeartsShowing(afterStrokeWithAlertActive: true, working));
    }

    /// <summary>Generalizing the bubble view must leave the limit-alert copy exactly as before (the Windows bubble
    /// shows <see cref="LimitAlert.Body"/> and lets the view trim it).</summary>
    [Fact]
    public void LimitBubbleContentIsUnchanged()
    {
        foreach (var lang in Enum.GetValues<AppLanguage>())
        {
            var l = new L(lang);
            foreach (var critical in new[] { true, false })
            {
                var alert = new LimitAlert("k", l.ClaudeWeekly, critical, 96, "firstname.lastname@corp.example");
                Assert.Equal(new C.SpeechBubbleContent(critical ? l.NotifCritical : l.NotifWarning, alert.Body(l), critical),
                    C.BubbleContent(new C.Bubble.Limit(alert), l));
            }
        }
    }

    [Fact]
    public void AgentBubbleUsesDefaultsWithoutAMessage() => Sta.Run(() =>
    {
        var l = new L(AppLanguage.En);
        Assert.Equal(new C.SpeechBubbleContent(l.AgentWaitingTitle, l.AgentWaitingDefaultBody, false),
            C.BubbleContent(new C.Bubble.AgentWaiting(null, null, 0), l));
        Assert.Equal(new C.SpeechBubbleContent(l.AgentDoneTitle, "Tests pass", false),
            C.BubbleContent(new C.Bubble.AgentDone(null, "Tests pass"), l));
    });

    [Fact]
    public void LabeledTitlesNameTheAgentAndCountTheRest() => Sta.Run(() =>
    {
        var l = new L(AppLanguage.En);
        Assert.Equal("web · waiting", C.BubbleContent(new C.Bubble.AgentWaiting("web", null, 0), l).Title);
        Assert.Equal("web · waiting +2", C.BubbleContent(new C.Bubble.AgentWaiting("web", null, 2), l).Title);
        Assert.Equal("api · done", C.BubbleContent(new C.Bubble.AgentDone("api", null), l).Title);
        // Unlabeled keeps its title, cut at a word if the column is too narrow for the count.
        var unlabeled = C.BubbleContent(new C.Bubble.AgentWaiting(null, null, 1), l).Title;
        Assert.True(C.MeasureSpeechBubbleLayout(unlabeled, "").UnclampedTitleWidth <= C.BubbleContentWidth);
        Assert.EndsWith(" +1", unlabeled);
        var kept = unlabeled[..^" +1".Length];
        if (kept != l.AgentWaitingTitle)
        {
            Assert.EndsWith("…", kept);
            Assert.StartsWith(kept[..^1] + " ", l.AgentWaitingTitle);   // cut between words
        }
        // Too long for any column: the cut lands between words, not inside one.
        const string sentence = "Your coding agent is waiting for an answer from you";
        var cut = C.AgentTitle(null, sentence, "", "+3");
        Assert.EndsWith("… +3", cut);
        Assert.StartsWith(cut[..^"… +3".Length] + " ", sentence);
        var wide = new string('W', AgentActivityEvent.MaxLabelLength);   // fits the label cap, not the line
        var longTitle = C.BubbleContent(new C.Bubble.AgentWaiting(wide, null, 7), l).Title;
        Assert.NotEqual($"{wide} · waiting +7", longTitle);   // precondition: the full title does not fit one line
        Assert.StartsWith("WWW", longTitle);
        Assert.EndsWith("… · waiting +7", longTitle);   // the label is cut, the status and count stay
    });

    /// <summary>Agent text is untrusted and up to 80 characters: in every language it must be cut with an ellipsis
    /// before the view's height cap would clip it, and the bubble plus its 8 pt gap must fit the window's headroom.</summary>
    [Fact]
    public void AgentBubbleTextNeverTruncates() => Sta.Run(() =>
    {
        const int limit = AgentActivityEvent.MaxMessageLength;
        string?[] messages =
        [
            null,
            "Tests pass",
            AgentActivityEvent.SanitizedMessage(new string('M', 200)),
            AgentActivityEvent.SanitizedMessage(new string('あ', 200)),
            AgentActivityEvent.SanitizedMessage(string.Concat(Enumerable.Repeat("Supercalifragilistic ", 10))),
            new string('W', limit),
            AgentActivityEvent.SanitizedMessage(string.Concat(Enumerable.Repeat("a" + new string('́', 2000), 79))),
            AgentActivityEvent.SanitizedMessage(string.Concat(Enumerable.Repeat("가나다라마바사 ", 20))),
        ];
        string?[] labels =
        [
            null,
            "my-web-app",
            AgentActivityEvent.SanitizedLabel(new string('W', 60)),
            AgentActivityEvent.SanitizedLabel(string.Concat(Enumerable.Repeat("가나다라", 10))),
        ];
        foreach (var lang in Enum.GetValues<AppLanguage>())
        {
            var l = new L(lang);
            foreach (var message in messages)
                foreach (var label in labels)
                    foreach (C.Bubble bubble in new C.Bubble[]
                             {
                                 new C.Bubble.AgentWaiting(label, message, 0),
                                 new C.Bubble.AgentWaiting(label, message, AgentActivityTracker.MaxAgents - 1),
                                 new C.Bubble.AgentDone(label, message),
                             })
                    {
                        var content = C.BubbleContent(bubble, l);
                        var layout = C.MeasureSpeechBubbleLayout(content.Title, content.Body);
                        Assert.False(layout.WouldTruncate, $"{lang}: {content.Body}");
                        Assert.True(layout.UnclampedTitleWidth <= C.BubbleContentWidth, $"{lang} title wraps: {content.Title}");
                        Assert.True(layout.Size.Height + 8 <= C.BubbleHeadroom, $"{lang} bubble overflows the headroom ({layout.Size.Height})");
                    }
        }
    });

    [Fact]
    public void FittingKeepsShortTextAndCutsLongText() => Sta.Run(() =>
    {
        Assert.Equal("Tests pass", C.FittedBubbleBody("Tests pass", "Done!"));
        var longText = new string('W', 80);
        Assert.True(C.MeasureSpeechBubbleLayout("Done!", longText).WouldTruncate);   // precondition: the view would clip it
        var fitted = C.FittedBubbleBody(longText, "Done!");
        Assert.EndsWith("…", fitted);
        Assert.StartsWith(fitted[..^1], longText);
        Assert.False(C.MeasureSpeechBubbleLayout("Done!", fitted).WouldTruncate);
        // One more character must not fit — the cut is as long as possible.
        Assert.True(C.MeasureSpeechBubbleLayout("Done!", longText[..fitted.Length] + "…").WouldTruncate);
    });

    /// <summary>The measurement stands in for the view: the body text block the bubble builds must wrap to the same
    /// height, or the fitted text could still be clipped (Latin, CJK fallback fonts, Hangul).</summary>
    [Fact]
    public void MeasurementMatchesTheBubbleView() => Sta.Run(() =>
    {
        foreach (var body in new[]
                 {
                     "Tests pass", new string('W', 80), new string('あ', 40), string.Concat(Enumerable.Repeat("가나다 ", 30)),
                     "Which branch should I merge into? main, develop or release/2.6",
                 })
        {
            var bubble = C.SpeechBubble(new C.SpeechBubbleContent("Done!", body, false));
            var text = Sta.Descendants(bubble).OfType<TextBlock>().Single(t => t.Text == body);
            text.MaxHeight = double.PositiveInfinity;
            text.Measure(new Size(C.BubbleContentWidth, double.PositiveInfinity));
            var measured = C.MeasureSpeechBubbleLayout("Done!", body);
            var lines = Math.Round(text.DesiredSize.Height / (text.FontSize * text.FontFamily.LineSpacing));
            Assert.True(Math.Abs(text.DesiredSize.Height - lines * text.FontSize * text.FontFamily.LineSpacing) < 1, body);
            Assert.Equal(measured.WouldTruncate, text.DesiredSize.Height > C.BubbleBodyMaxHeight + 0.01);
            Assert.Equal(measured.BodyLineCount, (int)lines);
        }
    });

    // MARK: Squash element

    [Fact]
    public void TheSquashRunsOnlyWhileActiveAndRestsUnsquashed() => Sta.Run(() =>
    {
        var squash = new AgentSquash(C.AgentBobInterval);
        Assert.False(squash.Active);
        Assert.False(squash.Squashed);
        squash.Active = true;
        Assert.True(squash.Active);
        squash.Squashed = true;
        Assert.Equal(new Point(0.5, 1), squash.RenderTransformOrigin);   // squashed toward the feet
        var scale = (System.Windows.Media.ScaleTransform)squash.RenderTransform;
        Assert.Equal((1.03, 0.94), (scale.ScaleX, scale.ScaleY));
        squash.Active = false;   // idle leaves no loop behind and the pet unsquashed
        Assert.False(squash.Squashed);
        Assert.Equal((1.0, 1.0), (scale.ScaleX, scale.ScaleY));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentSquash(TimeSpan.Zero));
    });
}

/// <summary>
/// Controller wiring, not just the pure helpers (port of FloatingPetAgentWiringTests). The display is "off" so no window
/// is ever put on screen; the public entry points stand in for the timer, the pipe and the window callbacks, and the
/// click tests raise mouse events on the window the controller builds.
/// </summary>
public class FloatingPetAgentWiringTests
{
    private sealed class FakePower : IPowerStatus
    {
        public bool DisplayOn { get; set; }
        public bool LowPower { get; set; }
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();
    }

    private sealed class NoLimits : IClaudeLimitsProviding
    {
        public Task<LimitStatus> Fetch(bool allowKeychainPrompt) => Task.FromResult(new LimitStatus());
    }

    private sealed class NoStatus : IProviderStatusProviding
    {
        public Task<Dictionary<string, ProviderStatus>> Fetch() => Task.FromResult(new Dictionary<string, ProviderStatus>());
    }

    private sealed class Fixture : IDisposable
    {
        public required FloatingPetController Controller;
        public required UsageStore Store;
        public required CompanionStore Companion;
        public required AppSettings Settings;
        public required FakePower Power;
        public required string SavePath;
        public int Opened;

        public void Dispose()
        {
            Controller.Dispose();
            Store.Dispose();
            try { File.Delete(SavePath); } catch (IOException) { }
        }
    }

    private static Fixture Make(bool petEnabled = true, bool hatched = true, bool? motion = true)
    {
        var settings = new AppSettings(null);
        var store = new UsageStore(providers: [], claudeLimitsProvider: new NoLimits(), discoverClaudeConfigDirs: () => [],
            readDefaultIdentity: () => null, readLastPrompt: _ => null,
            claudeUsageEntries: _ => Task.FromResult<IReadOnlyList<UsageEntry>>([]), readPromptHistory: _ => new(),
            statusProvider: new NoStatus(), autoRefresh: false, settings: settings);
        var state = new CompanionState { InstallBaselineSet = true };
        if (hatched)
            state.Active = new MonState(1, [1], stageIndex: 0, usedAtStage: 0, rarity: Rarity.Common, totalForms: 1);
        var path = Path.Combine(Path.GetTempPath(), $"ptb-agent-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, CompanionJson.Serialize(state));
        var companion = new CompanionStore(filePath: path, settings: settings);
        var power = new FakePower();   // display off: nothing is shown
        Fixture? fixture = null;
        var controller = new FloatingPetController(store, companion, power, () => fixture!.Opened++, settings, motionOverride: motion);
        fixture = new Fixture { Controller = controller, Store = store, Companion = companion, Settings = settings, Power = power, SavePath = path };
        store.FloatingPetEnabled = petEnabled;
        return fixture;
    }

    private static void Send(FloatingPetController controller, string state, string? label = null) =>
        controller.HandleAgentEvent(AgentActivityEvent.Parse($"poketokenbar://agent?state={state}" + (label is { } l ? $"&label={l}" : ""))!);

    [Fact]
    public void EventsAreDroppedWhileThePetOrTheSettingIsOff() => Sta.Run(() =>
    {
        using var off = Make(petEnabled: false);
        Send(off.Controller, "working");
        Assert.Equal(AgentActivity.Idle, off.Controller.Tracker.Activity);   // floating pet off

        using var fixture = Make();
        fixture.Store.FloatingPetAgentActivity = false;
        Send(fixture.Controller, "waiting");
        Assert.Equal(AgentActivity.Idle, fixture.Controller.Tracker.Activity);   // setting off
        Assert.Null(fixture.Controller.AgentExpiryFireDate);
    });

    [Fact]
    public void TurningTheSettingOffResetsAgentState() => Sta.Run(() =>
    {
        using var fixture = Make();
        Send(fixture.Controller, "working");
        Assert.Equal(AgentActivity.Working, fixture.Controller.Tracker.Activity);
        Assert.NotNull(fixture.Controller.AgentExpiryFireDate);

        fixture.Store.FloatingPetAgentActivity = false;
        Assert.Equal(AgentActivity.Idle, fixture.Controller.Tracker.Activity);
        Assert.Null(fixture.Controller.AgentExpiryFireDate);   // no timer left behind
        Assert.False(fixture.Controller.IsBobbing);
    });

    /// <summary>A done shows its bubble and leaves the saved home alone, through its expiry.</summary>
    [Fact]
    public void ADoneNeverMovesTheSavedHome() => Sta.Run(() =>
    {
        using var fixture = Make();
        fixture.Settings.Set("floatingPetOriginX", 300.0);
        fixture.Settings.Set("floatingPetOriginY", 200.0);

        Send(fixture.Controller, "done");
        Assert.Equal(new C.Bubble.AgentDone(null, null), C.BubbleFor(null, fixture.Controller.Tracker.Summary));
        Assert.Equal(300, fixture.Settings.GetDouble("floatingPetOriginX"));
        Assert.Equal(200, fixture.Settings.GetDouble("floatingPetOriginY"));

        var deadline = fixture.Controller.Tracker.Deadline!.Value;
        fixture.Controller.ExpireAgentActivity(deadline);
        Assert.Equal(AgentActivity.Idle, fixture.Controller.Tracker.Activity);
        Assert.Equal(300, fixture.Settings.GetDouble("floatingPetOriginX"));
        Assert.Equal(200, fixture.Settings.GetDouble("floatingPetOriginY"));
    });

    [Fact]
    public void ADropWhileDoneShowsBecomesTheNewHome() => Sta.Run(() =>
    {
        using var fixture = Make();
        fixture.Settings.Set("floatingPetOriginX", 300.0);
        fixture.Settings.Set("floatingPetOriginY", 200.0);

        Send(fixture.Controller, "done");
        var dropped = new Point(900, 100);
        fixture.Controller.PanelDidMove(dropped);

        var petSize = fixture.Store.FloatingPetSize;
        var (w, h) = C.PanelSize(petSize, showingBubble: true);
        var pet = C.PetOriginOf(dropped, petSize, new Size(w, h));
        Assert.Equal(pet.X, fixture.Settings.GetDouble("floatingPetOriginX"));
        Assert.Equal(pet.Y, fixture.Settings.GetDouble("floatingPetOriginY"));
    });

    [Fact]
    public void ClickDismissesTheBubbleAndStillOpensThePopover() => Sta.Run(() =>
    {
        using var fixture = Make();
        Send(fixture.Controller, "waiting");
        fixture.Controller.PetClicked();
        Assert.Equal(AgentActivity.Idle, fixture.Controller.Tracker.Activity);
        Assert.Equal(1, fixture.Opened);

        Send(fixture.Controller, "working");
        fixture.Controller.PetClicked();
        Assert.Equal(AgentActivity.Working, fixture.Controller.Tracker.Activity);   // a click does not stop work
        Assert.Equal(2, fixture.Opened);
    });

    private static void Click(Window w)
    {
        w.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        w.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    }

    /// <summary>Clicks the window the controller builds, so resolving the click route to either feature alone cannot
    /// silently discard the other (defect log: "Floating-pet features share one click route").</summary>
    [Fact]
    public void WindowClickAcknowledgesAgentsAndPraysOverAnEgg() => Sta.Run(() =>
    {
        using var fixture = Make(hatched: false);
        Assert.Null(fixture.Companion.RepresentativeSubject.SpeciesID);
        var window = fixture.Controller.MakeWindow();

        Send(fixture.Controller, "waiting");
        Click(window);
        Assert.Equal(AgentActivity.Idle, fixture.Controller.Tracker.Activity);
        Assert.Equal(1, fixture.Companion.State.EggPrayers?.Count);
        Assert.Equal(0, fixture.Opened);

        Send(fixture.Controller, "working");
        Click(window);
        Assert.Equal(AgentActivity.Working, fixture.Controller.Tracker.Activity);
        Assert.Equal(2, fixture.Companion.State.EggPrayers?.Count);
        Assert.Equal(0, fixture.Opened);

        Send(fixture.Controller, "done");
        fixture.Controller.OpenFromMenu();
        Assert.Equal(AgentActivity.Idle, fixture.Controller.Tracker.Activity);   // the unlabeled done ended the work and was acknowledged
        Assert.Equal(1, fixture.Opened);   // context-menu Open still opens an egg
        Assert.Equal(2, fixture.Companion.State.EggPrayers?.Count);
        window.Close();
    });

    [Fact]
    public void WindowClickAcknowledgesAgentsAndOpensAHatchedPet() => Sta.Run(() =>
    {
        using var fixture = Make();
        Assert.NotNull(fixture.Companion.RepresentativeSubject.SpeciesID);
        var window = fixture.Controller.MakeWindow();
        Send(fixture.Controller, "waiting");
        Click(window);
        Assert.Equal(AgentActivity.Idle, fixture.Controller.Tracker.Activity);
        Assert.Equal(1, fixture.Opened);
        Assert.Null(fixture.Companion.State.EggPrayers);
        window.Close();
    });

    [Fact]
    public void ExpiryRearmsWhenTheTimerFiresEarly() => Sta.Run(() =>
    {
        using var fixture = Make();
        Send(fixture.Controller, "done");
        var deadline = fixture.Controller.Tracker.Deadline!.Value;
        Assert.Equal(deadline, fixture.Controller.AgentExpiryFireDate);

        fixture.Controller.ExpireAgentActivity(deadline.AddSeconds(-0.5));
        Assert.Equal(AgentActivity.Done(null), fixture.Controller.Tracker.Activity);
        Assert.Equal(deadline, fixture.Controller.AgentExpiryFireDate);   // an early fire must arm again

        fixture.Controller.ExpireAgentActivity(deadline);
        Assert.Equal(AgentActivity.Idle, fixture.Controller.Tracker.Activity);
        Assert.Null(fixture.Controller.AgentExpiryFireDate);

        Send(fixture.Controller, "waiting");
        Assert.Null(fixture.Controller.AgentExpiryFireDate);   // waiting arms no timer
    });

    // MARK: Energy

    /// <summary>Energy saver: an agent works but the pet stands still; it squashes again once energy saver is off
    /// (unless Windows animation effects are off).</summary>
    [Fact]
    public void EnergySaverKeepsThePetStill() => Sta.Run(() =>
    {
        using var fixture = Make(motion: null);
        fixture.Power.LowPower = true;
        fixture.Power.Raise();
        Send(fixture.Controller, "working");
        Assert.False(fixture.Controller.IsBobbing);

        fixture.Power.LowPower = false;
        fixture.Power.Raise();
        Assert.Equal(SystemParameters.ClientAreaAnimation, fixture.Controller.IsBobbing);
    });

    /// <summary>A hidden pet (display off) runs no squash timer, even while an agent works.</summary>
    [Fact]
    public void AHiddenPetRunsNoSquashTimer() => Sta.Run(() =>
    {
        using var fixture = Make();
        Send(fixture.Controller, "working");
        Assert.True(fixture.Controller.IsBobbing);
        Assert.False(fixture.Controller.IsSquashTicking);
    });

    // MARK: Several agents

    private static C.Bubble? Bubble(FloatingPetController controller) => C.BubbleFor(null, controller.Tracker.Summary);

    /// <summary>One agent's done must not end another agent's work: the squash resumes after the done.</summary>
    [Fact]
    public void DoneWhileAnotherAgentWorksKeepsTheSquash() => Sta.Run(() =>
    {
        using var fixture = Make();
        Send(fixture.Controller, "working", "web");
        Assert.True(fixture.Controller.IsBobbing);

        Send(fixture.Controller, "done", "api");
        Assert.Equal(new C.Bubble.AgentDone("api", null), Bubble(fixture.Controller));
        Assert.True(fixture.Controller.IsBobbing);   // web is still working

        fixture.Controller.ExpireAgentActivity(fixture.Controller.Tracker.Agents["api"].Deadline!.Value);
        Assert.Null(Bubble(fixture.Controller));
        Assert.True(fixture.Controller.IsBobbing);   // and keeps squashing for web
    });

    /// <summary>A done overlays a waiting agent for its few seconds; the question comes back after.</summary>
    [Fact]
    public void DoneWhileAnotherAgentWaitsBringsTheQuestionBack() => Sta.Run(() =>
    {
        using var fixture = Make();
        Send(fixture.Controller, "waiting", "web");
        Send(fixture.Controller, "working", "api");
        Assert.Equal(new C.Bubble.AgentWaiting("web", null, 0), Bubble(fixture.Controller));   // work must not hide the question

        Send(fixture.Controller, "done", "api");
        Assert.Equal(new C.Bubble.AgentDone("api", null), Bubble(fixture.Controller));
        fixture.Controller.ExpireAgentActivity(fixture.Controller.Tracker.Agents["api"].Deadline!.Value);
        Assert.Equal(new C.Bubble.AgentWaiting("web", null, 0), Bubble(fixture.Controller));
    });

    [Fact]
    public void ClickClearsEveryWaitingAgentButNotWork() => Sta.Run(() =>
    {
        using var fixture = Make();
        Send(fixture.Controller, "waiting", "web");
        Send(fixture.Controller, "waiting", "api");
        Send(fixture.Controller, "working", "cli");
        Assert.Equal(new C.Bubble.AgentWaiting("api", null, 1), Bubble(fixture.Controller));

        fixture.Controller.PetClicked();
        Assert.Null(Bubble(fixture.Controller));   // both questions were seen
        Assert.True(fixture.Controller.Tracker.Summary.Working);
        Assert.True(fixture.Controller.IsBobbing);
        Assert.Equal(1, fixture.Opened);
    });
}

/// <summary>WPF elements and dispatcher timers need an STA thread with a dispatcher context, as on the app's UI thread.</summary>
internal static class Sta
{
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
