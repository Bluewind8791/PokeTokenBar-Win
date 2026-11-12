using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Port of AgentActivityTests.swift (#457). <c>poketokenbar://agent</c> is an input any app or web page can send, so
/// parsing must drop everything it does not understand and keep the message plain and short.
/// </summary>
public class AgentActivityTests
{
    private static AgentActivity? Event(string url) => AgentActivityEvent.Parse(url)?.Activity;

    [Fact]
    public void ParsesEveryState()
    {
        Assert.Equal(AgentActivity.Working, Event("poketokenbar://agent?state=working"));
        Assert.Equal(AgentActivity.Waiting(null), Event("poketokenbar://agent?state=waiting"));
        Assert.Equal(AgentActivity.Done(null), Event("poketokenbar://agent?state=done"));
        Assert.Equal(AgentActivity.Idle, Event("poketokenbar://agent?state=idle"));
        Assert.Equal(AgentActivity.Done(null), Event("POKETOKENBAR://Agent?state=DONE"));
    }

    [Fact]
    public void IgnoresUnknownInput()
    {
        Assert.Null(Event("poketokenbar://agent?state=sleeping"));   // unknown state
        Assert.Null(Event("poketokenbar://agent"));                  // missing state
        Assert.Null(Event("poketokenbar://agent?message=hi"));       // message without state
        Assert.Null(Event("poketokenbar://settings?state=done"));    // unknown host
        Assert.Null(Event("poketokenbar:agent?state=done"));         // no host
        Assert.Null(Event("https://agent?state=done"));              // other scheme
        Assert.Null(Event("poketokenbar://agent?state"));            // a state without a value
        Assert.Null(Event(""));
        Assert.Null(AgentActivityEvent.Parse(null));
    }

    [Fact]
    public void MessageIsKeptOnlyForBubbleStates()
    {
        Assert.Equal(AgentActivity.Waiting("Pick a branch"), Event("poketokenbar://agent?state=waiting&message=Pick%20a%20branch"));
        Assert.Equal(AgentActivity.Done("Tests pass"), Event("poketokenbar://agent?state=done&message=Tests%20pass"));
        Assert.Equal(AgentActivity.Working, Event("poketokenbar://agent?state=working&message=ignored"));
        // The first state wins.
        Assert.Equal(AgentActivity.Done("first"), Event("poketokenbar://agent?message=first&state=done&state=working"));
    }

    [Fact]
    public void MessageIsCollapsedToOnePlainLine()
    {
        Assert.Equal(AgentActivity.Done("line 1 line 2"),
            Event("poketokenbar://agent?state=done&message=%20%20line%201%0A%0D%09line%202%20%20"));
        // Bidi override and zero-width space are invisible tricks, not text.
        Assert.Equal("abcdef g", AgentActivityEvent.SanitizedMessage("ab‮cd​ef\u0007g"));
        Assert.Equal("family 👨‍👩‍👧", AgentActivityEvent.SanitizedMessage("family 👨‍👩‍👧"));   // ZWJ emoji stay joined
        Assert.Null(AgentActivityEvent.SanitizedMessage(" \n\t "));
        Assert.Null(AgentActivityEvent.SanitizedMessage(""));
        Assert.Null(AgentActivityEvent.SanitizedMessage(null));
        Assert.Equal(AgentActivity.Waiting(null), Event("poketokenbar://agent?state=waiting&message="));
    }

    [Fact]
    public void OversizedMessageIsCappedWithAnEllipsis()
    {
        const int limit = AgentActivityEvent.MaxMessageLength;
        var exact = new string('a', limit);
        Assert.Equal(exact, AgentActivityEvent.SanitizedMessage(exact));
        var oneOver = AgentActivityEvent.SanitizedMessage(exact + "a")!;   // the first character past the cap is cut
        Assert.Equal(limit, Characters(oneOver));
        Assert.EndsWith("…", oneOver);

        var longText = string.Concat(Enumerable.Repeat("word ", 100));
        var capped = AgentActivityEvent.SanitizedMessage(longText)!;
        Assert.True(Characters(capped) <= limit);
        Assert.EndsWith("…", capped);
        Assert.False(capped[..^1].EndsWith(' '), "no dangling space before the ellipsis");

        var encoded = Uri.EscapeDataString(longText);
        Assert.Equal(AgentActivity.Done(capped), Event($"poketokenbar://agent?state=done&message={encoded}"));
    }

    /// <summary>One character can stack thousands of combining marks: the character cap alone let 79 × ("a" + 2000
    /// marks) through and stalled text measuring on the UI thread.</summary>
    [Fact]
    public void StackedCombiningMarksAreBoundedByScalars()
    {
        var raw = string.Concat(Enumerable.Repeat("a" + new string('́', 2000), 79));
        Assert.Equal(79, Characters(raw));   // precondition: the character cap alone would let it through
        var message = AgentActivityEvent.SanitizedMessage(raw)!;
        Assert.True(message.EnumerateRunes().Count() <= AgentActivityEvent.MaxMessageScalars);
        Assert.True(Characters(message) <= AgentActivityEvent.MaxMessageLength);
        Assert.EndsWith("…", message);
    }

    private static int Characters(string s) => new System.Globalization.StringInfo(s).LengthInTextElements;

    // MARK: Tracker

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000);

    private static void Apply(AgentActivity activity, AgentActivityTracker tracker, DateTimeOffset at, string? label = null)
    {
        var state = activity.Kind.ToString().ToLowerInvariant();
        var message = activity.Message is { } m ? $"&message={m}" : "";
        var labelItem = label is { } l ? $"&label={l}" : "";
        tracker.Apply(AgentActivityEvent.Parse($"poketokenbar://agent?state={state}{message}{labelItem}")!, at);
    }

    [Fact]
    public void WorkingExpiresAfterTenMinutesWithoutNews()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Working, tracker, T0);
        Assert.Equal(T0.AddSeconds(600), tracker.Deadline);
        Assert.False(tracker.Expire(T0.AddSeconds(599)));
        Assert.Equal(AgentActivity.Working, tracker.Activity);
        Assert.True(tracker.Expire(T0.AddSeconds(600)));
        Assert.Equal(AgentActivity.Idle, tracker.Activity);
        Assert.Null(tracker.Deadline);
        Assert.False(tracker.Expire(T0.AddSeconds(10_000)));   // idle has nothing to expire
    }

    [Fact]
    public void AnotherWorkingEventPushesTheTimeoutBack()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Working, tracker, T0);
        Apply(AgentActivity.Working, tracker, T0.AddSeconds(500));
        Assert.False(tracker.Expire(T0.AddSeconds(700)));
        Assert.Equal(AgentActivity.Working, tracker.Activity);
    }

    [Fact]
    public void DoneShowsForSixSeconds()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Done("ok"), tracker, T0);
        Assert.False(tracker.Expire(T0.AddSeconds(5.9)));
        Assert.Equal(AgentActivity.Done("ok"), tracker.Activity);
        Assert.True(tracker.Expire(T0.AddSeconds(6)));
        Assert.Equal(AgentActivity.Idle, tracker.Activity);
    }

    [Fact]
    public void WaitingStaysUntilTheNextEventOrAClick()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Working, tracker, T0);
        Apply(AgentActivity.Waiting(null), tracker, T0.AddSeconds(1));
        Assert.Null(tracker.Deadline);   // waiting must not inherit the working timeout
        Assert.False(tracker.Expire(T0.AddSeconds(86_400)));
        Assert.Equal(AgentActivity.Waiting(null), tracker.Activity);

        Assert.True(tracker.DismissBubble());
        Assert.Equal(AgentActivity.Idle, tracker.Activity);

        Apply(AgentActivity.Waiting(null), tracker, T0);
        Apply(AgentActivity.Working, tracker, T0.AddSeconds(2));
        Assert.Equal(AgentActivity.Working, tracker.Activity);
    }

    [Fact]
    public void ClickDismissesOnlyBubbles()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Working, tracker, T0);
        Assert.False(tracker.DismissBubble());   // a click does not stop work
        Assert.Equal(AgentActivity.Working, tracker.Activity);
        Assert.NotNull(tracker.Deadline);

        Apply(AgentActivity.Done(null), tracker, T0);
        Assert.True(tracker.DismissBubble());
        Assert.Equal(AgentActivity.Idle, tracker.Activity);
        Assert.Null(tracker.Deadline);
        Assert.False(tracker.DismissBubble());
    }

    [Fact]
    public void IdleEventClearsEverything()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Working, tracker, T0);
        Apply(AgentActivity.Idle, tracker, T0);
        Assert.Empty(tracker.Agents);
        Assert.Null(tracker.Deadline);
        Assert.Equal(AgentActivity.Idle, tracker.Activity);
    }

    // MARK: Several agents

    [Fact]
    public void LabelIsParsedAndCleanedLikeAMessage()
    {
        Assert.Equal("my project", AgentActivityEvent.Parse("poketokenbar://agent?state=done&label=%20my%0A%20project%E2%80%AE%20")?.Label);
        Assert.Null(AgentActivityEvent.Parse("poketokenbar://agent?state=done")?.Label);   // no label is the one unlabeled agent
        Assert.Null(AgentActivityEvent.Parse("poketokenbar://agent?state=done&label=%20")?.Label);

        const int limit = AgentActivityEvent.MaxLabelLength;
        var longLabel = AgentActivityEvent.SanitizedLabel(new string('x', 100))!;
        Assert.Equal(limit, Characters(longLabel));
        Assert.EndsWith("…", longLabel);
        var stacked = AgentActivityEvent.SanitizedLabel(string.Concat(Enumerable.Repeat("a" + new string('́', 500), 20)))!;
        Assert.True(stacked.EnumerateRunes().Count() <= limit * 4);
    }

    /// <summary>The regression this change fixes: one session's events wiped another session's state.</summary>
    [Fact]
    public void EachLabelKeepsItsOwnState()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Working, tracker, T0, "web");
        Apply(AgentActivity.Done(null), tracker, T0.AddSeconds(1), "api");
        Assert.True(tracker.Summary.Working, "api's done must not end web's work");
        Assert.Equal(new AgentActivitySummary.Note("api", null), tracker.Summary.Done);

        Assert.True(tracker.Expire(T0.AddSeconds(7)));
        Assert.Null(tracker.Summary.Done);
        Assert.True(tracker.Summary.Working);
        Assert.Equal(AgentActivity.Working, tracker.Activity);
    }

    [Fact]
    public void UnlabeledEventsStillShareOneAgent()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Working, tracker, T0);
        Apply(AgentActivity.Done(null), tracker, T0);
        Assert.False(tracker.Summary.Working, "without labels a done ends the work, as before");
        Assert.Single(tracker.Agents);
    }

    [Fact]
    public void WaitingOutranksWorkingAndCountsTheOthers()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Waiting("first"), tracker, T0, "a");
        Apply(AgentActivity.Working, tracker, T0, "b");
        Apply(AgentActivity.Waiting("latest"), tracker, T0, "c");
        var summary = tracker.Summary;
        Assert.Equal(new AgentActivitySummary.Note("c", "latest"), summary.Waiting);   // the most recent question first
        Assert.Equal(1, summary.OtherWaitingCount);
        Assert.True(summary.Working);
        Assert.Equal(AgentActivity.Waiting("latest"), tracker.Activity);

        Apply(AgentActivity.Working, tracker, T0, "c");   // c's question was answered
        Assert.Equal(new AgentActivitySummary.Note("a", "first"), tracker.Summary.Waiting);
        Assert.Equal(0, tracker.Summary.OtherWaitingCount);
    }

    [Fact]
    public void DoneOverlaysAWaitingAgentOnlyForItsFewSeconds()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Waiting(null), tracker, T0, "a");
        Apply(AgentActivity.Done(null), tracker, T0, "b");
        Assert.Equal(AgentActivity.Done(null), tracker.Activity);
        Assert.Equal("a", tracker.Summary.Waiting?.Label);   // the question is still there underneath
        tracker.Expire(T0 + AgentActivityTracker.DoneDuration);
        Assert.Equal(AgentActivity.Waiting(null), tracker.Activity);
    }

    /// <summary>Overlapping dones: the bubble follows the latest, and each leaves on its own clock.</summary>
    [Fact]
    public void OverlappingDonesShowTheLatest()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Done("a"), tracker, T0, "a");
        Apply(AgentActivity.Done("b"), tracker, T0.AddSeconds(2), "b");
        Apply(AgentActivity.Done("c"), tracker, T0.AddSeconds(1), "c");
        Assert.Equal(new AgentActivitySummary.Note("c", "c"), tracker.Summary.Done);   // latest event, not latest clock
        tracker.Expire(T0 + AgentActivityTracker.DoneDuration + TimeSpan.FromSeconds(1));
        Assert.Equal(new AgentActivitySummary.Note("b", "b"), tracker.Summary.Done);
    }

    [Fact]
    public void EachLabelExpiresOnItsOwnClock()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Working, tracker, T0, "a");
        Apply(AgentActivity.Working, tracker, T0.AddSeconds(300), "b");
        Assert.Equal(T0.AddSeconds(600), tracker.Deadline);   // the earliest deadline arms the timer
        Assert.True(tracker.Expire(T0.AddSeconds(600)));
        Assert.Equal(new[] { "b" }, tracker.Agents.Keys);
        Assert.Equal(T0.AddSeconds(900), tracker.Deadline);
    }

    [Fact]
    public void ClickClearsEveryQuestionAndResultButNotWork()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Waiting(null), tracker, T0, "a");
        Apply(AgentActivity.Waiting(null), tracker, T0, "b");
        Apply(AgentActivity.Done(null), tracker, T0, "c");
        Apply(AgentActivity.Working, tracker, T0, "d");
        Assert.True(tracker.DismissBubble());
        Assert.Equal(new[] { "d" }, tracker.Agents.Keys);
        Assert.False(tracker.DismissBubble());
    }

    [Fact]
    public void IdleClearsOnlyItsOwnLabel()
    {
        var tracker = new AgentActivityTracker();
        Apply(AgentActivity.Working, tracker, T0, "a");
        Apply(AgentActivity.Working, tracker, T0, "b");
        Apply(AgentActivity.Idle, tracker, T0, "a");
        Assert.Equal(new[] { "b" }, tracker.Agents.Keys);
    }

    /// <summary>Labels are untrusted: a flood of them keeps only the newest few agents.</summary>
    [Fact]
    public void TheNumberOfAgentsIsCapped()
    {
        var tracker = new AgentActivityTracker();
        for (var index = 0; index < 50; index++)
            Apply(AgentActivity.Working, tracker, T0.AddSeconds(index), $"agent{index}");
        Assert.Equal(AgentActivityTracker.MaxAgents, tracker.Agents.Count);
        Assert.Equal(Enumerable.Range(42, 8).Select(i => $"agent{i}").ToHashSet(), tracker.Agents.Keys.ToHashSet());   // the oldest go first

        Apply(AgentActivity.Working, tracker, T0.AddSeconds(59), "agent45");   // refreshing a known label evicts nobody
        Assert.True(tracker.Agents.ContainsKey("agent42"));
        Apply(AgentActivity.Working, tracker, T0.AddSeconds(60), "agent42");   // refresh, no eviction
        Assert.Equal(AgentActivityTracker.MaxAgents, tracker.Agents.Count);
        Apply(AgentActivity.Waiting(null), tracker, T0.AddSeconds(61), "new");
        Assert.False(tracker.Agents.ContainsKey("agent43"), "agent42 was refreshed, so agent43 is now the oldest");
        Assert.True(tracker.Agents.ContainsKey("agent42"));
    }
}
