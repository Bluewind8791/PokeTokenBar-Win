using System.Net.Sockets;
using PokeTokenBar.Core;

namespace PokeTokenBar.Core.Tests;

/// <summary>
/// Placeholder-parity guard (port of LocalizationInterpolationTests.swift). <c>T(...)</c> only enforces the
/// argument count, so a translation that dropped its placeholder still compiles. Each interpolated member gets a
/// sentinel that cannot occur in natural copy, and every language's output must still contain it. Languages come
/// from the enum, never a literal list, so coverage cannot silently stop when a language is added.
/// </summary>
public class LocalizationInterpolationTests
{
    private const string A = "ZQXSENTINELA";
    private const string B = "ZQXSENTINELB";

    private static void Expect(AppLanguage lang, string member, string? produced, params string[] needles)
    {
        foreach (var needle in needles)
            Assert.True(produced?.Contains(needle) == true, $"{lang.RawValue()}.{member}: '{needle}' is missing → '{produced}'");
    }

    [Fact]
    public void InterpolationsSurviveInEverySupportedLanguage()
    {
        foreach (var lang in Enum.GetValues<AppLanguage>())
        {
            var l = new L(lang);

            // Limits & forecast
            Expect(lang, "plan", l.Plan(A), A);
            Expect(lang, "forecastReach", l.ForecastReach(A), A);
            Expect(lang, "claudeLimitEntry", l.ClaudeLimitEntry("weekly_scoped", A), A);
            Expect(lang, "limitsAccount", l.LimitsAccount(A), A);
            Expect(lang, "additionalClaudeAccountsFound", l.AdditionalClaudeAccountsFound(73), "73");
            Expect(lang, "additionalClaudeAccountsDetected", l.AdditionalClaudeAccountsDetected(A), A);
            Expect(lang, "additionalAccountExpiredHint", l.AdditionalAccountExpiredHint(A), A);
            Expect(lang, "accountSessionKeyLabel", l.AccountSessionKeyLabel(A), A);
            Expect(lang, "unattributedClaudeUsage", l.UnattributedClaudeUsage(A), A);
            Expect(lang, "trackedAccountToolTip", l.TrackedAccountToolTip(A), A);
            Expect(lang, "codexWindow(h)", l.CodexWindow(420), "7"); // 420 min → 7 h
            Expect(lang, "codexWindow(m)", l.CodexWindow(37), "37");
            Expect(lang, "percentRemaining", l.PercentRemaining(A), A);
            Expect(lang, "paceHint", l.PaceHint(A), A);
            Expect(lang, "paceDelta(+)", l.PaceDelta(4242), "4242");
            Expect(lang, "paceDelta(-)", l.PaceDelta(-4242), "4242");
            Expect(lang, "limitRefreshHTTPError(401)", l.LimitRefreshHTTPError(401), "401");
            Expect(lang, "limitRefreshHTTPError(404)", l.LimitRefreshHTTPError(404), "404");

            // Floating pet & settings
            Expect(lang, "floatingPetHoverTokensOnly", l.FloatingPetHoverTokensOnly(A), A);
            Expect(lang, "floatingPetHoverWithLimit", l.FloatingPetHoverWithLimit(A, B), A, B);
            Expect(lang, "floatingPetHoverSulkHours", l.FloatingPetHoverSulkHours(4242), "4242");
            Expect(lang, "floatingPetHoverSulkDays", l.FloatingPetHoverSulkDays(4242), "4242");
            Expect(lang, "intervalLabel", l.IntervalLabel(1860), "31"); // 1860 s → 31 min
            Expect(lang, "customScanRootsMatches", l.CustomScanRootsMatches(4242), "4242");

            // Save transfer
            Expect(lang, "importConfirmBody",
                l.ImportConfirmBody(4242, A, "ZQXEXPORTEDAT", "ZQXDEVICE", 1717, B),
                "4242", A, "ZQXEXPORTEDAT", "ZQXDEVICE", "1717", B);
            Expect(lang, "importSaveDone", l.ImportSaveDone(4242, A), "4242", A);
            Expect(lang, "restoreConfirmBody", l.RestoreConfirmBody("ZQXEXPORTEDAT", 4242, A, 1717, B),
                "ZQXEXPORTEDAT", "4242", A, "1717", B);
            Expect(lang, "restoreDoneMessage", l.RestoreDoneMessage(4242, A), "4242", A);
            Expect(lang, "snapshotDexAndTokens", l.SnapshotDexAndTokens(4242, A), "4242", A);

            // Mail report
            Expect(lang, "reportMailFallback", l.ReportMailFallback(A), A);
            Expect(lang, "reportMailSubject", l.ReportMailSubject(A), A);
            Expect(lang, "reportMailBody", l.ReportMailBody(A, B), A, B);

            // Companion progress & status
            Expect(lang, "stage", l.Stage(4242, 1717), "4242", "1717");
            Expect(lang, "eggToHatch", l.EggToHatch(A), A);
            Expect(lang, "eggFirstRunHint", l.EggFirstRunHint(A), A);
            Expect(lang, "toNextEvolution", l.ToNextEvolution(A), A);
            Expect(lang, "toGraduation", l.ToGraduation(A), A);
            Expect(lang, "growthBoost", l.GrowthBoost(4242), "4242");
            Expect(lang, "graduated", l.Graduated(A), A);
            Expect(lang, "statusEvolved", l.StatusEvolved(A), A);

            // Pokédex
            Expect(lang, "dexTotal", l.DexTotal(4242), "4242");
            Expect(lang, "dexSpeciesTotal", l.DexSpeciesTotal(4242), "4242");
            Expect(lang, "dexPageLabel", l.DexPageLabel(4242, 1717), "4242", "1717");

            // System notifications
            Expect(lang, "notifHatchBody", l.NotifHatchBody(A), A);
            Expect(lang, "notifShinyHatchBody", l.NotifShinyHatchBody(A, 4242), A, "4242");
            Expect(lang, "notifEvolveBody", l.NotifEvolveBody(A, B), A, B);
            Expect(lang, "notifDittoRevealBody", l.NotifDittoRevealBody(A), A);
            Expect(lang, "notifShinyDittoRevealBody", l.NotifShinyDittoRevealBody(A), A);
            Expect(lang, "notifGraduateBody", l.NotifGraduateBody(A), A);
            Expect(lang, "notifBody", l.NotifBody(A, B), A, B);
            Expect(lang, "notifCandyTitle", l.NotifCandyTitle(A, 4242), A, "4242");
            Expect(lang, "notifCandyBody", l.NotifCandyBody(A), A);

            // Updates
            Expect(lang, "updateAvailable", l.UpdateAvailable(A, B), A, B);
            Expect(lang, "updateFound", l.UpdateFound(A), A);
            Expect(lang, "upToDate", l.UpToDate(A), A);
            Expect(lang, "skippedVersion", l.SkippedVersion(A), A);

            // Bag, shop, eggs
            Expect(lang, "useOnCurrent", l.UseOnCurrent(A), A);
            Expect(lang, "buyConfirm", l.BuyConfirm(A), A);
            Expect(lang, "ownedCount", l.OwnedCount(4242), "4242");
            Expect(lang, "eggConfirm", l.EggConfirm(A, B), A, B);
            // Constant-derived substitution: hardcoding it would let the candy blurb drift from the real XP.
            Expect(lang, "itemDescription(rareCandy)", l.ItemDescription("rareCandy"), TokenFormatter.Compact(L.RareCandyXp));
            // Rarity label spliced into copy: a translation that spells the tier out instead is caught here.
            Expect(lang, "eggDescription(rare)", l.EggDescription("rare"), l.RarityRare);
            Expect(lang, "eggGuaranteeHint(uncommon)", l.EggGuaranteeHint("uncommon"), l.RarityUncommon);
        }
    }

    /// <summary>The guard above must reject a translation that dropped its placeholder.</summary>
    [Fact]
    public void GuardRejectsATranslationThatDroppedItsPlaceholder()
    {
        Assert.Contains(A, $"Hoje: {A} tokens");
        Assert.DoesNotContain(A, "Hoje: tokens");
    }
}

/// <summary>Port of LocalizationColumnTests.swift: <c>T(...)</c> is positional, so es/pt are easy to swap.</summary>
public class LocalizationColumnTests
{
    private static readonly string[] SpanishOnly = ["ción", "¿", "¡"];
    private static readonly string[] PortugueseOnly = ["ção", "ções", "ões", "ã"];

    [Fact]
    public void SpanishAndPortugueseColumnsAreNotSwapped()
    {
        var calls = 0;
        var offenders = new List<string>();
        foreach (var file in new[] { "Localization.cs", "LocalizationErrors.cs" })
        {
            foreach (var columns in LocalizationSource.TranslationCalls(LocalizationSource.CsCore(file), swift: false))
            {
                calls++;
                var es = LocalizationSource.Shape(columns[3]);
                var pt = LocalizationSource.Shape(columns[5]);
                offenders.AddRange(SpanishOnly.Where(pt.Contains).Select(m => $"pt contains Spanish \"{m}\": {pt}"));
                offenders.AddRange(PortugueseOnly.Where(es.Contains).Select(m => $"es contains Portuguese \"{m}\": {es}"));
            }
        }
        Assert.True(calls > 400, "the scanner must actually find the T(...) calls");
        Assert.Empty(offenders);
    }
}

/// <summary>Behavior of the hand-ported (non-table) members of <see cref="L"/>.</summary>
public class LocalizationBehaviorTests
{
    [Fact]
    public void LanguageSelectsItsColumn()
    {
        Assert.Equal("홈", new L(AppLanguage.Ko).Home);
        Assert.Equal("Home", new L(AppLanguage.En).Home);
        Assert.Equal("Главная", new L(AppLanguage.Ru).Home);
        Assert.Equal(AppLanguage.De, new L(AppLanguage.De).Lang);
    }

    /// <summary>Port of AntigravityGroupLocalizationTests.testGroupTitleMapsKnownAPINamesInEveryLanguage (#322).</summary>
    [Fact]
    public void AntigravityGroupTitleMapsKnownApiNamesInEveryLanguage()
    {
        foreach (var lang in Enum.GetValues<AppLanguage>())
        {
            var l = new L(lang);
            Assert.Equal(l.AntigravityGeminiGroup, l.AntigravityGroupTitle("Gemini Models"));
            Assert.Equal(l.AntigravityThirdPartyGroup, l.AntigravityGroupTitle("Claude and GPT models"));
            Assert.Equal(l.AntigravityThirdPartyGroup, l.AntigravityGroupTitle("GPT Models"));
            Assert.Equal(l.AntigravityThirdPartyGroup, l.AntigravityGroupTitle("3P quota"));
            Assert.Equal("Custom Team Cap", l.AntigravityGroupTitle("Custom Team Cap"));
        }
    }

    [Fact]
    public void AntigravityWindowUsesWindowOrBucketId()
    {
        var l = new L(AppLanguage.En);
        Assert.Equal(l.FiveHourSession, l.AntigravityWindow("5h", "x"));
        Assert.Equal(l.FiveHourSession, l.AntigravityWindow(null, "gemini-api-5h"));
        Assert.Equal(l.Weekly, l.AntigravityWindow("weekly", "x"));
        Assert.Equal(l.Weekly, l.AntigravityWindow(null, "claude-api-weekly"));
        Assert.Equal("Limit", l.AntigravityWindow(null, "other"));
    }

    [Fact]
    public void ClaudeLimitEntryNamesKindsAndFallsBackToTheRawKind()
    {
        var l = new L(AppLanguage.En);
        Assert.Equal(l.FiveHourSession, l.ClaudeLimitEntry("session", null));
        Assert.Equal(l.Weekly, l.ClaudeLimitEntry("weekly_all", "opus"));
        Assert.Equal("Weekly (scoped)", l.ClaudeLimitEntry("weekly_scoped", null));
        Assert.Equal("Weekly Opus", l.ClaudeLimitEntry("weekly_scoped", "Opus"));
        Assert.Equal("monthly extra Opus", l.ClaudeLimitEntry("monthly_extra", "Opus"));
        Assert.Equal("limit", l.ClaudeLimitEntry(null, null));
    }

    [Fact]
    public void CodexWindowNamesKnownDurations()
    {
        var l = new L(AppLanguage.En);
        Assert.Equal(l.FiveHourSession, l.CodexWindow(300));
        Assert.Equal(l.Weekly, l.CodexWindow(10_080));
        Assert.Equal("2h", l.CodexWindow(120));
        Assert.Equal("90m", l.CodexWindow(90));
        Assert.Equal("Limit", l.CodexWindow(null));
    }

    [Fact]
    public void PaceDeltaIsNullAtZero()
    {
        var l = new L(AppLanguage.En);
        Assert.Null(l.PaceDelta(0));
        Assert.Equal("3 pts more used", l.PaceDelta(3));
        Assert.Equal("3 pts less used", l.PaceDelta(-3));
    }

    [Fact]
    public void IntervalAndDifficultyFormatting()
    {
        var l = new L(AppLanguage.En);
        Assert.Equal("Manual", l.IntervalLabel(0));
        Assert.Equal("5 min", l.IntervalLabel(300));
        Assert.Equal("100%", l.DifficultyValue(1.0));
        Assert.Equal("5.0%", l.DifficultyValue(0.05));
        Assert.Equal("0.50%", l.DifficultyValue(0.005));
    }

    [Fact]
    public void LimitRefreshHttpErrorSeparatesAuthFailures()
    {
        var l = new L(AppLanguage.En);
        Assert.StartsWith("Claude credential is expired", l.LimitRefreshHTTPError(403));
        Assert.Equal("Failed to fetch Claude limits (500).", l.LimitRefreshHTTPError(500));
    }

    /// <summary>From LanguageSurfaceRegressionTests: move methods are localized, not the raw API slug.</summary>
    [Fact]
    public void MoveMethodsAndStatsAreLocalized()
    {
        foreach (var lang in Enum.GetValues<AppLanguage>())
        {
            var l = new L(lang);
            var labels = new[]
            {
                l.TokenInput, l.TokenOutput, l.TokenCacheWrite, l.TokenCacheRead, l.Website, l.Sponsor,
                l.EvolutionScrollPrevious, l.EvolutionScrollNext,
                l.MoveMethod(new PokemonMoveLearnMethod("light-ball-egg", 0)),
                l.MoveMethod(new PokemonMoveLearnMethod("form-change", 0)),
            };
            Assert.DoesNotContain(labels, string.IsNullOrEmpty);
            Assert.DoesNotContain("light ball egg", labels);
            Assert.DoesNotContain("form change", labels);
        }
        var en = new L(AppLanguage.En);
        Assert.Equal("Lv. 7", en.MoveMethod(new PokemonMoveLearnMethod("level-up", 7)));
        Assert.Equal("Start", en.MoveMethod(new PokemonMoveLearnMethod("level-up", 0)));
        Assert.Equal("TM", en.MoveMethod(new PokemonMoveLearnMethod("machine", 0)));
        Assert.Equal("some new method", en.MoveMethod(new PokemonMoveLearnMethod("some-new-method", 0)));
        Assert.Equal("HP", en.StatLabel("hp"));
        Assert.Equal("Sp. Atk", en.StatLabel("special-attack"));
        Assert.Equal("accuracy", en.StatLabel("accuracy"));
        Assert.Equal("—", en.GenderLabel(null));
        Assert.Equal("Female", en.GenderLabel(PokemonGender.Female));
    }

    [Fact]
    public void EnumLabelsCoverEveryCase()
    {
        foreach (var lang in Enum.GetValues<AppLanguage>())
        {
            var l = new L(lang);
            foreach (var v in Enum.GetValues<PaceTier>()) Assert.NotEmpty(l.PaceTier(v));
            foreach (var v in Enum.GetValues<ProviderStatusIndicator>()) Assert.NotEmpty(l.ProviderStatusLabel(v));
            foreach (var v in Enum.GetValues<RecapScope>())
            {
                Assert.NotEmpty(l.RecapScopeName(v));
                Assert.NotEmpty(l.RecapCompareSoFar(v));
                Assert.NotEmpty(l.RecapCurrentPeriod(v));
            }
            foreach (var v in Enum.GetValues<MenuLimitColorMode>()) Assert.NotEmpty(l.MenuLimitColorMode(v));
            foreach (var v in Enum.GetValues<DexSortOption>()) Assert.NotEmpty(l.Label(v));
            foreach (var v in Enum.GetValues<CatchLogSortOption>()) Assert.NotEmpty(l.Label(v));
            foreach (var v in new[] { "rareCandy", "mint", "shinyCharm" })
            {
                Assert.NotEmpty(l.ItemName(v));
                Assert.NotEmpty(l.ItemDescription(v));
            }
            foreach (var v in new[] { "common", "uncommon", "rare", "legendary" }) Assert.NotEmpty(l.RarityLabel(v));
            foreach (var v in new[] { "notASaveFile", "newerSchema", "fileTooLarge", "backupFailed" }) Assert.NotEmpty(l.ImportErrorMessage(v));
        }
        var en = new L(AppLanguage.En);
        Assert.Equal(en.ThisWeek, en.RecapCurrentPeriod(RecapScope.Week));
        Assert.Equal(en.SortRarity, en.Label(CatchLogSortOption.RarityDesc));
        Assert.Equal(en.ImportErrorBackupFailed, en.ImportErrorMessage("backupFailed"));
        Assert.Throws<ArgumentOutOfRangeException>(() => en.RarityLabel("mythic"));
    }

    [Fact]
    public void EggNamesTreatCommonAsTheBasicEgg()
    {
        var l = new L(AppLanguage.En);
        Assert.Equal("Pokémon Egg", l.EggName(null));
        Assert.Equal("Pokémon Egg", l.EggName("common"));
        Assert.Equal("Rare Egg", l.EggName("rare"));
        Assert.Equal(l.EggDescription(null), l.EggDescription("common"));
        Assert.Contains(l.RarityLegendary, l.EggDescription("legendary"));
        Assert.Equal("Collapsed", l.SnapshotHistoryState(false));
        Assert.Equal("Expanded", l.SnapshotHistoryState(true));
    }
}

/// <summary>Port of LocalizationErrorsTests.swift (Windows exception types stand in for the NSError domains).</summary>
public class LocalizationErrorsTests
{
    [Fact]
    public void ErrorsHaveSelectedLanguageMessagesWithoutSystemDescription()
    {
        var errors = new Exception[]
        {
            new HttpRequestException("RAW SYSTEM MESSAGE"),
            new UnauthorizedAccessException("RAW SYSTEM MESSAGE"),
            new IOException("RAW SYSTEM MESSAGE", unchecked((int)0x80070070)),
            new FileNotFoundException("RAW SYSTEM MESSAGE"),
            new InvalidOperationException("RAW SYSTEM MESSAGE"),
        };
        var languages = Enum.GetValues<AppLanguage>();
        foreach (var error in errors)
        {
            var messages = languages.Select(lang => new L(lang).UserFacingError(error)).ToList();
            Assert.Equal(languages.Length, messages.Distinct().Count());
            Assert.All(messages, m => Assert.False(string.IsNullOrEmpty(m) || m.Contains("RAW SYSTEM MESSAGE")));
        }
        Assert.Contains("네트워크", new L(AppLanguage.Ko).UserFacingError(errors[0]));
        Assert.Contains("access was denied", new L(AppLanguage.En).UserFacingError(errors[1]));
        Assert.Contains("disk space", new L(AppLanguage.En).UserFacingError(errors[2]));
        Assert.Contains("could not be found", new L(AppLanguage.En).UserFacingError(errors[3]));
        Assert.Contains("could not be found", new L(AppLanguage.En).UserFacingError(new DirectoryNotFoundException()));
        Assert.Contains("network request", new L(AppLanguage.En).UserFacingError(new SocketException()));
        Assert.Contains("operation could not be completed", new L(AppLanguage.En).UserFacingError(new IOException("other")));
    }
}

/// <summary>Port of KoreanParticleTests.swift.</summary>
public class KoreanParticleTests
{
    [Fact]
    public void SubjectFollowsFinalConsonant()
    {
        Assert.Equal("유니란이", KoreanParticle.Subject.Attach("유니란"));
        Assert.Equal("피카츄가", KoreanParticle.Subject.Attach("피카츄"));
    }

    [Fact]
    public void ObjectFollowsFinalConsonant()
    {
        Assert.Equal("꼬부기를", KoreanParticle.Object.Attach("꼬부기"));
        Assert.Equal("리자몽을", KoreanParticle.Object.Attach("리자몽"));
    }

    /// <summary>"으로/로": no final consonant and a ㄹ final take the same form — without that branch it reads "알으로".</summary>
    [Fact]
    public void DirectionTreatsRieulLikeNoFinalConsonant()
    {
        Assert.Equal("란쿨루스로", KoreanParticle.Direction.Attach("란쿨루스"));
        Assert.Equal("듀란으로", KoreanParticle.Direction.Attach("듀란"));
        Assert.Equal("희귀 알로", KoreanParticle.Direction.Attach("희귀 알"));
        Assert.Equal("레트라로", KoreanParticle.Direction.Attach("레트라"));
    }

    /// <summary>Endings whose final consonant is unknowable (Unown letters, numbers, Latin, empty) keep both forms.</summary>
    [Fact]
    public void NonHangulEndingKeepsBothForms()
    {
        Assert.Equal("안농 [A]이(가)", KoreanParticle.Subject.Attach("안농 [A]"));
        Assert.Equal("#123을(를)", KoreanParticle.Object.Attach("#123"));
        Assert.Equal("Pikachu(으)로", KoreanParticle.Direction.Attach("Pikachu"));
        Assert.Equal("이(가)", KoreanParticle.Subject.Attach(""));
    }

    [Fact]
    public void KoreanCopyUsesTheMatchingParticle()
    {
        var l = new L(AppLanguage.Ko);
        Assert.Equal("알에서 유니란이 나왔어요!", l.NotifHatchBody("유니란"));
        Assert.Equal("이로치 뚜벅쵸가 태어났어요! (1/48)", l.NotifShinyHatchBody("뚜벅쵸", 48));
        Assert.Equal("솔록이 듀란으로 진화했어요!", l.NotifEvolveBody("솔록", "듀란"));
        Assert.Equal("란쿨루스로 진화했어요!", l.StatusEvolved("란쿨루스"));
        Assert.Equal("레트라를 놓아주고 희귀 알로 바꿀까요?", l.EggConfirm("레트라", l.EggName("rare")));
    }

    [Fact]
    public void OtherLanguagesAreUntouched()
    {
        Assert.Equal("Unfezant hatched from the egg!", new L(AppLanguage.En).NotifHatchBody("Unfezant"));
        Assert.Equal("デスマス が デスカーン に進化しました！", new L(AppLanguage.Ja).NotifEvolveBody("デスマス", "デスカーン"));
    }
}

/// <summary>Port of AppLanguageTests (CompanionTests.swift) and the language half of PokemonNameLocalizationTests.swift.</summary>
public class AppLanguageTests
{
    [Fact]
    public void LanguageMetadata()
    {
        Assert.Equal("de", AppLanguage.De.RawValue());
        Assert.Equal(["de"], AppLanguage.De.ApiCodes());
        Assert.Equal("Deutsch", AppLanguage.De.Label());
        Assert.Equal("ru", AppLanguage.Ru.RawValue());
        Assert.Equal(["ru"], AppLanguage.Ru.ApiCodes());
        Assert.Equal("Русский", AppLanguage.Ru.Label());
        Assert.Equal(["pt-br", "pt"], AppLanguage.Pt.ApiCodes());
        Assert.Equal(["ja-hrkt", "ja"], AppLanguage.Ja.ApiCodes());
        Assert.Equal("ja", AppLanguage.Ja.DisplayCulture().Name);
    }

    [Fact]
    public void RawValueRoundTrips()
    {
        foreach (var lang in Enum.GetValues<AppLanguage>()) Assert.Equal(lang, AppLanguageExtensions.FromRawValue(lang.RawValue()));
        Assert.Null(AppLanguageExtensions.FromRawValue("it"));
        Assert.Null(AppLanguageExtensions.FromRawValue(null));
    }

    [Fact]
    public void PreferredLanguagesMapToSupportedLanguagesOrEnglish()
    {
        foreach (var id in new[] { "de", "de-DE", "de-AT", "DE-de" }) Assert.Equal(AppLanguage.De, AppLanguageExtensions.SystemDefault(id));
        foreach (var id in new[] { "ru", "ru-RU", "ru-KZ", "RU-ru" }) Assert.Equal(AppLanguage.Ru, AppLanguageExtensions.SystemDefault(id));
        Assert.Equal(AppLanguage.Ko, AppLanguageExtensions.SystemDefault("ko-KR"));
        Assert.Equal(AppLanguage.Pt, AppLanguageExtensions.SystemDefault("pt-BR"));
        Assert.Equal(AppLanguage.En, AppLanguageExtensions.SystemDefault("it-IT"));
        Assert.Equal(AppLanguage.En, AppLanguageExtensions.SystemDefault("uk-UA"));
        Assert.Equal(AppLanguage.En, AppLanguageExtensions.SystemDefault("k"));
        Assert.Equal(AppLanguage.En, AppLanguageExtensions.SystemDefault(""));
        Assert.Equal(AppLanguage.En, AppLanguageExtensions.SystemDefault(null));
        Assert.Contains(AppLanguageExtensions.SystemDefault(), Enum.GetValues<AppLanguage>());
    }

    [Fact]
    public void SelectedLanguageThenEnglishWithCurrentAndLegacyCodes()
    {
        var names = new Dictionary<string, string>
        {
            ["en"] = "Tackle", ["ko"] = "몸통박치기", ["ja-Hrkt"] = "たいあたり", ["ja"] = "体当たり", ["pt_BR"] = "Investida",
        };
        Assert.Equal("몸통박치기", AppLanguage.Ko.ResolveName(names));
        Assert.Equal("たいあたり", AppLanguage.Ja.ResolveName(names));
        Assert.Equal("Investida", AppLanguage.Pt.ResolveName(names));
        Assert.Equal("Tackle", AppLanguage.De.ResolveName(names));
        Assert.Equal("Tackle", AppLanguage.Ko.ResolveName(new Dictionary<string, string> { ["ko"] = " \n", ["en"] = " Tackle " }));
        Assert.Null(AppLanguage.Ko.ResolveName(new Dictionary<string, string> { ["fr"] = "Charge" }));
    }
}

/// <summary>Port of the pure parts of PokemonNameLocalizationTests.swift (the cache client is not ported here).</summary>
public class PokemonNameLocalizationTests
{
    [Fact]
    public void ParserPreservesFutureLanguagesAndIgnoresEmptyValues()
    {
        var names = PokemonNameLocalization.Collect(
        [
            ("ko", "몸통박치기"), ("en", "Tackle"), ("it", "Azione"), ("pt_BR", "Investida"), ("", "Bad"), ("fr", " "),
        ]);
        Assert.Equal("Azione", names["it"]);
        Assert.Equal("Investida", names["pt-br"]);
        Assert.False(names.ContainsKey(""));
        Assert.False(names.ContainsKey("fr"));
        Assert.Equal("Azione", PokemonNameLocalization.Resolve(names, ["it"]));
        Assert.Equal("Tackle", PokemonNameLocalization.Resolve(names, ["not-yet-supported"]));
    }

    [Fact]
    public void IdentifierTitleCasesSlugParts()
    {
        Assert.Equal("Sky Forme", PokemonNameLocalization.Identifier("sky-forme"));
        Assert.Equal("Mr Mime", PokemonNameLocalization.Identifier("mr--mime"));
        Assert.Equal("", PokemonNameLocalization.Identifier(""));
    }

    [Fact]
    public void ResourceKeysAndValidation()
    {
        var form = new PokemonNameResource(PokemonNameResource.ResourceKind.PokemonForm, "shaymin-sky");
        Assert.Equal("pokemon-form-shaymin-sky", form.CacheKey);
        Assert.NotEqual(new PokemonNameResource(PokemonNameResource.ResourceKind.Type, "grass").CacheKey,
            new PokemonNameResource(PokemonNameResource.ResourceKind.Ability, "grass").CacheKey);
        Assert.True(form.IsValid);
        Assert.False(new PokemonNameResource(PokemonNameResource.ResourceKind.Move, "").IsValid);
        Assert.False(new PokemonNameResource(PokemonNameResource.ResourceKind.Move, "Tackle").IsValid);
        Assert.False(new PokemonNameResource(PokemonNameResource.ResourceKind.Move, "../x").IsValid);
    }
}

/// <summary>
/// Port of LocalizedUILiteralTests.swift: user-facing copy must go through <see cref="L"/>, so no Hangul string
/// literal may appear in the WPF app sources (Korean comments are allowed — only literals are offenders).
/// </summary>
public class LocalizedUILiteralTests
{
    [Fact]
    public void NoHangulStringLiteralsInAppSources()
    {
        var app = Path.Combine(LocalizationSource.WindowsRoot(), "src", "PokeTokenBar.App");
        var offenders = new List<string>();
        var files = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml"))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                // Render-harness fixtures (sample Pokémon names), never shown by the installed app.
                && !f.EndsWith("Samples.cs"))
            .ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // A branch keyed by AppLanguage.Ko is one row of a per-language table (date/unit wording that
                // Foundation formatters supply on macOS and .NET does not): every language has its own row.
                if (lines[i].Contains("AppLanguage.Ko =>")) continue;
                var offends = file.EndsWith(".xaml") ? HasHangulOutsideXmlComment(lines[i]) : HasHangulStringLiteral(lines[i]);
                if (offends) offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }
        Assert.True(offenders.Count == 0,
            "User-facing copy must be routed through L so every AppLanguage gets it: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ScannerSeparatesLiteralsFromComments()
    {
        Assert.True(HasHangulStringLiteral("Text = \"홈\";"));
        Assert.False(HasHangulStringLiteral("Text = l.Home; // 홈"));
        Assert.False(HasHangulStringLiteral("var url = \"https://x\"; // 홈"));
        Assert.True(HasHangulStringLiteral("Text = \"a \\\" 홈\";"));
        Assert.True(HasHangulOutsideXmlComment("<TextBlock Text=\"홈\"/>"));
        Assert.False(HasHangulOutsideXmlComment("<!-- 홈 -->"));
    }

    /// <summary>Character walk, not a regex: it must tell a <c>//</c> inside a literal from a trailing comment.</summary>
    private static bool HasHangulStringLiteral(string line)
    {
        var inString = false;
        var escaped = false;
        var previous = '\0';
        foreach (var c in line)
        {
            if (escaped) { escaped = false; previous = c; continue; }
            if (c == '\\' && inString) { escaped = true; previous = c; continue; }
            if (c == '"') { inString = !inString; previous = c; continue; }
            if (!inString && c == '/' && previous == '/') return false; // trailing comment
            if (inString && IsHangul(c)) return true;
            previous = c;
        }
        return false;
    }

    private static bool HasHangulOutsideXmlComment(string line)
    {
        var withoutComments = System.Text.RegularExpressions.Regex.Replace(line, "<!--.*?-->", "");
        return withoutComments.Any(IsHangul);
    }

    private static bool IsHangul(char c) =>
        c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';
}
