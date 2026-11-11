using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Parity guard between the Swift string table (Localization.swift + LocalizationErrors.swift) and its port.
/// A string changed or added on macOS without regenerating Localization.cs (or the reverse) fails here
/// instead of silently diverging.
/// </summary>
public class LocalizationParityTests
{
    private static readonly string[] SwiftFiles = ["Localization.swift", "LocalizationErrors.swift"];
    private static readonly string[] CsFiles = ["Localization.cs", "LocalizationErrors.cs"];

    private static string Pascal(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    private sealed record SwiftMember(string Name, bool IsFunc, string Params, string Body);

    /// <summary>Top-level members of <c>struct L</c> / <c>extension L</c> (4-space indented var/func).</summary>
    private static List<SwiftMember> SwiftMembers(string source)
    {
        var members = new List<SwiftMember>();
        foreach (Match m in Regex.Matches(source, @"^    (var|func) (\w+)(?:\(([^)]*)\))?[^{\n]*\{", RegexOptions.Multiline))
        {
            var open = m.Index + m.Length - 1;
            var close = LocalizationSource.MatchingBrace(source, open);
            members.Add(new SwiftMember(m.Groups[2].Value, m.Groups[1].Value == "func", m.Groups[3].Value,
                source[(open + 1)..close].Trim()));
        }
        return members;
    }

    /// <summary>Every <c>t(...)</c> tuple of the Swift sources, as a multiset, equals every <c>T(...)</c> tuple of the port.</summary>
    internal static List<string> TupleDrift(IEnumerable<string> swiftSources, IEnumerable<string> csSources)
    {
        static List<string> Tuples(IEnumerable<string> sources, bool swift) => sources
            .SelectMany(s => LocalizationSource.TranslationCalls(s, swift))
            .Select(call => string.Join(" | ", call.Select(LocalizationSource.Shape)))
            .Order(StringComparer.Ordinal).ToList();
        var swiftTuples = Tuples(swiftSources, swift: true);
        var csTuples = Tuples(csSources, swift: false);
        var drift = new List<string>();
        foreach (var missing in swiftTuples.GroupBy(t => t).Where(g => g.Count() > csTuples.Count(t => t == g.Key)))
            drift.Add("only in Swift: " + missing.Key);
        foreach (var extra in csTuples.GroupBy(t => t).Where(g => g.Count() > swiftTuples.Count(t => t == g.Key)))
            drift.Add("only in C#: " + extra.Key);
        return drift;
    }

    [Fact]
    public void EveryTranslationTupleMatchesSwiftSource()
    {
        var swift = SwiftFiles.Select(LocalizationSource.SwiftCore).ToList();
        var calls = swift.Sum(s => LocalizationSource.TranslationCalls(s, swift: true).Count);
        Assert.True(calls > 400, $"the scanner must actually find the t(...) calls (found {calls})");
        Assert.Empty(TupleDrift(swift, CsFiles.Select(LocalizationSource.CsCore)));
    }

    /// <summary>The tuple guard must fail on an injected drift — a guard that only ever passes checks nothing.</summary>
    [Fact]
    public void TupleGuardRejectsAnInjectedDrift()
    {
        var swift = SwiftFiles.Select(LocalizationSource.SwiftCore).ToList();
        var cs = CsFiles.Select(LocalizationSource.CsCore).ToList();
        var edited = swift.Select(s => s.Replace("\"Hebdo\"", "\"Hebdomadaire\"")).ToList();
        Assert.NotEqual(swift, edited);
        Assert.Contains(TupleDrift(edited, cs), d => d.StartsWith("only in Swift:") && d.Contains("Hebdomadaire"));
        Assert.Contains(TupleDrift(edited, cs), d => d.StartsWith("only in C#:") && d.Contains("Hebdo |"));
    }

    [Fact]
    public void EverySwiftMemberExistsInPort()
    {
        var csNames = typeof(L).GetMembers(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).ToHashSet();
        var swiftNames = SwiftFiles.SelectMany(f => SwiftMembers(LocalizationSource.SwiftCore(f)))
            .Select(m => m.Name).Where(n => n != "t").Distinct().ToList();
        Assert.True(swiftNames.Count > 350, $"member scan found only {swiftNames.Count}");
        Assert.DoesNotContain(swiftNames, n => !csNames.Contains(Pascal(n)));
    }

    /// <summary>
    /// Runtime check of every member whose Swift body is just <c>t(...)</c>: the C# member of the same
    /// (PascalCase) name returns exactly the Swift string in every language, with sentinel arguments
    /// substituted where the Swift literal interpolates a parameter.
    /// </summary>
    [Fact]
    public void SimpleMembersReturnTheSwiftStringsInEveryLanguage()
    {
        var failures = new List<string>();
        var checkedMembers = 0;
        foreach (var file in SwiftFiles)
        {
            foreach (var member in SwiftMembers(LocalizationSource.SwiftCore(file)))
            {
                if (!member.Body.StartsWith("t(")) continue;
                var calls = LocalizationSource.TranslationCalls(member.Body, swift: true);
                if (calls.Count != 1) continue;
                if (!TryArguments(member.Params, out var names, out var values)) continue;
                var text = values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)!).ToArray();
                var expected = calls[0].Select(parts => Expand(parts, names, text)).ToArray();
                if (expected.Any(e => e is null)) continue;

                checkedMembers++;
                foreach (var lang in Enum.GetValues<AppLanguage>())
                {
                    var l = new L(lang);
                    string? actual;
                    if (member.IsFunc)
                    {
                        var method = typeof(L).GetMethods().SingleOrDefault(m => m.Name == Pascal(member.Name) && m.GetParameters().Length == values.Length);
                        actual = method?.Invoke(l, values) as string;
                    }
                    else
                    {
                        actual = typeof(L).GetProperty(Pascal(member.Name))?.GetValue(l) as string;
                    }
                    var want = expected[(int)lang];
                    if (actual != want) failures.Add($"{lang.RawValue()}.{member.Name}: expected '{want}' got '{actual}'");
                }
            }
        }
        Assert.True(checkedMembers > 340, $"only {checkedMembers} simple members were checked");
        Assert.Empty(failures);
    }

    private static bool TryArguments(string swiftParams, out string[] names, out object[] values)
    {
        var parsed = swiftParams.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Regex.Match(p, @"^(?:\w+ )?(\w+): (\S+)$")).ToList();
        names = parsed.Select(m => m.Groups[1].Value).ToArray();
        values = new object[parsed.Count];
        for (var k = 0; k < parsed.Count; k++)
        {
            switch (parsed[k].Success ? parsed[k].Groups[2].Value : null)
            {
                case "String": values[k] = $"ZQXP{k}"; break;
                case "Int": values[k] = 4200 + k; break;
                case "UInt64": values[k] = (ulong)(4200 + k); break;
                default: return false;
            }
        }
        return true;
    }

    /// <summary>The Swift literal with its interpolations evaluated; null when an expression is not understood.</summary>
    private static string? Expand(List<LiteralPart> parts, string[] names, string[] values)
    {
        var result = "";
        foreach (var part in parts)
        {
            if (!part.IsInterpolation) { result += part.Value; continue; }
            var particle = Regex.Match(part.Value, @"^KoreanParticle\.(\w+)\.attach\(to: (\w+)\)$");
            var name = particle.Success ? particle.Groups[2].Value : part.Value;
            var index = Array.IndexOf(names, name);
            if (index < 0) return null;
            result += particle.Success
                ? Enum.Parse<KoreanParticle>(Pascal(particle.Groups[1].Value)).Attach(values[index])
                : values[index];
        }
        return result;
    }

    /// <summary><see cref="L.RareCandyXp"/> mirrors <c>RareCandy.xp</c>, which the candy description interpolates.</summary>
    [Fact]
    public void RareCandyXpMatchesSwiftConstant()
    {
        var swift = LocalizationSource.SwiftCore("CompanionModel.swift");
        var match = Regex.Match(swift, @"enum RareCandy \{.*?static let xp = ([\d_]+)", RegexOptions.Singleline);
        Assert.True(match.Success);
        Assert.Equal(L.RareCandyXp, long.Parse(match.Groups[1].Value.Replace("_", ""), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ScannersReadInterpolatedMultilineAndCommentedSources()
    {
        const string swift = """"
            // a comment with "quotes" and t( inside
            var a: String { t("가", "a", "あ", "es \(x) ción", "fr", "pt \(f("q")) ção", "de", "ru") }
            func b() -> String {
                t("""
                  line1
                    line2 \(y)
                  """,
                  "b \"quoted\"", "い", "es", "fr", "pt", "de", "ru")
            }
            """";
        // Normalize like LocalizationSource.Read: a CRLF checkout must not change what is scanned.
        var calls = LocalizationSource.TranslationCalls(swift.Replace("\r\n", "\n"), swift: true);
        Assert.Equal(2, calls.Count);
        Assert.Equal("es {} ción", LocalizationSource.Shape(calls[0][3]));
        Assert.Equal("f(\"q\")", calls[0][5][1].Value);
        Assert.Equal("line1\n  line2 {}", LocalizationSource.Shape(calls[1][0]));
        Assert.Equal("b \"quoted\"", LocalizationSource.Shape(calls[1][1]));

        const string cs = """
            // T("not", "a", "call")
            public string A(int n) => T($"{n}개 {{x}}", "b \"q\"\n", "c", "d", "e", "f", "g", "h");
            """;
        var csCalls = LocalizationSource.TranslationCalls(cs.Replace("\r\n", "\n"), swift: false);
        Assert.Single(csCalls);
        Assert.Equal("{}개 {x}", LocalizationSource.Shape(csCalls[0][0]));
        Assert.Equal("b \"q\"\n", LocalizationSource.Shape(csCalls[0][1]));
    }
}
