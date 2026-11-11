using System.Text;

namespace PokeTokenBar.Core.Tests;

/// <summary>One piece of a string literal: plain text, or an interpolation with its source expression.</summary>
internal readonly record struct LiteralPart(bool IsInterpolation, string Value);

/// <summary>
/// Minimal source scanners for the localization parity tests: they read the string literals of every
/// <c>t(...)</c> call in Localization.swift and every <c>T(...)</c> call in Localization.cs, understanding
/// escapes, interpolations, Swift multiline literals and <c>//</c> comments (which may contain quotes).
/// </summary>
internal static class LocalizationSource
{
    public static string SwiftCore(string file) =>
        Read(Path.Combine(Fixtures.RepoRoot(), "Sources", "PokeTokenBar", "Core", file));

    public static string CsCore(string file) =>
        Read(Path.Combine(WindowsRoot(), "src", "PokeTokenBar.Core", file));

    public static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    /// <summary>The Windows port root (folder holding PokeTokenBar.Windows.slnx) above the test binary.</summary>
    public static string WindowsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PokeTokenBar.Windows.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("PokeTokenBar.Windows.slnx not found above the test binary");
    }

    /// <summary>The 8 literals of every <c>t(</c> (Swift) or <c>T(</c> (C#) call whose arguments are all literals.</summary>
    public static List<List<LiteralPart>[]> TranslationCalls(string source, bool swift)
    {
        var name = swift ? 't' : 'T';
        var calls = new List<List<LiteralPart>[]>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '"' || (!swift && c == '$' && Peek(source, i + 1) == '"'))
            {
                i = ReadLiteral(source, i, swift, out _);
                continue;
            }
            if (c == '/' && Peek(source, i + 1) == '/')
            {
                i = source.IndexOf('\n', i) is var nl and >= 0 ? nl : source.Length;
                continue;
            }
            if (!swift && c == '\'')
            {
                i = SkipCharLiteral(source, i);
                continue;
            }
            if (c == name && Peek(source, i + 1) == '(' && (i == 0 || !IsIdentifierChar(source[i - 1])))
            {
                var j = i + 2;
                var args = new List<List<LiteralPart>>();
                while (j < source.Length)
                {
                    if (char.IsWhiteSpace(source[j]) || source[j] == ',') { j++; continue; }
                    if (source[j] == '"' || (!swift && source[j] == '$' && Peek(source, j + 1) == '"'))
                    {
                        j = ReadLiteral(source, j, swift, out var parts);
                        args.Add(parts);
                        continue;
                    }
                    break;
                }
                if (args.Count == 8 && source[j] == ')') calls.Add(args.ToArray());
                i = j;
                continue;
            }
            i++;
        }
        return calls;
    }

    /// <summary>Reads a literal starting at <paramref name="i"/>; returns the index after it.</summary>
    public static int ReadLiteral(string s, int i, bool swift, out List<LiteralPart> parts) =>
        swift ? ReadSwiftString(s, i, out parts) : ReadCsString(s, i, out parts);

    public static int ReadSwiftString(string s, int i, out List<LiteralPart> parts)
    {
        string raw;
        int end;
        if (string.CompareOrdinal(s, i, "\"\"\"", 0, 3) == 0)
        {
            // Multiline: content lines lose the closing delimiter's indentation.
            var start = s.IndexOf('\n', i) + 1;
            var close = s.IndexOf("\"\"\"", start, StringComparison.Ordinal);
            var lines = s[start..close].Split('\n');
            var indent = lines[^1];
            raw = string.Join("\n", lines[..^1].Select(l => l.StartsWith(indent, StringComparison.Ordinal) ? l[indent.Length..] : l.TrimStart()));
            end = close + 3;
        }
        else
        {
            var j = i + 1;
            while (s[j] != '"')
            {
                if (s[j] == '\\') j = s[j + 1] == '(' ? SkipParens(s, j + 1) : j + 2;
                else j++;
            }
            raw = s[(i + 1)..j];
            end = j + 1;
        }
        parts = UnescapeSwift(raw);
        return end;
    }

    /// <summary><paramref name="j"/> is at '('; returns the index after the matching ')', skipping nested strings.</summary>
    private static int SkipParens(string s, int j)
    {
        var depth = 0;
        while (true)
        {
            var c = s[j];
            if (c == '"') { j = ReadSwiftString(s, j, out _); continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return j + 1;
            j++;
        }
    }

    private static List<LiteralPart> UnescapeSwift(string raw)
    {
        var parts = new List<LiteralPart>();
        var buf = new StringBuilder();
        for (var k = 0; k < raw.Length;)
        {
            if (raw[k] != '\\') { buf.Append(raw[k++]); continue; }
            var n = raw[k + 1];
            switch (n)
            {
                case 'n': buf.Append('\n'); k += 2; break;
                case 't': buf.Append('\t'); k += 2; break;
                case 'r': buf.Append('\r'); k += 2; break;
                case '0': buf.Append('\0'); k += 2; break;
                case '"' or '\'' or '\\': buf.Append(n); k += 2; break;
                case '\n': k += 2; break; // line continuation in multiline literals
                case 'u':
                    var close = raw.IndexOf('}', k);
                    buf.Append(char.ConvertFromUtf32(Convert.ToInt32(raw[(k + 3)..close], 16)));
                    k = close + 1;
                    break;
                case '(':
                    var end = SkipParens(raw, k + 1);
                    Flush(parts, buf);
                    parts.Add(new LiteralPart(true, raw[(k + 2)..(end - 1)]));
                    k = end;
                    break;
                default: throw new FormatException($"unknown Swift escape \\{n}");
            }
        }
        Flush(parts, buf);
        return parts;
    }

    public static int ReadCsString(string s, int i, out List<LiteralPart> parts)
    {
        var interpolated = s[i] == '$';
        var j = interpolated ? i + 2 : i + 1;
        parts = new List<LiteralPart>();
        var buf = new StringBuilder();
        while (s[j] != '"')
        {
            var c = s[j];
            if (c == '\\')
            {
                var n = s[j + 1];
                buf.Append(n switch
                {
                    'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', '"' or '\'' or '\\' => n,
                    _ => throw new FormatException($"unknown C# escape \\{n}"),
                });
                j += 2;
            }
            else if (interpolated && (c == '{' || c == '}') && s[j + 1] == c)
            {
                buf.Append(c);
                j += 2;
            }
            else if (interpolated && c == '{')
            {
                var depth = 0;
                var start = j + 1;
                do
                {
                    if (s[j] == '{') depth++;
                    else if (s[j] == '}') depth--;
                    j++;
                } while (depth > 0);
                Flush(parts, buf);
                parts.Add(new LiteralPart(true, s[start..(j - 1)]));
            }
            else
            {
                buf.Append(c);
                j++;
            }
        }
        Flush(parts, buf);
        return j + 1;
    }

    /// <summary>Literal text with every interpolation reduced to a "{}" slot — comparable across languages.</summary>
    public static string Shape(List<LiteralPart> parts) =>
        string.Concat(parts.Select(p => p.IsInterpolation ? "{}" : p.Value));

    private static void Flush(List<LiteralPart> parts, StringBuilder buf)
    {
        if (buf.Length == 0) return;
        parts.Add(new LiteralPart(false, buf.ToString()));
        buf.Clear();
    }

    private static int SkipCharLiteral(string s, int i)
    {
        var j = i + 1;
        while (s[j] != '\'') j += s[j] == '\\' ? 2 : 1;
        return j + 1;
    }

    /// <summary>Index of the '}' closing the '{' at <paramref name="open"/>, skipping Swift strings and comments.</summary>
    public static int MatchingBrace(string s, int open)
    {
        var depth = 0;
        for (var i = open; ;)
        {
            var c = s[i];
            if (c == '"') { i = ReadSwiftString(s, i, out _); continue; }
            if (c == '/' && Peek(s, i + 1) == '/') { i = s.IndexOf('\n', i); continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
            i++;
        }
    }

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
}
