using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PokeTokenBar.Core;

namespace PokeTokenBar.App;

/// <summary>
/// Port of <c>FloatingPetController</c> / <c>FloatingPetView</c> (FloatingPetPanel.swift): an opt-in,
/// always-on-top, non-activating desktop pet. Drag to move (position remembered), click to pray over the egg or
/// open the popover for a hatched Pokémon, right-click for Open / Hide / Back view, hover for today's usage, a
/// speech bubble for limit alerts, a heart bubble after stroking it, and a callout that pops by itself for a
/// hatch / evolution / graduation / Ditto line.
/// Agent activity (<c>poketokenbar://agent</c>, #457): squash while working, bubble while waiting, run to the cursor
/// when done. The run never moves the saved home. Windows-only: the run can be switched off
/// (<see cref="UsageStore.FloatingPetAgentRun"/>); the done bubble then shows at home.
/// </summary>
public sealed class FloatingPetController : IDisposable
{
    private const string OriginXKey = "floatingPetOriginX";
    private const string OriginYKey = "floatingPetOriginY";
    public const double BubbleHeadroom = 72, BubbleMinWidth = 180;
    private const double BubbleHorizontalPadding = 8;
    public const double BubbleContentWidth = BubbleMinWidth - BubbleHorizontalPadding * 2;
    /// <summary>Movement under ~4 pt between press and release is a click, not a drag.</summary>
    private const double ClickThresholdSquared = 16;

    /// <summary>How long the heart bubble stays after the last qualifying stroke.</summary>
    public static readonly TimeSpan HeartDuration = TimeSpan.FromMilliseconds(1800);
    /// <summary>How long the callout stays up on its own when a hatch / evolution / graduation / Ditto line arrives.</summary>
    public static readonly TimeSpan EventPopDuration = TimeSpan.FromSeconds(6);
    /// <summary>Praying refreshes usage every 30 prayers (30, 60, 90…), the same step as the hover quip, so the egg's
    /// progress moves without a request per click.</summary>
    public const int PrayersPerRefresh = L.PrayersPerQuip;
    /// <summary>Minimum gap between prayer-triggered refreshes. A refresh also queries the limit endpoint, which has
    /// its own rate limit.</summary>
    public static readonly TimeSpan PrayerRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>What a click on the pet does: an egg is prayed over, a hatched Pokémon opens the popover.</summary>
    public enum ClickAction { Pray, OpenPopover }

    public static ClickAction ClickActionFor(bool subjectIsEgg) => subjectIsEgg ? ClickAction.Pray : ClickAction.OpenPopover;

    /// <summary>Speech takes priority over hearts: limit alert, agent done, agent waiting, then a stroke.</summary>
    public static bool HeartsShowing(bool afterStrokeWithAlertActive, AgentActivitySummary? agent = null) =>
        !afterStrokeWithAlertActive && BubbleFor(null, agent ?? new()) == null;

    /// <summary>Reserve the bubble headroom for either speech or hearts, including agent-only speech.</summary>
    public static bool IsShowingBubble(bool alertActive, bool heartsShowing, AgentActivitySummary? agent = null) =>
        alertActive || BubbleFor(null, agent ?? new()) != null || heartsShowing;

    // MARK: Agent activity (pure)

    /// <summary>Run to the cursor / back home.</summary>
    public static readonly TimeSpan AgentRunDuration = TimeSpan.FromSeconds(0.6);
    /// <summary>Distance between the cursor and the window after a run, so the pet never sits under the pointer.</summary>
    public const double AgentRunCursorGap = 16;
    /// <summary>Half a squash cycle while an agent works. Must stay &gt; 0: it is the frame cap of this loop.</summary>
    public static readonly TimeSpan AgentBobInterval = TimeSpan.FromSeconds(0.5);

    /// <summary>Where the pet stands relative to its saved home during an agent "done" run.</summary>
    public enum RunPhase { Home, Away, Returning }

    /// <summary>A move persists the origin only at home. A user drag during a run cancels the return and becomes the
    /// new home; the run's own animation frames never overwrite it.</summary>
    public static (RunPhase Phase, bool Persist) ResolveMove(RunPhase phase, bool userDragging) =>
        userDragging ? (RunPhase.Home, true) : (phase, phase == RunPhase.Home);

    /// <summary>
    /// Window origin next to the cursor (DIPs, y down): below-right by default, flipped left/up at the work-area edges
    /// so the window does not cover the pointer, then clamped to the work area. When the window is larger than the
    /// work area, its bottom-left (the pet) stays visible.
    /// </summary>
    public static Point RunPanelOrigin(Point cursor, Size panelSize, Rect workArea, double gap = AgentRunCursorGap)
    {
        var fitsRight = cursor.X + gap + panelSize.Width <= workArea.Right;
        var fitsBelow = cursor.Y + gap + panelSize.Height <= workArea.Bottom;
        var x = fitsRight ? cursor.X + gap : cursor.X - gap - panelSize.Width;
        var y = fitsBelow ? cursor.Y + gap : cursor.Y - gap - panelSize.Height;
        return new Point(Math.Max(Math.Min(x, workArea.Right - panelSize.Width), workArea.Left),
            Math.Min(Math.Max(y, workArea.Top), workArea.Bottom - panelSize.Height));
    }

    /// <summary>Motion (squash, run) needs an animated host (no energy saver) and Windows animation effects on.</summary>
    public static bool AllowsMotion(bool animated, bool reduceMotion) => animated && !reduceMotion;

    /// <summary>A done runs to the cursor unless the run is switched off or the pet is already there for another
    /// agent's done.</summary>
    public static bool RunsToCursor(RunPhase phase, bool motionAllowed, bool panelVisible, bool runEnabled) =>
        runEnabled && phase != RunPhase.Away && motionAllowed && panelVisible;

    public static bool ShouldBob(bool working, bool motionAllowed) => motionAllowed && working;

    /// <summary>The one bubble the pet shows.</summary>
    public abstract record Bubble
    {
        public sealed record Limit(LimitAlert Alert) : Bubble;
        public sealed record AgentWaiting(string? Label, string? Message, int Others) : Bubble;
        public sealed record AgentDone(string? Label, string? Message) : Bubble;
    }

    /// <summary>A limit alert outranks agents; among agents a fresh <c>done</c> outranks waiting.</summary>
    public static Bubble? BubbleFor(LimitAlert? limitAlert, AgentActivitySummary agent)
    {
        if (limitAlert != null) return new Bubble.Limit(limitAlert);
        if (agent.Done is { } done) return new Bubble.AgentDone(done.Label, done.Message);
        if (agent.Waiting is { } waiting) return new Bubble.AgentWaiting(waiting.Label, waiting.Message, agent.OtherWaitingCount);
        return null;
    }

    public sealed record SpeechBubbleContent(string Title, string Body, bool IsCritical);

    public static SpeechBubbleContent BubbleContent(Bubble bubble, L l) => bubble switch
    {
        Bubble.Limit limit => new SpeechBubbleContent(limit.Alert.IsCritical ? l.NotifCritical : l.NotifWarning,
            limit.Alert.Body(l), limit.Alert.IsCritical),
        Bubble.AgentWaiting waiting => AgentContent(
            AgentTitle(waiting.Label, l.AgentWaitingTitle, l.AgentWaitingStatus, waiting.Others > 0 ? $"+{waiting.Others}" : null),
            waiting.Message ?? l.AgentWaitingDefaultBody),
        Bubble.AgentDone done => AgentContent(AgentTitle(done.Label, l.AgentDoneTitle, l.AgentDoneStatus, null),
            done.Message ?? l.AgentDoneDefaultBody),
        _ => throw new ArgumentOutOfRangeException(nameof(bubble), bubble, null),
    };

    private static SpeechBubbleContent AgentContent(string title, string body) =>
        new(title, FittedBubbleBody(body, title), IsCritical: false);

    /// <summary>
    /// "&lt;label&gt; · &lt;status&gt;" (or the plain title without a label), then "+N" more waiting agents. The count
    /// is digits in every language: words would take the room the label needs. The label — or the plain title — is
    /// cut with an ellipsis so the title stays on one line.
    /// </summary>
    public static string AgentTitle(string? label, string unlabeled, string status, string? count)
    {
        var lead = label ?? unlabeled;
        var suffix = label == null ? "" : " · " + status;
        if (count != null) suffix += " " + count;
        static bool Fits(string title) => MeasureSpeechBubbleLayout(title, "").UnclampedTitleWidth <= BubbleContentWidth;
        if (Fits(lead + suffix)) return lead + suffix;
        var characters = TextElements(lead);
        string Cut(int n) => string.Concat(characters.Take(n)).Trim() + "…" + suffix;
        // Invariant: `low` fits (or is the shortest cut), `high` does not.
        int low = 0, high = characters.Length;
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (Fits(Cut(mid))) low = mid; else high = mid;
        }
        // Prefer a word boundary ("needs…", not "needs y…"); folder names and CJK have none.
        var space = low > 0 ? Array.LastIndexOf(characters, " ", low - 1) : -1;
        return space >= 0 && space >= low / 2 ? Cut(space) : Cut(low);
    }

    /// <summary>Cuts <paramref name="text"/> with an ellipsis until it fits <see cref="BubbleBodyLineLimit"/>, so the
    /// view never clips it.</summary>
    public static string FittedBubbleBody(string text, string title)
    {
        if (!MeasureSpeechBubbleLayout(title, text).WouldTruncate) return text;
        var characters = TextElements(text);
        string Ellipsized(int n) => string.Concat(characters.Take(n)).Trim() + "…";
        // Invariant: `low` fits, `high` does not. Only fitting candidates are ever returned.
        int low = 0, high = characters.Length;
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (MeasureSpeechBubbleLayout(title, Ellipsized(mid)).WouldTruncate) high = mid; else low = mid;
        }
        return Ellipsized(low);
    }

    private static string[] TextElements(string text)
    {
        var elements = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) elements.Add(e.GetTextElement());
        return [.. elements];
    }

    /// <summary>Body lines the bubble shows (macOS <c>lineLimit(2)</c>).</summary>
    public const int BubbleBodyLineLimit = 2;
    public const double BubbleTitleFontSize = 11, BubbleBodyFontSize = 10;
    /// <summary>The body's height cap in the view: two 10 pt lines. WPF has no line limit, so the view clips by height
    /// and <see cref="SpeechBubbleLayout.WouldTruncate"/> checks the same height.</summary>
    public const double BubbleBodyMaxHeight = BubbleBodyLineLimit * BubbleBodyFontSize * 1.35;
    /// <summary>The tail below the card (6 pt, drawn 1 pt into it).</summary>
    private const double BubbleTailHeight = 5;

    /// <summary>Chrome size plus the signals the view actually fails on: the wrapped body height against
    /// <see cref="BubbleBodyMaxHeight"/>, and single-line width against the content column.</summary>
    public sealed record SpeechBubbleLayout(Size Size, int BodyLineCount, double UnclampedTitleWidth, double UnclampedBodyWidth,
        bool WouldTruncate);

    /// <summary>Measures the bubble as <see cref="SpeechBubble"/> lays it out — with text blocks like the view's (same
    /// typefaces, sizes, column and wrapping, which breaks a word longer than the column), without building the
    /// bubble. On the UI thread, like the view.</summary>
    public static SpeechBubbleLayout MeasureSpeechBubbleLayout(string title, string body, double contentWidth = BubbleContentWidth)
    {
        static Size Measure(string text, FontWeight weight, double size, double? wrapWidth)
        {
            var block = new TextBlock
            {
                Text = text, FontFamily = Theme.UiFont, FontSize = size, FontWeight = weight,
                TextWrapping = wrapWidth == null ? TextWrapping.NoWrap : TextWrapping.Wrap,
            };
            block.Measure(new Size(wrapWidth ?? double.PositiveInfinity, double.PositiveInfinity));
            return block.DesiredSize;
        }
        var titleWrapped = Measure(title, FontWeights.Bold, BubbleTitleFontSize, contentWidth);
        var bodyWrapped = body.Length == 0 ? new Size() : Measure(body, FontWeights.Normal, BubbleBodyFontSize, contentWidth);
        var unit = Math.Max(Measure("Ay", FontWeights.Normal, BubbleBodyFontSize, null).Height, 1);
        var textWidth = Math.Min(contentWidth, Math.Max(titleWrapped.Width, bodyWrapped.Width));
        var textHeight = Math.Ceiling(titleWrapped.Height) + 2 + Math.Ceiling(Math.Min(bodyWrapped.Height, BubbleBodyMaxHeight));
        return new SpeechBubbleLayout(
            new Size(textWidth + BubbleHorizontalPadding * 2, textHeight + 12 + BubbleTailHeight),
            body.Length == 0 ? 0 : Math.Max(1, (int)Math.Round(bodyWrapped.Height / unit)),
            Measure(title, FontWeights.Bold, BubbleTitleFontSize, null).Width,
            body.Length == 0 ? 0 : Measure(body, FontWeights.Normal, BubbleBodyFontSize, null).Width,
            bodyWrapped.Height > BubbleBodyMaxHeight + 0.01);
    }

    /// <summary>The tooltip would overlap the heart bubble, so it is cleared while hearts show.</summary>
    public static string? NativeToolTip(string text, bool heartsShowing) => heartsShowing ? null : text;

    /// <summary>Pop the callout only for an event not shown yet, while the pet is on screen and no speech or hearts
    /// bubble is up. An event that arrives when it cannot pop is dropped, not queued.</summary>
    public static bool ShouldPopEvent(int? eventSeq, int? lastSeenSeq, bool petVisible, bool bubbleShowing)
    {
        if (eventSeq is not { } seq || seq == lastSeenSeq) return false;
        return petVisible && !bubbleShowing;
    }

    /// <summary>How many refresh milestones <paramref name="count"/> prayers have passed.</summary>
    public static long PrayerRefreshMilestone(long count) => Math.Max(0, count) / PrayersPerRefresh;

    /// <summary>Refresh once a milestone is passed that was not refreshed yet AND the floor since the last refresh has
    /// passed. A milestone held back by the floor is not lost: the next click after the floor still sees it as new.</summary>
    public static bool ShouldRefreshOnPrayer(long count, long lastRefreshedMilestone, DateTimeOffset? lastRefreshAt, DateTimeOffset now)
    {
        if (PrayerRefreshMilestone(count) <= lastRefreshedMilestone) return false;
        if (lastRefreshAt is not { } last) return true;
        return now - last >= PrayerRefreshInterval;
    }

    private readonly UsageStore _store;
    private readonly CompanionStore _companion;
    private readonly IPowerStatus _power;
    private readonly AppSettings _settings;
    private readonly Action _openPopover;
    private Window? _window;
    private readonly ContentControl _bubbleSlot = new() { Focusable = false };
    private readonly ContentControl _spriteSlot = new() { Focusable = false, HorizontalAlignment = HorizontalAlignment.Center };
    private string? _spriteKey;
    /// <summary>True between press and release: a refresh must not snap the window back mid-drag.</summary>
    private bool _pressed;
    private readonly WinEventProc _onForeground;
    private IntPtr _foregroundHook;

    /// <summary>Owned here and handed to each pet view, so a prayer never rebuilds the sprite.</summary>
    private readonly PrayerBurst _prayerBurst = new();
    private readonly PetClickRouter _clickRouter;
    private readonly PettingDetector _petting = new();
    private bool _heartsShowing;
    /// <summary>One-shot expiries (restarted, never repeating): no standing timer while nothing shows.</summary>
    private readonly DispatcherTimer _heartExpiry = new() { Interval = HeartDuration };
    private readonly DispatcherTimer _eventPopExpiry = new() { Interval = EventPopDuration };
    /// <summary>The event text while the self-popped callout is up (null otherwise, so it never shows on a later hover).</summary>
    private string? _eventPopText;
    private int? _lastSeenEventSeq;
    private readonly ToolTip _callout = HoverCallout.MakeToolTip();

    private readonly AgentActivityTracker _tracker = new();
    /// <summary>One-shot at the tracker's deadline; none while idle or waiting.</summary>
    private readonly DispatcherTimer _agentTimer = new();
    private DateTimeOffset? _agentTimerDeadline;
    /// <summary>The working squash: its own element around the sprite slot, so a tick never rebuilds the pet.</summary>
    private readonly AgentSquash _squash;
    /// <summary>Whether the pet should squash (an agent works and motion is allowed); the squash runs only while shown.</summary>
    private bool _bobs;
    private RunPhase _runPhase = RunPhase.Home;
    /// <summary>Pet origin next to the cursor; read only while <see cref="_runPhase"/> is <see cref="RunPhase.Away"/>.</summary>
    private Point? _awayPetOrigin;
    /// <summary>Bumped on every run / return animation, so a stale completion does nothing.</summary>
    private int _runGeneration;
    /// <summary>Tests pin motion on or off; null follows energy saver and Windows animation effects.</summary>
    private readonly bool? _motionOverride;
    private readonly Func<DateTimeOffset> _clock;

    public FloatingPetController(UsageStore store, CompanionStore companion, IPowerStatus power, Action openPopover, AppSettings? settings = null,
        bool? motionOverride = null, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _companion = companion;
        _power = power;
        _openPopover = openPopover;
        _settings = settings ?? AppSettings.Current;
        _motionOverride = motionOverride;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _squash = new AgentSquash(AgentBobInterval) { Child = _spriteSlot, HorizontalAlignment = HorizontalAlignment.Center };
        _onForeground = (_, _, _, _, _, _, _) => KeepOnTop();
        _clickRouter = new PetClickRouter(
            pray: _companion.Pray,
            prayerCount: () => _companion.State.EggPrayers?.Count ?? 0,
            didPray: () => _callout.Content = HoverCallout.Make(CurrentHoverText()),
            spawnEmoji: () => _prayerBurst.Spawn(Random.Shared.NextDouble()),
            refreshUsage: () => _ = _store.Refresh(),
            openPopover: _openPopover,
            clock: _clock);
        _lastSeenEventSeq = companion.LatestEvent?.Seq;   // an event from before the pet existed is not news
        _heartExpiry.Tick += (_, _) =>
        {
            _heartExpiry.Stop();
            _heartsShowing = false;
            Sync();
        };
        _eventPopExpiry.Tick += (_, _) =>
        {
            _eventPopExpiry.Stop();
            _eventPopText = null;
            if (_window?.IsMouseOver != true) _callout.IsOpen = false;
            Sync();
        };
        _agentTimer.Tick += (_, _) => ExpireAgentActivity(_clock());
        _store.Changed += Sync;
        _companion.Changed += Sync;
        _power.Changed += Sync;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Sync();
    }

    /// <summary>Windows "Animation effects" (Accessibility → Visual effects) is macOS Reduce Motion.</summary>
    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Sync();
    }

    public static (double Width, double Height) PanelSize(double petSize, bool showingBubble) =>
        showingBubble ? (Math.Max(petSize, BubbleMinWidth), petSize + BubbleHeadroom) : (petSize, petSize);

    /// <summary>
    /// The hover text. <paramref name="sulkIdle"/> &gt; 0 appends how long the companion has been sulking (hours below
    /// 48 h, days from there); the event line and the egg's prayer line follow on their own lines.
    /// </summary>
    public static string HoverTooltip(long todayTokens, double? limitUtilization, LimitPace.LimitDisplayMode mode, L l,
        TimeSpan sulkIdle = default, string? eventLine = null, long prayers = 0)
    {
        var usage = TokenFormatter.Grouped(todayTokens);
        var text = l.FloatingPetHoverTokensOnly(usage);
        if (limitUtilization is { } pct)
        {
            var percent = TokenFormatter.Percent(UsageStore.DisplayPercent(pct, mode));
            text = l.FloatingPetHoverWithLimit(usage, mode == LimitPace.LimitDisplayMode.Remaining ? l.PercentRemaining(percent) : percent);
        }
        if (sulkIdle > TimeSpan.Zero)
        {
            var hours = (int)(sulkIdle.TotalSeconds / 3600);
            text += " · " + (hours < 48 ? l.FloatingPetHoverSulkHours(hours) : l.FloatingPetHoverSulkDays(hours / 24));
        }
        if (eventLine != null) text += "\n" + eventLine;
        return prayers > 0 ? text + "\n" + l.PrayerHoverLine(prayers) : text;
    }

    private string CurrentHoverText() => HoverTooltip(_store.TodayTotalTokens, _store.HighestLimitUtilization, _store.LimitDisplayMode,
        new L(_companion.Language), _companion.SulkIdle, _eventPopText,
        _companion.RepresentativeSubject.SpeciesID == null ? _companion.State.EggPrayers?.Count ?? 0 : 0);

    private void Sync()
    {
        if (!AgentEnabled && (_tracker.Agents.Count > 0 || _runPhase != RunPhase.Home)) ResetAgent();
        _bobs = ShouldBob(_tracker.Summary.Working, MotionAllowed);
        // Display off → the window and its animation timers go away (macOS setDisplayAwake); on → rebuilt.
        if (!_store.FloatingPetEnabled || !_power.DisplayOn) Hide();
        else Show();
        PopEventIfNew();
    }

    private bool AgentEnabled => _store.FloatingPetEnabled && _store.FloatingPetAgentActivity;

    private bool MotionAllowed => _motionOverride
        ?? AllowsMotion(animated: !_power.LowPower, reduceMotion: !SystemParameters.ClientAreaAnimation);

    /// <summary>Speech (limit or agent) or hearts: the window needs the bubble headroom.</summary>
    private bool ShowingBubble
    {
        get
        {
            var alert = _store.CurrentBubbleAlert != null;
            var summary = _tracker.Summary;
            return IsShowingBubble(alert, _heartsShowing && HeartsShowing(alert, summary), summary);
        }
    }

    private void Show()
    {
        var petSize = _store.FloatingPetSize;
        var alert = _store.CurrentBubbleAlert;
        var summary = _tracker.Summary;
        var subject = _companion.RepresentativeSubject;
        var l = new L(_companion.Language);
        var window = _window ??= MakeWindow();

        // Energy saver shows a still sprite (FloatingPetController.shouldAnimate(lowPower:)), and no wobble, float or pop-in.
        var animated = !_power.LowPower;
        var back = _companion.FacesBack;
        // Rebuild the sprite only when the subject, size, facing or animation changes (it owns an animation timer).
        var key = $"{subject.SpeciesID}-{subject.IsShiny}-{subject.Form?.RawValue}-{back}-{petSize}-{_store.AnimationQuality}-{animated}-{_companion.Language}";
        if (key != _spriteKey)
        {
            _spriteKey = key;
            (_spriteSlot.Content as PetView)?.Detach();
            _spriteSlot.Content = new PetView(new SpriteView(subject.SpeciesID, petSize, animated: animated, shiny: subject.IsShiny,
                form: subject.Form, back: back, minFrameDelay: TimeSpan.FromSeconds(_store.AnimationQuality.FrameFloor())),
                petSize, animated, l, _prayerBurst);
        }
        var bubble = BubbleFor(alert, summary);
        var hearts = _heartsShowing && bubble == null;
        if (_spriteSlot.Content is PetView pet) pet.HeartsShowing = hearts;
        _bubbleSlot.Content = bubble == null ? null : SpeechBubble(BubbleContent(bubble, l));
        _bubbleSlot.Visibility = bubble == null ? Visibility.Collapsed : Visibility.Visible;
        _squash.Active = _bobs;
        _petting.MinStroke = Math.Max(10, petSize * 0.15);

        // Speech and hearts take priority over the hover / event callout.
        var showingBubble = IsShowingBubble(alert != null, hearts, summary);
        var text = showingBubble ? null : NativeToolTip(CurrentHoverText(), hearts);
        if (text == null) _callout.IsOpen = false;
        else _callout.Content = HoverCallout.Make(text);
        window.ToolTip = text == null ? null : _callout;

        if (_pressed) { if (!window.IsVisible) window.Show(); return; }
        var frame = TargetFrame(petSize, showingBubble);
        window.Width = frame.Width;
        window.Height = frame.Height;
        // The pet keeps its place; a bubble grows the window upward and centered over it. During a run animation these
        // are the base values the animation ends on.
        window.Left = frame.Left;
        window.Top = frame.Top;
        if (!window.IsVisible) window.Show();
        KeepOnTop();
    }

    private void Hide()
    {
        // Nothing runs for a hidden pet: one-shot timers, particles, the squash and the callout go too.
        _heartExpiry.Stop();
        _heartsShowing = false;
        _eventPopExpiry.Stop();
        _eventPopText = null;
        _callout.IsOpen = false;
        _prayerBurst.Clear();
        _petting.Reset();
        _squash.Active = false;
        // A hidden pet comes back at home, not where it last ran to.
        _runPhase = RunPhase.Home;
        _runGeneration += 1;
        (_spriteSlot.Content as PetView)?.Detach();
        _spriteSlot.Content = null;
        if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        // Detach the reusable slots so the next window can host them.
        if (_window?.Content is Stack st) st.Children.Clear();
        _window?.Close();
        _window = null;
        _spriteKey = null;
    }

    // MARK: Agent activity

    /// <summary>The agents the pet heard from (tests read it).</summary>
    public AgentActivityTracker Tracker => _tracker;
    public RunPhase Phase => _runPhase;
    public bool IsBobbing => _bobs;
    /// <summary>Whether the squash timer runs: only while the pet is shown.</summary>
    internal bool IsSquashTicking => _squash.Active;
    /// <summary>When the pending expiry fires; null while nothing is armed.</summary>
    public DateTimeOffset? AgentExpiryFireDate => _agentTimerDeadline;

    /// <summary>Entry point for <c>poketokenbar://agent</c> URLs (on the UI thread). Dropped while the pet or the
    /// setting is off.</summary>
    public void HandleAgentEvent(AgentActivityEvent e)
    {
        if (!AgentEnabled) return;
        _tracker.Apply(e, _clock());
        AgentDidChange();
    }

    private void AgentDidChange()
    {
        _bobs = ShouldBob(_tracker.Summary.Working, MotionAllowed);
        ScheduleAgentExpiry();
        if (_tracker.Summary.Done != null)
        {
            if (_window is { IsVisible: true } w && !_pressed && RunsToCursor(_runPhase, MotionAllowed, panelVisible: true, runEnabled: _store.FloatingPetAgentRun)) RunToCursor(w);
            else Sync();
        }
        else ReturnHome();
    }

    /// <summary>One timer at the tracker's deadline — none while idle or waiting.</summary>
    private void ScheduleAgentExpiry()
    {
        _agentTimer.Stop();
        _agentTimerDeadline = null;
        if (_tracker.Deadline is not { } deadline) return;
        var delay = deadline - _clock();
        _agentTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        _agentTimerDeadline = deadline;
        _agentTimer.Start();
    }

    /// <summary>Timer callback. A fire before the deadline re-arms instead of leaving the state stuck.</summary>
    public void ExpireAgentActivity(DateTimeOffset now)
    {
        _agentTimer.Stop();   // this call is the fire; a still-early state must arm a new one
        _agentTimerDeadline = null;
        if (_tracker.Expire(now)) AgentDidChange();
        else ScheduleAgentExpiry();
    }

    private void ResetAgent()
    {
        _tracker.Reset();
        _agentTimer.Stop();
        _agentTimerDeadline = null;
        if (_window != null) StopRunAnimation(_window);
        _runPhase = RunPhase.Home;
        _runGeneration += 1;
    }

    /// <summary>Runs to the cursor's monitor. The cursor and work area come in physical pixels and are converted with
    /// the window's DPI, the scale its own coordinates use.</summary>
    private void RunToCursor(Window w)
    {
        Show();   // the done bubble appears at home first, so the pet does not jump when the window grows
        var dpi = VisualTreeHelper.GetDpi(w);
        var cursor = System.Windows.Forms.Cursor.Position;
        var work = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var frame = BeginRun(new Point(cursor.X / dpi.DpiScaleX, cursor.Y / dpi.DpiScaleY),
            new Rect(work.Left / dpi.DpiScaleX, work.Top / dpi.DpiScaleY, work.Width / dpi.DpiScaleX, work.Height / dpi.DpiScaleY));
        _callout.IsOpen = false;
        Animate(w, frame.TopLeft, () => { });
    }

    /// <summary>Leaves home for a spot next to the cursor and returns the window frame to move to.</summary>
    public Rect BeginRun(Point cursor, Rect workArea)
    {
        var petSize = _store.FloatingPetSize;
        var (width, height) = PanelSize(petSize, showingBubble: true);
        var size = new Size(width, height);
        var origin = RunPanelOrigin(cursor, size, workArea);
        _runPhase = RunPhase.Away;
        _awayPetOrigin = PetOriginOf(origin, petSize, size);
        return new Rect(origin, size);
    }

    private void ReturnHome()
    {
        if (_runPhase != RunPhase.Away || _window is not { IsVisible: true } w || _pressed)
        {
            if (_runPhase == RunPhase.Away) _runPhase = RunPhase.Home;
            Sync();
            return;
        }
        if (!MotionAllowed)
        {
            _runPhase = RunPhase.Home;
            Sync();
            return;
        }
        Show();   // the bubble leaves where the pet stands, then it walks home
        _runPhase = RunPhase.Returning;
        var frame = TargetFrame(_store.FloatingPetSize, ShowingBubble);
        Animate(w, frame.TopLeft, () =>
        {
            if (_runPhase != RunPhase.Returning) return;
            _runPhase = RunPhase.Home;
            Sync();
        });
    }

    /// <summary>Moves the window over <see cref="AgentRunDuration"/>. The base values are the target, so the window
    /// stays there when the animation ends; a newer run, a drag or a hide makes the completion a no-op.</summary>
    private void Animate(Window w, Point to, Action completion)
    {
        _runGeneration += 1;
        var generation = _runGeneration;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var left = new DoubleAnimation(w.Left, to.X, AgentRunDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var top = new DoubleAnimation(w.Top, to.Y, AgentRunDuration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        top.Completed += (_, _) =>
        {
            if (_runGeneration == generation) completion();
        };
        // Animate first, then set the base values: the window never jumps to the target before it moves.
        w.BeginAnimation(Window.LeftProperty, left);
        w.BeginAnimation(Window.TopProperty, top);
        w.Left = to.X;
        w.Top = to.Y;
    }

    /// <summary>Freezes the window where the run animation has it now.</summary>
    private static void StopRunAnimation(Window w)
    {
        if (!w.HasAnimatedProperties) return;
        var (left, top) = (w.Left, w.Top);
        w.BeginAnimation(Window.LeftProperty, null);
        w.BeginAnimation(Window.TopProperty, null);
        w.Left = left;
        w.Top = top;
    }

    /// <summary>
    /// A move of the window. Only home (or a user drag) persists the pet's origin; a drag during a run or the walk
    /// back cancels the return and becomes the new home. Windows saves on drop only, so programmatic moves never come
    /// here — the run's frames are covered by <see cref="ResolveMove"/> all the same.
    /// </summary>
    public void PanelDidMove(Point panelOrigin, bool userDragging)
    {
        var move = ResolveMove(_runPhase, userDragging);
        if (move.Phase != _runPhase)
        {
            _runPhase = move.Phase;
            _runGeneration += 1;   // the pending return is cancelled
            if (_window != null) StopRunAnimation(_window);
        }
        if (!move.Persist) return;
        var petSize = _store.FloatingPetSize;
        var (width, height) = PanelSize(petSize, ShowingBubble);
        var pet = PetOriginOf(panelOrigin, petSize, new Size(width, height));
        _settings.Set(OriginXKey, pet.X);
        _settings.Set(OriginYKey, pet.Y);
    }

    /// <summary>A plain click answers waiting / done agents, then prays over an egg or opens a hatched pet.</summary>
    public void PetClicked()
    {
        if (_tracker.DismissBubble()) AgentDidChange();
        _clickRouter.Handle(subjectIsEgg: _companion.RepresentativeSubject.SpeciesID == null);
    }

    /// <summary>The context menu's Open always opens, including when the representative is an egg.</summary>
    public void OpenFromMenu()
    {
        if (_tracker.DismissBubble()) AgentDidChange();
        _openPopover();
    }

    private void PetWasStroked()
    {
        _heartsShowing = HeartsShowing(afterStrokeWithAlertActive: _store.CurrentBubbleAlert != null, _tracker.Summary);
        if (!_heartsShowing) return;
        _callout.IsOpen = false;
        Sync();
        // One-shot expiry, restarted by every stroke; no standing timer.
        _heartExpiry.Stop();
        _heartExpiry.Start();
    }

    /// <summary>Shows the callout by itself for <see cref="EventPopDuration"/> when a new event arrived.</summary>
    private void PopEventIfNew()
    {
        if (_companion.LatestEvent is not { } e || e.Seq == _lastSeenEventSeq) return;
        var shouldPop = ShouldPopEvent(e.Seq, _lastSeenEventSeq, petVisible: _window?.IsVisible == true, bubbleShowing: ShowingBubble);
        _lastSeenEventSeq = e.Seq;   // seen either way: a missed event is not queued
        if (!shouldPop || _window == null) return;
        _eventPopText = e.Text;
        _callout.Content = HoverCallout.Make(CurrentHoverText());
        _window.ToolTip = _callout;
        _callout.PlacementTarget = _window;
        _callout.IsOpen = true;
        _eventPopExpiry.Stop();
        _eventPopExpiry.Start();
    }

    /// <summary>Window top-left for a pet square at <paramref name="petOrigin"/>: a bubble widens the window around the
    /// pet and grows it upward.</summary>
    public static Point PanelOriginOf(Point petOrigin, double petSize, Size panelSize) =>
        new(petOrigin.X - Math.Max(0, (panelSize.Width - petSize) / 2), petOrigin.Y - (panelSize.Height - petSize));

    public static Point PetOriginOf(Point panelOrigin, double petSize, Size panelSize) =>
        new(panelOrigin.X + Math.Max(0, (panelSize.Width - petSize) / 2), panelOrigin.Y + (panelSize.Height - petSize));

    /// <summary>The window frame for the pet's place: next to the cursor during a run, else home.</summary>
    private Rect TargetFrame(double petSize, bool showingBubble)
    {
        var (w, h) = PanelSize(petSize, showingBubble);
        var size = new Size(w, h);
        var pet = _runPhase == RunPhase.Away && _awayPetOrigin is { } away ? away : PetOrigin(petSize);
        return new Rect(PanelOriginOf(pet, petSize, size), size);
    }

    /// <summary>Top-left of the pet square in DIPs; default: bottom-right of the primary work area, 24 pt in.</summary>
    private Point PetOrigin(double petSize)
    {
        var work = SystemParameters.WorkArea;
        if (_settings.GetDouble(OriginXKey) is { } x && _settings.GetDouble(OriginYKey) is { } y)
        {
            var onScreen = x + petSize > SystemParameters.VirtualScreenLeft && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                && y + petSize > SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (onScreen) return new Point(x, y);
        }
        return new Point(work.Right - petSize - 24, work.Bottom - petSize - 24);
    }

    /// <summary>A pointer position in screen pixels; window-relative while the window has no HWND (tests raise mouse
    /// events on a window that was never shown).</summary>
    private static Point ScreenPoint(Window w, MouseEventArgs e) =>
        PresentationSource.FromVisual(w) == null ? e.GetPosition(w) : w.PointToScreen(e.GetPosition(w));

    internal Window MakeWindow()
    {
        var root = new Stack { Orientation = Orientation.Vertical, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        root.Children.Add(_bubbleSlot);
        root.Children.Add(_squash);
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            Content = root,
        };
        ToolTipService.SetInitialShowDelay(w, 300);
        w.SourceInitialized += (_, _) => MakeNonActivating(w);
        if (_foregroundHook == IntPtr.Zero)
            _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _onForeground, 0, 0, WinEventOutOfContext);

        Point? down = null;
        Point originAtDown = default;
        var dragged = false;
        w.MouseLeftButtonDown += (_, e) =>
        {
            _petting.Reset();
            down = ScreenPoint(w, e);
            originAtDown = new Point(w.Left, w.Top);
            dragged = false;
            _pressed = true;
            w.CaptureMouse();
        };
        w.MouseMove += (_, e) =>
        {
            var dpi = VisualTreeHelper.GetDpi(w);
            var now = ScreenPoint(w, e);
            if (down is not { } start || e.LeftButton != MouseButtonState.Pressed)
            {
                // Only moves with no button pressed count as strokes, so drags never do. Screen coordinates: the
                // window widens and re-centres when the bubble appears, which would shift window x mid-gesture.
                if (e.LeftButton != MouseButtonState.Pressed && _petting.Feed(now.X / dpi.DpiScaleX, e.Timestamp / 1000.0))
                    PetWasStroked();
                return;
            }
            _petting.Reset();
            var dx = (now.X - start.X) / dpi.DpiScaleX;
            var dy = (now.Y - start.Y) / dpi.DpiScaleY;
            if (!dragged && dx * dx + dy * dy >= ClickThresholdSquared)
            {
                dragged = true;
                // A drag during a run (or the walk back) stops it where it is and cancels the return; the drop is home.
                if (_runPhase != RunPhase.Home)
                {
                    StopRunAnimation(w);
                    originAtDown = new Point(w.Left - dx, w.Top - dy);
                    _runPhase = RunPhase.Home;
                    _runGeneration += 1;
                }
            }
            if (!dragged) return;
            w.Left = originAtDown.X + dx;
            w.Top = originAtDown.Y + dy;
        };
        w.MouseLeave += (_, _) => _petting.Reset();
        w.MouseLeftButtonUp += (_, _) =>
        {
            w.ReleaseMouseCapture();
            _pressed = false;
            if (down == null) return;
            down = null;
            if (dragged) PanelDidMove(new Point(w.Left, w.Top), userDragging: true);
            else PetClicked();
        };
        w.MouseRightButtonUp += (_, _) =>
        {
            var l = new L(_companion.Language);
            var menu = new ContextMenu();
            var open = new MenuItem { Header = l.FloatingPetMenuOpen };
            open.Click += (_, _) => OpenFromMenu();
            var hide = new MenuItem { Header = l.FloatingPetMenuHide };
            hide.Click += (_, _) => _store.FloatingPetEnabled = false;
            var backView = new MenuItem { Header = l.BackViewLabel, IsCheckable = true, IsChecked = _store.AlwaysFacesBack };
            backView.Click += (_, _) => _store.AlwaysFacesBack = !_store.AlwaysFacesBack;
            menu.Items.Add(open);
            menu.Items.Add(hide);
            menu.Items.Add(backView);
            menu.IsOpen = true;
        };
        return w;
    }

    /// <summary>Port of <c>SpeechBubbleView</c>: title (red when critical) + up to two body lines, tail pointing down.
    /// Limit alerts and agent speech share it.</summary>
    public static FrameworkElement SpeechBubble(SpeechBubbleContent content)
    {
        var title = Ui.Text(content.Title, BubbleTitleFontSize, content.IsCritical ? Theme.Keys.Red : Theme.Keys.Primary, FontWeights.Bold);
        var body = Ui.Text(content.Body, BubbleBodyFontSize, Theme.Keys.Secondary).Wrapping();
        body.MaxHeight = BubbleBodyMaxHeight;   // two lines, like lineLimit(2)
        body.TextTrimming = TextTrimming.CharacterEllipsis;
        var card = new Border
        {
            Child = Ui.VStack(2, title, body),
            Width = BubbleContentWidth + BubbleHorizontalPadding * 2,
            Padding = new Thickness(BubbleHorizontalPadding, 6, BubbleHorizontalPadding, 6),
            CornerRadius = new CornerRadius(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Opacity = 0.15, BlurRadius = 8, ShadowDepth = 2, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Background);
        var tail = new Polygon { Points = [new(10, 0), new(20, 0), new(15, 6)], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -1, 0, 0) };
        tail.SetResourceReference(Shape.FillProperty, Theme.Keys.Background);
        var bubble = Ui.VStack(0, card, tail);
        bubble.HorizontalAlignment = HorizontalAlignment.Center;
        return bubble;
    }

    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080, WsExTopmost = 0x00000008;
    private const uint GwHwndPrev = 3, EventSystemForeground = 0x0003, WinEventOutOfContext = 0;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    /// <summary>
    /// Windows can drop the pet into the normal z-order band when another app takes the foreground,
    /// leaving WS_EX_TOPMOST set but the window under ordinary windows (an NSPanel's floating level
    /// never does this). Checked on every foreground change and refresh. It re-raises only when a
    /// non-topmost window sits above the pet, so the pet never jumps over the popover or other
    /// topmost windows.
    /// </summary>
    private void KeepOnTop()
    {
        if (_window == null) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        // Topmost windows form one contiguous band at the top, so any normal window above means demoted.
        for (var h = GetWindow(hwnd, GwHwndPrev); h != IntPtr.Zero; h = GetWindow(h, GwHwndPrev))
        {
            if ((GetWindowLong(h, GwlExStyle) & WsExTopmost) != 0) continue;
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            return;
        }
    }

    /// <summary>Like a non-activating NSPanel: clicks don't steal focus, and it stays out of Alt+Tab.</summary>
    private static void MakeNonActivating(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow);
    }

    public void Dispose()
    {
        _store.Changed -= Sync;
        _companion.Changed -= Sync;
        _power.Changed -= Sync;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        _agentTimer.Stop();
        Hide();
    }
}

/// <summary>
/// The working squash (port of <c>AgentBob</c>): a two-frame squash toward the feet, toggled every
/// <see cref="Interval"/>, with no interpolation, so the loop costs two redraws a second. A hop would clip — the window
/// is sprite-tall. It wraps the sprite slot, so a tick changes one transform and never rebuilds the pet (sprite, hearts
/// and particles move with it). Inactive, the timer is stopped and the pet stands still.
/// </summary>
public sealed class AgentSquash : Decorator
{
    private readonly DispatcherTimer _timer;
    private readonly ScaleTransform _scale = new();

    public AgentSquash(TimeSpan interval)
    {
        // The interval is the loop's frame cap: zero would spin.
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => Squashed = !Squashed;
        RenderTransformOrigin = new Point(0.5, 1);
        RenderTransform = _scale;
    }

    public TimeSpan Interval => _timer.Interval;

    /// <summary>Whether the loop runs (an agent works, motion is allowed and the pet is shown). Off, no timer runs.</summary>
    public bool Active
    {
        get => _timer.IsEnabled;
        set
        {
            if (value == _timer.IsEnabled) return;
            Squashed = false;
            if (value) _timer.Start(); else _timer.Stop();
        }
    }

    /// <summary>The squashed frame: 3 % wider, 6 % shorter, anchored at the feet.</summary>
    public bool Squashed
    {
        get => _scale.ScaleY < 1;
        set
        {
            _scale.ScaleX = value ? 1.03 : 1;
            _scale.ScaleY = value ? 0.94 : 1;
        }
    }
}

/// <summary>
/// Routes a plain click on the pet (port of <c>PetClickRouter</c>). Praying always counts and spawns its emoji at
/// once; only the usage refresh it triggers is throttled (at growing prayer milestones, at most once per interval).
/// </summary>
public sealed class PetClickRouter(Action pray, Func<long> prayerCount, Action didPray, Action spawnEmoji,
    Action refreshUsage, Action openPopover, Func<DateTimeOffset> clock)
{
    private DateTimeOffset? _lastPrayerRefreshAt;
    /// <summary>In memory only: after a restart the first click past a milestone may refresh once more, which is harmless.</summary>
    private long _lastRefreshedMilestone;

    public void Handle(bool subjectIsEgg)
    {
        switch (FloatingPetController.ClickActionFor(subjectIsEgg))
        {
            case FloatingPetController.ClickAction.Pray:
                pray();
                spawnEmoji();
                didPray();
                var count = prayerCount();
                var milestone = FloatingPetController.PrayerRefreshMilestone(count);
                if (milestone < _lastRefreshedMilestone) _lastRefreshedMilestone = milestone;   // a new egg started over
                var now = clock();
                if (FloatingPetController.ShouldRefreshOnPrayer(count, _lastRefreshedMilestone, _lastPrayerRefreshAt, now))
                {
                    _lastRefreshedMilestone = milestone;
                    _lastPrayerRefreshAt = now;
                    refreshUsage();
                }
                break;
            case FloatingPetController.ClickAction.OpenPopover:
                openPopover();
                break;
        }
    }
}

/// <summary>
/// The 🙏 particles floating up from the egg (port of <c>PrayerBurst</c>). Each one removes itself after
/// <see cref="Lifetime"/>; no standing timer. Pure presentation state: it never touches an RNG that feeds a hatch roll.
/// </summary>
public sealed class PrayerBurst
{
    public sealed record Particle(double XFraction)
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    /// <summary>A little longer than the 1 s float-and-fade, so a particle is gone only once it is invisible.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(1100);

    private readonly List<Particle> _particles = [];
    public IReadOnlyList<Particle> Particles => _particles;
    /// <summary>Bumped on every prayer; the egg wobbles once per change.</summary>
    public int WobbleCount { get; private set; }
    /// <summary>Raised on spawn, removal and clear.</summary>
    public event Action? Changed;

    /// <param name="xFraction">0…1 across the usable width of the pet (the emoji's own width is already excluded).</param>
    public void Spawn(double xFraction)
    {
        var particle = new Particle(Math.Clamp(xFraction, 0, 1));
        _particles.Add(particle);
        WobbleCount += 1;
        Changed?.Invoke();
        _ = RemoveLater(particle);
    }

    /// <summary>Drops every particle (the pet was hidden).</summary>
    public void Clear()
    {
        if (_particles.Count == 0) return;
        _particles.Clear();
        Changed?.Invoke();
    }

    private async Task RemoveLater(Particle particle)
    {
        await Task.Delay(Lifetime);   // resumes on the UI thread when spawned there
        bool removed;
        lock (_particles) removed = _particles.Remove(particle);   // off the UI thread (no context) removals may overlap
        if (removed) Changed?.Invoke();
    }

    /// <summary>Emoji edge for a pet size (clamped so it stays legible at 48 pt and modest at 384 pt).</summary>
    public static double EmojiSize(double petSize) => Math.Min(Math.Max(petSize * 0.25, 14), 32);

    /// <summary>Centre of a particle at <paramref name="progress"/> (0 = just spawned, 1 = gone): x keeps the glyph fully
    /// inside the pet; it rises from the lower middle to the top edge.</summary>
    public static Point Position(double xFraction, double petSize, double progress)
    {
        var emoji = EmojiSize(petSize);
        var startY = petSize * 0.7;
        var endY = emoji / 2;
        return new Point(emoji / 2 + xFraction * (petSize - emoji), startY + (endY - startY) * progress);
    }
}

/// <summary>
/// The pet square: the sprite, the 🙏 particles drawn inside its own frame (the window never grows for them), the
/// one-shot wobble per prayer, and the heart bubble anchored to the sprite's visible top. Without
/// <c>animated</c> (energy saver) nothing moves: particles stand still until they expire, no wobble, no pop-in.
/// </summary>
public sealed class PetView : Grid
{
    private readonly SpriteView _sprite;
    private readonly double _size;
    private readonly bool _animated;
    private readonly PrayerBurst? _burst;
    private readonly Canvas _particles;
    private readonly Canvas _heartLayer = new() { IsHitTestVisible = false };
    private readonly FrameworkElement _heart;
    private readonly RotateTransform _wobble = new();
    private readonly Dictionary<Guid, FrameworkElement> _shown = [];
    private int _wobbleSeen;

    public PetView(SpriteView sprite, double size, bool animated, L l, PrayerBurst? burst = null)
    {
        _sprite = sprite;
        _size = size;
        _animated = animated;
        _burst = burst;
        Width = size;
        Height = size;
        _sprite.RenderTransformOrigin = new Point(0.5, 0.5);
        _sprite.RenderTransform = _wobble;
        _particles = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        _heart = HeartBubble.Make(l);
        _heart.Visibility = Visibility.Collapsed;
        _heartLayer.Children.Add(_heart);
        Children.Add(_sprite);
        Children.Add(_particles);
        Children.Add(_heartLayer);
        _sprite.VisibleTopChanged += PlaceHeart;
        PlaceHeart();
        if (burst != null)
        {
            _wobbleSeen = burst.WobbleCount;
            // Subscribed from the start (a prayer may land before the first layout); the controller detaches it.
            burst.Changed += SyncParticles;
        }
    }

    /// <summary>Stops following the burst (the view was replaced or the pet hidden).</summary>
    public void Detach()
    {
        if (_burst != null) _burst.Changed -= SyncParticles;
    }

    /// <summary>The heart bubble after a stroke. Insertion pops in; removal is instant, because the window shrinks at
    /// the same moment and a fading bubble would be drawn lower, over the pet.</summary>
    public bool HeartsShowing
    {
        get => _heart.Visibility == Visibility.Visible;
        set
        {
            if (value == HeartsShowing) return;
            _heart.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value || !_animated) return;
            var scale = new ScaleTransform(0.8, 0.8);
            _heart.RenderTransformOrigin = new Point(0.5, 1);
            _heart.RenderTransform = scale;
            var spring = new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = new BackEase { Amplitude = 0.4 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
            _heart.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        }
    }

    /// <summary>A particle frozen mid-flight (render harness: the live ones use a random x).</summary>
    public void AddStillPrayer(double xFraction, double progress) => _particles.Children.Add(Particle(xFraction, progress));

    /// <summary>Hearts hug the visible top of the sprite (not its box), the tail tip just inside it.</summary>
    private void PlaceHeart()
    {
        Canvas.SetLeft(_heart, (_size - HeartBubbleArt.Width) / 2);
        Canvas.SetTop(_heart, SpriteVisibleTop.BubbleTopOffset(_sprite.VisibleTop ?? 0, HeartBubbleArt.Height, HeartBubbleArt.TailOverlap));
    }

    private FrameworkElement Particle(double xFraction, double progress)
    {
        var emoji = PrayerBurst.EmojiSize(_size);
        var view = EmojiImages.View("🙏", emoji);
        var at = PrayerBurst.Position(xFraction, _size, progress);
        Canvas.SetLeft(view, at.X - emoji / 2);
        Canvas.SetTop(view, at.Y - emoji / 2);
        view.Opacity = 1 - progress;
        return view;
    }

    private void SyncParticles()
    {
        if (_burst == null) return;
        var live = _burst.Particles.Select(p => p.Id).ToHashSet();
        foreach (var gone in _shown.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _particles.Children.Remove(_shown[gone]);
            _shown.Remove(gone);
        }
        foreach (var p in _burst.Particles.Where(p => !_shown.ContainsKey(p.Id)))
        {
            // Energy saver: shown still at mid-flight until it expires.
            var view = Particle(p.XFraction, _animated ? 0 : 0.5);
            _shown[p.Id] = view;
            _particles.Children.Add(view);
            if (!_animated) continue;
            var emoji = PrayerBurst.EmojiSize(_size);
            var end = PrayerBurst.Position(p.XFraction, _size, 1);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            view.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(end.Y - emoji / 2, TimeSpan.FromSeconds(1)) { EasingFunction = ease });
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(1)) { EasingFunction = ease });
        }
        if (_burst.WobbleCount != _wobbleSeen)
        {
            _wobbleSeen = _burst.WobbleCount;
            // One-shot wobble per prayer; it runs once per change, never in a loop.
            if (_animated)
                _wobble.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimationUsingKeyFrames
                {
                    KeyFrames =
                    {
                        new LinearDoubleKeyFrame(-9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.06))),
                        new LinearDoubleKeyFrame(9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.16))),
                        new LinearDoubleKeyFrame(-5, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.24))),
                        new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.30))),
                    },
                    FillBehavior = FillBehavior.Stop,
                });
        }
    }
}

/// <summary>
/// Detects a back-and-forth stroking motion from horizontal mouse samples (port of <c>PettingDetector</c>). Pure, so
/// it is unit-testable without mouse events.
/// </summary>
public sealed class PettingDetector
{
    /// <summary>Direction reversals needed within <see cref="Window"/> to count as stroking.</summary>
    public const int RequiredReversals = 3;
    public const double Window = 1.2;

    /// <summary>Minimum horizontal travel (DIPs) of one stroke; scales with the pet size.</summary>
    public double MinStroke { get; set; } = 14;

    private double _direction;   // 0 = not moving far enough yet, else -1 / +1
    private double _extremeX;    // furthest x reached in the current direction
    private double? _lastTimestamp;
    private readonly List<double> _reversalTimes = [];

    public void Reset()
    {
        _direction = 0;
        _extremeX = 0;
        _lastTimestamp = null;
        _reversalTimes.Clear();
    }

    /// <summary>
    /// Feeds one mouse sample (x in DIPs, time in seconds). True when this sample completes a reversal and the recent
    /// wiggle has at least <see cref="RequiredReversals"/> of them. Every stroke must travel <see cref="MinStroke"/>,
    /// so jitter and slow drifting never reverse.
    /// </summary>
    public bool Feed(double x, double time)
    {
        if (_lastTimestamp is not { } last || time - last > Window)
        {
            Reset();
            _lastTimestamp = time;
            _extremeX = x;
            return false;
        }
        _lastTimestamp = time;
        _reversalTimes.RemoveAll(t => time - t > Window);

        if (_direction == 0)
        {
            if (Math.Abs(x - _extremeX) < MinStroke) return false;
            _direction = x > _extremeX ? 1 : -1;
            _extremeX = x;
            return false;
        }
        if ((x - _extremeX) * _direction > 0)   // still travelling the same way
        {
            _extremeX = x;
            return false;
        }
        if ((_extremeX - x) * _direction < MinStroke) return false;
        _direction = -_direction;
        _extremeX = x;
        _reversalTimes.Add(time);
        return _reversalTimes.Count >= RequiredReversals;
    }
}

/// <summary>
/// Pixel-art layout of the heart bubble, so it matches the Gen-V pixel sprites. One character per pixel: <c>.</c>
/// empty, <c>O</c> outline, <c>W</c> fill, <c>R</c> heart, <c>D</c> heart shade, <c>H</c> heart highlight. A
/// 2-step rounded bubble with its tail at the bottom centre.
/// </summary>
public static class HeartBubbleArt
{
    /// <summary>Pixel unit in DIPs; an integer so edges stay crisp.</summary>
    public const double Pixel = 2;
    public static readonly string[] Rows =
    [
        "..OOOOOOOOOOOOO..",
        ".OWWWWWWWWWWWWWO.",
        "OWWWWWWWWWWWWWWWO",
        "OWWWWRRRWRRRWWWWO",
        "OWWWRHHRRRRRRWWWO",
        "OWWWRHRRRRRRDWWWO",
        "OWWWRRRRRRRRDWWWO",
        "OWWWWRRRRRRDWWWWO",
        "OWWWWWRRRRDWWWWWO",
        "OWWWWWWRRDWWWWWWO",
        "OWWWWWWWDWWWWWWWO",
        "OWWWWWWWWWWWWWWWO",
        ".OWWWWWWWWWWWWWO.",
        "..OOOOOWWWOOOOO..",
        ".......OWO.......",
        "........O........",
    ];

    public static double Width => Rows[0].Length * Pixel;
    public static double Height => Rows.Length * Pixel;
    /// <summary>How far the tail tip sticks into the sprite's visible top (DIPs).</summary>
    public const double TailOverlap = 3;

    public static Color? ColorFor(char cell) => cell switch
    {
        'O' => Color.FromRgb(0x20, 0x20, 0x20),
        'W' => Colors.White,
        'R' => Color.FromRgb(0xE0, 0x30, 0x3C),
        'D' => Color.FromRgb(0xA8, 0x20, 0x2C),
        'H' => Color.FromRgb(0xFF, 0x9A, 0xA2),
        _ => null,
    };
}

/// <summary>Port of <c>HeartBubbleView</c>: the pixel-art heart bubble on an integer grid, the same colors in light and
/// dark mode (game style).</summary>
public static class HeartBubble
{
    public static FrameworkElement Make(L l)
    {
        var group = new DrawingGroup();
        for (var y = 0; y < HeartBubbleArt.Rows.Length; y++)
            for (var x = 0; x < HeartBubbleArt.Rows[y].Length; x++)
                if (HeartBubbleArt.ColorFor(HeartBubbleArt.Rows[y][x]) is { } color)
                    group.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null,
                        new RectangleGeometry(new Rect(x * HeartBubbleArt.Pixel, y * HeartBubbleArt.Pixel, HeartBubbleArt.Pixel, HeartBubbleArt.Pixel))));
        group.Freeze();
        var image = new Image
        {
            Source = new DrawingImage(group), Width = HeartBubbleArt.Width, Height = HeartBubbleArt.Height,
            SnapsToDevicePixels = true, IsHitTestVisible = false,
        };
        RenderOptions.SetEdgeMode(image, EdgeMode.Aliased);
        System.Windows.Automation.AutomationProperties.SetName(image, l.FloatingPetHeartsLabel);
        return image;
    }
}

/// <summary>
/// The pet's hover / event callout (macOS draws it in its own panel above the pet: window background, separator
/// border, 8 pt corners and padding, 11 pt text). On Windows it is the window's tooltip, opened by hover or, for an
/// event line, by the controller itself.
/// </summary>
public static class HoverCallout
{
    public static FrameworkElement Make(string text)
    {
        var card = new Border
        {
            Child = Ui.Text(text, 11),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, Theme.Keys.Background);
        card.SetResourceReference(Border.BorderBrushProperty, Theme.Keys.Separator);
        return card;
    }

    /// <summary>A chrome-less tooltip centred 6 pt above the pet.</summary>
    public static ToolTip MakeToolTip()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        var tip = new ToolTip
        {
            Template = new ControlTemplate(typeof(ToolTip)) { VisualTree = presenter },
            HasDropShadow = false,
            Placement = PlacementMode.Custom,
        };
        tip.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, -popup.Height - 6), PopupPrimaryAxis.Horizontal)];
        return tip;
    }
}
