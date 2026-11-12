using System.Globalization;
using System.Text.Json.Nodes;

namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>UsageLedger</c>: day-by-day token totals kept across months, so a recap survives the 1st.
/// Providers only report the current month, so on the 3rd a seven-day window would lose four days and a
/// year would be out of reach. A local cache of numbers the app already derived; it rebuilds itself from
/// the next refresh. Persisted in <see cref="AppSettings"/> under the macOS keys.
/// </summary>
public sealed class UsageLedger : IEquatable<UsageLedger>
{
    public const string DefaultsKey = "dailyTokenLedger";
    public const string CoverageKey = "dailyTokenLedgerSince";

    private Dictionary<string, long> _tokensByDay = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, long> TokensByDay => _tokensByDay;
    /// <summary>The first day the ledger vouches for. From there on a missing row means "no usage";
    /// before it the app was not recording, which a recap must not present as a quiet stretch.</summary>
    public string? CoveredSince { get; private set; }

    public long Tokens(string day) => _tokensByDay.GetValueOrDefault(day);

    public bool Covers(string day) => CoveredSince != null && string.CompareOrdinal(day, CoveredSince) >= 0;

    /// <summary>
    /// Records a refresh. A day never goes down: a rotated or pruned log makes the provider report less
    /// than it did yesterday. Today keeps climbing all day, so the larger value is also the live total.
    /// </summary>
    public void Merge(IEnumerable<DailyUsage> series)
    {
        string? first = null;
        foreach (var day in series)
        {
            if (first == null || string.CompareOrdinal(day.Date, first) < 0) first = day.Date;
            if (day.TotalTokens > 0) _tokensByDay[day.Date] = Math.Max(_tokensByDay.GetValueOrDefault(day.Date), day.TotalTokens);
        }
        // The series lists every day since the 1st, empty ones included, so the whole stretch was
        // scanned: that is what makes a zero a real zero.
        if (first != null && (CoveredSince == null || string.CompareOrdinal(first, CoveredSince) < 0)) CoveredSince = first;
    }

    /// <summary>Drops what no recap can reach any more. <c>yyyy-MM-dd</c> keys sort chronologically.</summary>
    public void Prune(string cutoff)
    {
        _tokensByDay = _tokensByDay.Where(p => string.CompareOrdinal(p.Key, cutoff) >= 0)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        // The dropped days are unknown again, not empty.
        if (CoveredSince != null && string.CompareOrdinal(CoveredSince, cutoff) < 0) CoveredSince = cutoff;
    }

    public static UsageLedger Load(AppSettings settings)
    {
        var ledger = new UsageLedger();
        // Hand-edited settings are data, not truth: keep what parses as a day and drop the rest. A junk
        // key that sorted below every real day would otherwise backdate the coverage.
        if (settings.GetNode(DefaultsKey) is JsonObject stored)
        {
            foreach (var (key, value) in stored)
            {
                // Read through the JSON text: an in-memory value can be an int, a long or a JsonElement.
                if (IsDay(key) && value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
                    && long.TryParse(v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) && tokens > 0)
                    ledger._tokensByDay[key] = tokens;
            }
        }
        if (settings.GetString(CoverageKey) is { } since && IsDay(since)) ledger.CoveredSince = since;
        return ledger;
    }

    public void Save(AppSettings settings)
    {
        var obj = new JsonObject();
        foreach (var (day, tokens) in _tokensByDay.OrderBy(p => p.Key, StringComparer.Ordinal)) obj[day] = tokens;
        settings.SetNode(DefaultsKey, obj);
        settings.Set(CoverageKey, CoveredSince);
    }

    private static bool IsDay(string key) =>
        DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public bool Equals(UsageLedger? other) =>
        other != null && CoveredSince == other.CoveredSince && _tokensByDay.Count == other._tokensByDay.Count
        && _tokensByDay.All(p => other._tokensByDay.TryGetValue(p.Key, out var v) && v == p.Value);

    public override bool Equals(object? obj) => Equals(obj as UsageLedger);
    public override int GetHashCode() => HashCode.Combine(CoveredSince, _tokensByDay.Count);

    /// <summary>
    /// The current run of active days. "Active" is any usage at all — the rule the recap's best
    /// streak uses — so the two never disagree about a day.
    /// <para>
    /// A today without usage does not end the run; only a whole day without usage does. Otherwise
    /// the run would read as broken every morning until the first prompt.
    /// </para>
    /// Days step through local calendar dates, never a fixed 86400 seconds: a DST day is 23 or 25 hours
    /// long. The ledger reaches back past the 1st, so the run does not reset when a month starts.
    /// </summary>
    public UsageStreak CurrentStreak(DateTimeOffset now, RecapCalendar calendar)
    {
        bool IsActive(DateTime day) => Tokens(RecapCalendar.DayKey(day)) > 0;

        var today = calendar.LocalDate(now);
        var includesToday = IsActive(today);
        var cursor = includesToday ? today : today.AddDays(-1);
        var days = 0;
        while (IsActive(cursor))
        {
            days++;
            cursor = cursor.AddDays(-1);
        }
        return new UsageStreak(days, includesToday);
    }
}

/// <summary>Port of <c>UsageStreak</c>: the run of consecutive days with usage that is still going.</summary>
/// <param name="IncludesToday">Today is part of the run. False while today has no usage yet: the run ends
/// yesterday and today's first use extends it.</param>
public sealed record UsageStreak(int Days, bool IncludesToday)
{
    public static readonly UsageStreak None = new(0, false);
}

/// <summary>
/// The Foundation <c>Calendar</c> a recap uses: Gregorian whatever the system calendar (a year runs
/// January to December and day keys match <see cref="LocalUsageReader.LocalDay"/>), in a time zone,
/// with the user's first weekday.
/// </summary>
public sealed record RecapCalendar(TimeZoneInfo TimeZone, DayOfWeek FirstDayOfWeek)
{
    public static RecapCalendar Current(TimeZoneInfo? timeZone = null) =>
        new(timeZone ?? TimeZoneInfo.Local, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);

    /// <summary>The local calendar date of an instant.</summary>
    public DateTime LocalDate(DateTimeOffset date) => TimeZoneInfo.ConvertTime(date, TimeZone).Date;

    /// <summary>Midnight of a local date; if a DST jump skips midnight, the first valid instant.</summary>
    public DateTimeOffset Midnight(DateTime localDate)
    {
        var dt = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        while (TimeZone.IsInvalidTime(dt)) dt = dt.AddMinutes(30);
        return new DateTimeOffset(dt, TimeZone.GetUtcOffset(dt));
    }

    public static string DayKey(DateTime localDate) => localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Port of <c>RecapPeriod</c>: one calendar week, month or year. Calendar periods rather than rolling
/// windows, so stepping back lands on "the week of the 7th", not an arbitrary seven days.
/// </summary>
public sealed record RecapPeriod
{
    public RecapScope Scope { get; }
    /// <summary>First instant of the period.</summary>
    public DateTimeOffset Start { get; }
    /// <summary>First instant after the period.</summary>
    public DateTimeOffset End { get; }

    public RecapPeriod(RecapScope scope, DateTimeOffset containing, RecapCalendar calendar)
    {
        Scope = scope;
        var (start, end) = LocalBounds(scope, calendar.LocalDate(containing), calendar);
        Start = calendar.Midnight(start);
        End = calendar.Midnight(end);
    }

    public RecapPeriod Shifted(int offset, RecapCalendar calendar)
    {
        var start = calendar.LocalDate(Start);
        var moved = Scope switch
        {
            RecapScope.Week => start.AddDays(7 * offset),
            RecapScope.Month => start.AddMonths(offset),
            _ => start.AddYears(offset),
        };
        return new RecapPeriod(Scope, calendar.Midnight(moved), calendar);
    }

    internal static (DateTime Start, DateTime End) LocalBounds(RecapScope scope, DateTime local, RecapCalendar calendar)
    {
        switch (scope)
        {
            case RecapScope.Week:
                var start = local.AddDays(-(((int)local.DayOfWeek - (int)calendar.FirstDayOfWeek + 7) % 7));
                return (start, start.AddDays(7));
            case RecapScope.Month:
                var month = new DateTime(local.Year, local.Month, 1);
                return (month, month.AddMonths(1));
            default:
                var year = new DateTime(local.Year, 1, 1);
                return (year, year.AddYears(1));
        }
    }
}

/// <summary>What a recap needs from a dex entry (<c>DexEntry</c> in the companion code).</summary>
public interface IRecapGraduate
{
    DateTimeOffset? CaughtAt { get; }
    bool IsReleased { get; }
}

/// <summary>
/// Port of <c>UsageRecap</c>: a read-only recap of one period, computed on demand. Nothing here is a
/// reward or a goal: it reports what already happened.
/// </summary>
public sealed record UsageRecap
{
    /// <summary>One meter: a day in the week and month views, a month in the year view.</summary>
    /// <param name="Key"><c>yyyy-MM-dd</c> of the bucket's first day.</param>
    /// <param name="IsCurrent">Holds today.</param>
    /// <param name="HasData">False for the future and for days before the ledger started: unknown, not zero.</param>
    public sealed record Bucket(string Key, long Tokens, bool IsCurrent, bool HasData);

    public sealed record Day(string Key, long Tokens);

    public required RecapPeriod Period { get; init; }
    public required List<Bucket> Buckets { get; init; }
    public long Total { get; init; }
    /// <summary>Today falls inside the period.</summary>
    public bool IsInProgress { get; init; }
    /// <summary>The previous period over the same number of days while this one runs, the whole of it
    /// otherwise. Null when the ledger does not reach its first day: days the app never saw would count
    /// as zero and invent a jump.</summary>
    public long? PreviousTotal { get; init; }
    public Day? BestDay { get; init; }
    public int ActiveDays { get; init; }
    /// <summary>Days of the period, up to today, that the ledger covers: the denominator of <see cref="ActiveDays"/>.</summary>
    public int CountedDays { get; init; }
    /// <summary>Longest run of consecutive active days inside the period.</summary>
    public int BestStreak { get; init; }
    /// <summary>Individuals graduated during the period, newest first.</summary>
    public required List<IRecapGraduate> Graduated { get; init; }
    /// <summary>The previous period holds at least one day the ledger covers.</summary>
    public bool CanGoBack { get; init; }

    public double? Delta => PreviousTotal is > 0 ? (double)(Total - PreviousTotal.Value) / PreviousTotal.Value : null;

    public static UsageRecap Make(UsageLedger ledger, IEnumerable<IRecapGraduate> dex, RecapPeriod period,
        DateTimeOffset now, RecapCalendar calendar)
    {
        var today = calendar.LocalDate(now);
        var periodStart = calendar.LocalDate(period.Start);
        var periodEnd = calendar.LocalDate(period.End);

        // Local calendar dates, not +86400: a DST day is 23 or 25 hours long.
        static List<DateTime> Days(DateTime start, DateTime end)
        {
            var result = new List<DateTime>();
            for (var cursor = start; cursor < end; cursor = cursor.AddDays(1)) result.Add(cursor);
            return result;
        }
        bool Known(string key) => ledger.Tokens(key) > 0 || ledger.Covers(key);

        var periodDays = Days(periodStart, periodEnd);
        var elapsed = periodDays.Where(d => d <= today)
            .Select(d => RecapCalendar.DayKey(d)).Select(key => new Day(key, ledger.Tokens(key))).ToList();
        int bestStreak = 0, run = 0;
        foreach (var day in elapsed)
        {
            run = day.Tokens > 0 ? run + 1 : 0;
            bestStreak = Math.Max(bestStreak, run);
        }

        List<Bucket> buckets;
        if (period.Scope != RecapScope.Year)
        {
            buckets = periodDays.Select(d =>
            {
                var key = RecapCalendar.DayKey(d);
                return new Bucket(key, ledger.Tokens(key), d == today, d <= today && Known(key));
            }).ToList();
        }
        else
        {
            buckets = Enumerable.Range(0, 12).Select(index =>
            {
                var start = periodStart.AddMonths(index);
                var end = start.AddMonths(1);
                var keys = Days(start, end).Where(d => d <= today).Select(d => RecapCalendar.DayKey(d)).ToList();
                return new Bucket(RecapCalendar.DayKey(start), keys.Sum(ledger.Tokens),
                    start <= today && today < end, keys.Any(Known));
            }).ToList();
        }

        var isInProgress = periodStart <= today && today < periodEnd;
        var previous = period.Shifted(-1, calendar);
        var previousDays = Days(calendar.LocalDate(previous.Start), calendar.LocalDate(previous.End))
            .Select(d => RecapCalendar.DayKey(d)).ToList();
        // A running period is compared over the same number of days, or the 18th of the month would read
        // as a 40% drop against a whole month.
        var compared = isInProgress ? previousDays.Take(elapsed.Count).ToList() : previousDays;
        long? previousTotal = compared.Count > 0 && ledger.Covers(compared[0]) ? compared.Sum(ledger.Tokens) : null;

        var graduated = dex
            .Where(e => !e.IsReleased && e.CaughtAt is { } caught && caught >= period.Start && caught < period.End)
            .OrderByDescending(e => e.CaughtAt)
            .ToList();

        var active = elapsed.Where(d => d.Tokens > 0).ToList();
        return new UsageRecap
        {
            Period = period,
            Buckets = buckets,
            Total = elapsed.Sum(d => d.Tokens),
            IsInProgress = isInProgress,
            PreviousTotal = previousTotal,
            // Like Swift's max(by:), the first of equal maxima.
            BestDay = active.Aggregate((Day?)null, (best, d) => best == null || d.Tokens > best.Tokens ? d : best),
            ActiveDays = active.Count,
            CountedDays = elapsed.Count(d => Known(d.Key)),
            BestStreak = bestStreak,
            Graduated = graduated,
            CanGoBack = previousDays.Count > 0 && ledger.Covers(previousDays[^1]),
        };
    }
}
