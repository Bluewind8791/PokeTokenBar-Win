using System.Globalization;

namespace PokeTokenBar.Core;

/// <summary>Port of <c>AppLanguage</c> (CompanionModel.swift). Pokémon names come from PokéAPI's multilingual names.</summary>
public enum AppLanguage
{
    Ko, En, Ja, Es, Fr, Pt, De, Ru,
}

public static class AppLanguageExtensions
{
    /// <summary>Persisted value, identical to the Swift raw value ("ko", "en", …).</summary>
    public static string RawValue(this AppLanguage language) => language switch
    {
        AppLanguage.Ko => "ko",
        AppLanguage.En => "en",
        AppLanguage.Ja => "ja",
        AppLanguage.Es => "es",
        AppLanguage.Fr => "fr",
        AppLanguage.Pt => "pt",
        AppLanguage.De => "de",
        AppLanguage.Ru => "ru",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>Inverse of <see cref="RawValue"/> (Swift <c>init(rawValue:)</c>); null for unknown values.</summary>
    public static AppLanguage? FromRawValue(string? raw)
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
            if (language.RawValue() == raw) return language;
        return null;
    }

    /// <summary>PokéAPI <c>language.name</c> candidates; the first match wins.</summary>
    public static IReadOnlyList<string> ApiCodes(this AppLanguage language) => language switch
    {
        AppLanguage.Ko => ["ko"],
        AppLanguage.En => ["en"],
        AppLanguage.Ja => ["ja-hrkt", "ja"],
        AppLanguage.Es => ["es"],
        AppLanguage.Fr => ["fr"],
        AppLanguage.Pt => ["pt-br", "pt"],
        AppLanguage.De => ["de"],
        // Official Russian names exist, but PokéAPI has no ru data yet, so this falls back to English for now —
        // a temporary data gap, not a Russian naming convention. Once PokéAPI ships ru it is used as is.
        AppLanguage.Ru => ["ru"],
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    public static string Label(this AppLanguage language) => language switch
    {
        AppLanguage.Ko => "한국어",
        AppLanguage.En => "English",
        AppLanguage.Ja => "日本語",
        AppLanguage.Es => "Español",
        AppLanguage.Fr => "Français",
        AppLanguage.Pt => "Português",
        AppLanguage.De => "Deutsch",
        AppLanguage.Ru => "Русский",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    /// <summary>Port of <c>displayLocale</c>: the culture used for dates and numbers shown in the app language.</summary>
    public static CultureInfo DisplayCulture(this AppLanguage language) => CultureInfo.GetCultureInfo(language.RawValue());

    /// <summary>Picks this language's name from langCode → name (first <see cref="ApiCodes"/> match, then English).</summary>
    public static string? ResolveName(this AppLanguage language, IReadOnlyDictionary<string, string> byLang) =>
        PokemonNameLocalization.Resolve(byLang, language.ApiCodes());

    /// <summary>
    /// Default language for a new install, inferred from the system UI language (global release: never force Korean).
    /// Existing users keep their saved language.
    /// </summary>
    public static AppLanguage SystemDefault() => SystemDefault(CultureInfo.CurrentUICulture.Name);

    /// <summary>
    /// Pure mapping behind <see cref="SystemDefault()"/>, split out so locale branches are testable without
    /// changing the environment. Only ko/ja/es/fr/pt/de/ru match; everything else is English.
    /// </summary>
    public static AppLanguage SystemDefault(string? preferredLanguage)
    {
        var prefix = preferredLanguage is null ? null : preferredLanguage[..Math.Min(2, preferredLanguage.Length)].ToLowerInvariant();
        return prefix switch
        {
            "ko" => AppLanguage.Ko,
            "ja" => AppLanguage.Ja,
            "es" => AppLanguage.Es,
            "fr" => AppLanguage.Fr,
            "pt" => AppLanguage.Pt,
            "de" => AppLanguage.De,
            "ru" => AppLanguage.Ru,
            _ => AppLanguage.En,
        };
    }
}
