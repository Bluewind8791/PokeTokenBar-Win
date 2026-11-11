namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>KoreanParticle.swift</c>: attaches the Korean particle matching the final consonant of a runtime
/// name (PokéAPI), so copy reads "피카츄가" / "이상해씨가" instead of the "이(가)" double form.
/// Only a final Hangul syllable (U+AC00…U+D7A3) is judged. Anything else (Unown's "[A]", "#123" before names
/// load, …) keeps the double form — better than a wrong particle.
/// </summary>
public enum KoreanParticle
{
    /// <summary>이/가</summary>
    Subject,
    /// <summary>을/를</summary>
    Object,
    /// <summary>으로/로</summary>
    Direction,
}

public static class KoreanParticleExtensions
{
    private const int SyllableFirst = 0xAC00;
    private const int SyllableLast = 0xD7A3;
    private const int Rieul = 8;

    public static string Attach(this KoreanParticle particle, string word)
    {
        if (Coda(word) is not { } coda) return word + Fallback(particle);
        return particle switch
        {
            KoreanParticle.Subject => word + (coda == 0 ? "가" : "이"),
            KoreanParticle.Object => word + (coda == 0 ? "를" : "을"),
            // A ㄹ final takes "로", not "으로" (서울로, 알로).
            KoreanParticle.Direction => word + (coda == 0 || coda == Rieul ? "로" : "으로"),
            _ => throw new ArgumentOutOfRangeException(nameof(particle), particle, null),
        };
    }

    private static string Fallback(KoreanParticle particle) => particle switch
    {
        KoreanParticle.Subject => "이(가)",
        KoreanParticle.Object => "을(를)",
        KoreanParticle.Direction => "(으)로",
        _ => throw new ArgumentOutOfRangeException(nameof(particle), particle, null),
    };

    /// <summary>Final-consonant index of the last character (0 = none); null when it is not a Hangul syllable.</summary>
    private static int? Coda(string word)
    {
        if (word.Length == 0) return null;
        int last = word[^1];
        if (last < SyllableFirst || last > SyllableLast) return null;
        return (last - SyllableFirst) % 28;
    }
}
