using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PokeTokenBar.Core;

public enum AgentActivityKind { Idle, Working, Waiting, Done }

/// <summary>
/// Port of <c>AgentActivity</c> (AgentActivity.swift): what an external coding agent last reported through
/// <c>poketokenbar://agent?state=…</c>. Agent-agnostic: any tool that can open a URL drives the same states.
/// <see cref="Message"/> is kept for <see cref="AgentActivityKind.Waiting"/> and <see cref="AgentActivityKind.Done"/> only.
/// </summary>
public readonly record struct AgentActivity(AgentActivityKind Kind, string? Message = null)
{
    public static AgentActivity Idle => new(AgentActivityKind.Idle);
    public static AgentActivity Working => new(AgentActivityKind.Working);
    public static AgentActivity Waiting(string? message) => new(AgentActivityKind.Waiting, message);
    public static AgentActivity Done(string? message) => new(AgentActivityKind.Done, message);
}

/// <summary>One parsed <c>poketokenbar://agent?state=&lt;working|waiting|done|idle&gt;[&amp;message=&lt;text&gt;][&amp;label=&lt;name&gt;]</c> URL.</summary>
/// <param name="Label">Null is the one unlabeled agent, so URLs without a label behave as before.</param>
public sealed record AgentActivityEvent(AgentActivity Activity, string? Label)
{
    public const string Scheme = "poketokenbar";
    public const string Host = "agent";
    /// <summary>Any app or web page can open the URL, so the message size is bounded here. These are size limits on
    /// untrusted input, not layout: the floating pet fits the text to its bubble.</summary>
    public const int MaxMessageLength = 80;
    /// <summary>Characters can stack any number of combining marks, so scalars are bounded too — otherwise 80
    /// characters could carry thousands of scalars into text measuring.</summary>
    public const int MaxMessageScalars = MaxMessageLength * 4;
    /// <summary>Names one agent among several (e.g. a project folder). Short: it shares the title line.</summary>
    public const int MaxLabelLength = 24;

    private static readonly Regex UrlShape = new(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*):(?<rest>[^#]*)", RegexOptions.Singleline);

    /// <summary>Null for any other scheme, host or state — unknown input is ignored, never an error.</summary>
    public static AgentActivityEvent? Parse(string? url)
    {
        if (url == null || UrlShape.Match(url) is not { Success: true } m) return null;
        if (!m.Groups["scheme"].Value.Equals(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = m.Groups["rest"].Value;
        if (!rest.StartsWith("//", StringComparison.Ordinal)) return null;   // poketokenbar:agent has no host
        var authorityEnd = rest.IndexOfAny(['/', '?'], 2);
        var authority = authorityEnd < 0 ? rest[2..] : rest[2..authorityEnd];
        if (!authority.Equals(Host, StringComparison.OrdinalIgnoreCase)) return null;
        var queryStart = rest.IndexOf('?');
        if (queryStart < 0) return null;
        var items = rest[(queryStart + 1)..].Split('&').Select(item =>
        {
            var eq = item.IndexOf('=');
            return eq < 0 ? (Name: Decode(item), Value: (string?)null) : (Name: Decode(item[..eq]), Value: Decode(item[(eq + 1)..]));
        }).ToList();
        string? First(string name) => items.FirstOrDefault(i => i.Name == name).Value;
        if (First("state")?.ToLowerInvariant() is not { } state) return null;   // the first state item wins
        var message = SanitizedMessage(First("message"));
        var label = SanitizedLabel(First("label"));
        AgentActivity? activity = state switch
        {
            "idle" => AgentActivity.Idle,
            "working" => AgentActivity.Working,
            "waiting" => AgentActivity.Waiting(message),
            "done" => AgentActivity.Done(message),
            _ => null,
        };
        return activity is { } a ? new AgentActivityEvent(a, label) : null;
    }

    /// <summary>Percent-decoding like <c>URLComponents.queryItems</c>: <c>+</c> stays a plus sign.</summary>
    private static string Decode(string s) => Uri.UnescapeDataString(s);

    public static string? SanitizedMessage(string? raw) => Sanitized(raw, MaxMessageLength);
    public static string? SanitizedLabel(string? raw) => Sanitized(raw, MaxLabelLength);

    /// <summary>
    /// Plain single-line text: control characters become spaces, invisible format characters (bidi overrides,
    /// zero-width spaces) are dropped, whitespace runs collapse, and anything past 4× <paramref name="maxLength"/>
    /// scalars or <paramref name="maxLength"/> characters (grapheme clusters) is cut with an ellipsis. Empty input → null.
    /// </summary>
    private static string? Sanitized(string? raw, int maxLength)
    {
        if (raw == null) return null;
        var maxScalars = maxLength * 4;
        var kept = new StringBuilder(raw.Length);
        foreach (var rune in raw.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control: kept.Append(' '); break;
                // U+200D joins emoji sequences; dropping it would split them apart.
                case UnicodeCategory.Format when rune.Value != 0x200D: break;
                default: kept.Append(rune.ToString()); break;
            }
        }
        var text = string.Join(' ', kept.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return null;
        var runes = text.EnumerateRunes().ToList();
        if (runes.Count > maxScalars)
            text = string.Concat(runes.Take(maxScalars - 1).Select(r => r.ToString())).Trim() + "…";
        var characters = new StringInfo(text);
        if (characters.LengthInTextElements <= maxLength) return text;
        return characters.SubstringByTextElements(0, maxLength - 1).Trim() + "…";
    }
}

/// <summary>What the pet shows across every agent it heard from (port of <c>AgentActivitySummary</c>).</summary>
public sealed record AgentActivitySummary
{
    public sealed record Note(string? Label, string? Message);

    /// <summary>The latest <c>done</c>: a transient overlay over everything else.</summary>
    public Note? Done { get; init; }
    /// <summary>The most recently waiting agent; <see cref="OtherWaitingCount"/> more are waiting behind it.</summary>
    public Note? Waiting { get; init; }
    public int OtherWaitingCount { get; init; }
    public bool Working { get; init; }
}

/// <summary>
/// Port of <c>AgentActivityTracker</c>: the pure state machine behind the floating pet's agent reactions, one entry per
/// label. Time comes in as arguments, so tests drive expirations without sleeping; the caller arms one timer at
/// <see cref="Deadline"/>.
/// </summary>
public sealed class AgentActivityTracker
{
    /// <summary>An agent can crash or be killed without ever sending <c>done</c>.</summary>
    public static readonly TimeSpan WorkingTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DoneDuration = TimeSpan.FromSeconds(6);
    /// <summary>Labels come from untrusted input; past this many the oldest agent is forgotten.</summary>
    public const int MaxAgents = 8;

    /// <param name="Activity">Never idle: an idle agent is removed.</param>
    /// <param name="Sequence">Event order; larger is more recent.</param>
    public readonly record struct Entry(AgentActivity Activity, DateTimeOffset? Deadline, int Sequence);

    /// <summary>Keyed by sanitized label; <c>""</c> is the unlabeled agent.</summary>
    private readonly Dictionary<string, Entry> _agents = [];
    private int _sequence;

    public IReadOnlyDictionary<string, Entry> Agents => _agents;

    /// <summary>The earliest moment any agent falls back to idle on its own.</summary>
    public DateTimeOffset? Deadline => _agents.Values.Select(e => e.Deadline).Min();

    public AgentActivitySummary Summary
    {
        get
        {
            static AgentActivitySummary.Note Note(string key, string? message) => new(key.Length == 0 ? null : key, message);
            var working = _agents.Values.Any(e => e.Activity.Kind == AgentActivityKind.Working);
            var waiting = _agents.Where(a => a.Value.Activity.Kind == AgentActivityKind.Waiting).ToList();
            var done = _agents.Where(a => a.Value.Activity.Kind == AgentActivityKind.Done).ToList();
            var latestDone = done.Count == 0 ? (KeyValuePair<string, Entry>?)null : done.MaxBy(a => a.Value.Sequence);
            var latestWaiting = waiting.Count == 0 ? (KeyValuePair<string, Entry>?)null : waiting.MaxBy(a => a.Value.Sequence);
            return new AgentActivitySummary
            {
                Done = latestDone is { } d ? Note(d.Key, d.Value.Activity.Message) : null,
                Waiting = latestWaiting is { } w ? Note(w.Key, w.Value.Activity.Message) : null,
                OtherWaitingCount = Math.Max(0, waiting.Count - 1),
                Working = working,
            };
        }
    }

    /// <summary>The one activity the pet shows first: done, then waiting, then working.</summary>
    public AgentActivity Activity
    {
        get
        {
            var summary = Summary;
            if (summary.Done is { } done) return AgentActivity.Done(done.Message);
            if (summary.Waiting is { } waiting) return AgentActivity.Waiting(waiting.Message);
            return summary.Working ? AgentActivity.Working : AgentActivity.Idle;
        }
    }

    /// <summary>An event replaces only its own label's state.</summary>
    public void Apply(AgentActivityEvent e, DateTimeOffset now)
    {
        var key = e.Label ?? "";
        Expire(now);
        DateTimeOffset? deadline;
        switch (e.Activity.Kind)
        {
            case AgentActivityKind.Idle:
                _agents.Remove(key);
                return;
            case AgentActivityKind.Working: deadline = now + WorkingTimeout; break;
            case AgentActivityKind.Done: deadline = now + DoneDuration; break;
            default: deadline = null; break;
        }
        if (!_agents.ContainsKey(key) && _agents.Count >= MaxAgents)
            _agents.Remove(_agents.MinBy(a => a.Value.Sequence).Key);
        _sequence += 1;
        _agents[key] = new Entry(e.Activity, deadline, _sequence);
    }

    /// <summary>Returns whether any agent changed.</summary>
    public bool Expire(DateTimeOffset now)
    {
        var expired = _agents.Where(a => a.Value.Deadline is { } d && now >= d).Select(a => a.Key).ToList();
        foreach (var key in expired) _agents.Remove(key);
        return expired.Count > 0;
    }

    /// <summary>A click on the pet answers every question and acknowledges results; it does not stop work.</summary>
    public bool DismissBubble()
    {
        var answered = _agents.Where(a => a.Value.Activity.Kind != AgentActivityKind.Working).Select(a => a.Key).ToList();
        foreach (var key in answered) _agents.Remove(key);
        return answered.Count > 0;
    }

    public void Reset() => _agents.Clear();
}
