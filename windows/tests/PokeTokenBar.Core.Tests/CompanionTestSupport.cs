using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// The Swift tests run on <c>@MainActor</c>: work the store starts with <c>Task { … }</c> runs only when the test
/// awaits. This single-threaded pump reproduces that: the body and every continuation run on one thread, and work
/// posted by the store runs only when the test body yields. Posted work still queued when the body finishes is
/// dropped, exactly like Swift tasks that never got the main actor during a synchronous test.
/// </summary>
internal static class MainActor
{
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
        private volatile bool _done;

        public override void Post(SendOrPostCallback d, object? state)
        {
            try { _queue.Add((d, state)); } catch (InvalidOperationException) { }
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Complete()
        {
            _done = true;
            try { _queue.Add((_ => { }, null)); } catch (InvalidOperationException) { }
        }

        public void Run()
        {
            while (!_done)
            {
                var (d, state) = _queue.Take();
                if (_done) break;
                d(state);
            }
            _queue.CompleteAdding();
        }
    }

    public static void Run(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        var context = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var task = body();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.Run();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    public static void Run(Action body) => Run(() =>
    {
        body();
        return Task.CompletedTask;
    });

    /// <summary>Swift tests' <c>waitUntil</c>: polls while yielding to posted work.</summary>
    public static async Task<bool> WaitUntil(Func<bool> condition, double timeoutSeconds = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(1);
        }
        return condition();
    }

    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, double timeoutSeconds = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(1);
        }
        return await condition();
    }

    /// <summary>Lets queued store work run (a few pump turns) without a condition.</summary>
    public static async Task Drain(int turns = 5)
    {
        for (var i = 0; i < turns; i++) await Task.Yield();
        await Task.Delay(1);
    }
}

/// <summary>Swift tests' <c>SeededRNG</c> (SplitMix64). A class here: create one per store (Swift copied the struct).</summary>
internal sealed class SeededRng(ulong seed) : IRandomNumberGenerator
{
    private ulong _state = seed;

    public ulong Next()
    {
        unchecked
        {
            _state += 0x9E37_79B9_7F4A_7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }
    }
}

internal sealed class CountingRng(ulong seed) : IRandomNumberGenerator
{
    private readonly SeededRng _base = new(seed);
    public int CallCount { get; private set; }

    public ulong Next()
    {
        CallCount += 1;
        return _base.Next();
    }
}

/// <summary>Returns a fixed sequence (then repeats the last value).</summary>
internal sealed class SequenceRng(params ulong[] values) : IRandomNumberGenerator
{
    private int _index;
    public ulong Next() => values[Math.Min(_index++, values.Length - 1)];
}

/// <summary>Swift <c>StubProvider</c>: one line; the index is that line's base alone (one roll, always that base).</summary>
internal sealed class StubProvider(EvoLine value) : IPokeProviding
{
    public EvoLine Value { get; } = value;
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(Value);
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        Task.FromResult<IReadOnlyList<BaseSpecies>>([new BaseSpecies(Value.BaseID, 255)]);
}

internal sealed class PokeStubException : Exception;

/// <summary>Swift <c>IndexProvider</c>: injected base index + an evolution-less line for any requested id.</summary>
internal sealed class IndexProvider : IPokeProviding
{
    public List<BaseSpecies> Index { get; set; } = [];
    public bool FailAll { get; set; }
    public Task<EvoLine> Line(int baseSpeciesID) => Task.FromResult(Lines.Make(baseSpeciesID, Lines.Node(baseSpeciesID)));
    public Task<IReadOnlyList<BaseSpecies>> BaseSpeciesIndex() =>
        FailAll ? Task.FromException<IReadOnlyList<BaseSpecies>>(new PokeStubException())
                : Task.FromResult<IReadOnlyList<BaseSpecies>>(Index);
}

internal static class Lines
{
    public static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    public static EvoNode Node(int id, params EvoNode[] children) => new(id, children);

    public static IEnumerable<int> AllIDs(EvoNode n) => new[] { n.SpeciesID }.Concat(n.Children.SelectMany(AllIDs));

    /// <summary>Swift <c>makeLine</c>: names en "P{id}", ko "포{id}", ja "ポ{id}".</summary>
    public static EvoLine Make(int @base, EvoNode tree, Rarity rarity = Rarity.Common)
    {
        var names = new Dictionary<int, Dictionary<string, string>>();
        foreach (var id in AllIDs(tree)) names[id] = new() { ["en"] = $"P{id}", ["ko"] = $"포{id}", ["ja"] = $"ポ{id}" };
        return new EvoLine(@base, tree, rarity, names);
    }

    /// <summary>3-stage linear: 1→2→3</summary>
    public static EvoLine Linear3 => Make(1, Node(1, Node(2, Node(3))));
    /// <summary>Branch: 10 → {11,12,13}</summary>
    public static EvoLine Branch3 => Make(10, Node(10, Node(11), Node(12), Node(13)));
    /// <summary>No evolution: 20</summary>
    public static EvoLine NoEvo => Make(20, Node(20));

    public static Dictionary<int, Dictionary<string, string>> Names(params (int Id, string Lang, string Name)[] items)
    {
        var result = new Dictionary<int, Dictionary<string, string>>();
        foreach (var (id, lang, name) in items)
        {
            if (!result.TryGetValue(id, out var m)) result[id] = m = [];
            m[lang] = name;
        }
        return result;
    }
}

/// <summary>Temp state files and the JSON fixtures the Swift tests build with JSONEncoder.</summary>
internal static class StateFiles
{
    public static string TempPath(string prefix = "poke") =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.json");

    public static string Json(MonState m) => CompanionJson.ToJsonString(CompanionJson.EncodeMon(m));

    public static string Json(IEnumerable<DexEntry> entries) =>
        CompanionJson.ToJsonString(new JsonArray(entries.Select(e =>
            (JsonNode)CompanionJson.EncodeDexEntry(e, SwiftDateEncoding.DeferredToDate)).ToArray()));

    public static string Json(CompanionState s) => System.Text.Encoding.UTF8.GetString(CompanionJson.Serialize(s));

    public static string WriteText(string json, string? path = null)
    {
        path ??= TempPath();
        File.WriteAllText(path, json);
        return path;
    }

    public static string Write(CompanionState s, string? path = null) => WriteText(Json(s), path);

    public static CompanionStore Store(EvoLine line, ulong seed = 7, string? path = null, AppSettings? settings = null,
        bool dittoRolling = false) =>
        new(new StubProvider(line), clock: () => Lines.FixedNow, filePath: path ?? TempPath(), rng: new SeededRng(seed),
            dittoDisguiseRollingEnabled: dittoRolling, settings: settings ?? new AppSettings(null));
}
