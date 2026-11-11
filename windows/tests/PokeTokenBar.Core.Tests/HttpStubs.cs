using System.Net;
using System.Text;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>An in-process HTTP transport: every request is answered by <c>respond</c>, nothing leaves the machine.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly object _gate = new();
    public List<HttpRequestMessage> Requests { get; } = [];

    public HttpClient Client() => new(this);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate) Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>Scripted limits results, recording the prompt flag of every call. The last result repeats.</summary>
internal sealed class ScriptedLimits(params Func<LimitStatus>[] results) : IClaudeLimitsProviding
{
    private readonly Queue<Func<LimitStatus>> _results = new(results);
    public List<bool> PromptFlags { get; } = [];

    public static Func<LimitStatus> Ok(LimitStatus status) => () => status;
    public static Func<LimitStatus> Fail(LimitsError error) => () => throw error;

    public Task<LimitStatus> Fetch(bool allowKeychainPrompt)
    {
        PromptFlags.Add(allowKeychainPrompt);
        var next = _results.Count > 1 ? _results.Dequeue() : _results.Peek();
        return Task.FromResult(next());
    }
}

internal static class LimitFixtures
{
    public static LimitStatus Status(double fiveHour, string? email = null)
    {
        var status = LimitStatus.Decode(Encoding.UTF8.GetBytes(
            "{\"five_hour\":{\"utilization\":" + fiveHour.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"resets_at\":null}}"));
        status.AccountEmail = email;
        return status;
    }

    public static LimitStatus Full(double fiveHour, double sevenDay, double? fable = null, string? email = null, string? org = null)
    {
        static string N(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var parts = new List<string>
        {
            $"\"five_hour\":{{\"utilization\":{N(fiveHour)}}}",
            $"\"seven_day\":{{\"utilization\":{N(sevenDay)}}}",
        };
        if (fable is { } f)
            parts.Add($"\"limits\":[{{\"kind\":\"weekly_scoped\",\"percent\":{N(f)},\"scope\":{{\"model\":{{\"display_name\":\"Fable\"}}}}}}]");
        var status = LimitStatus.Decode(Encoding.UTF8.GetBytes("{" + string.Join(",", parts) + "}"));
        status.AccountEmail = email;
        status.AccountOrganizationName = org;
        return status;
    }
}
