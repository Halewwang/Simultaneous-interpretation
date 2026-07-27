using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EMKE.Core;

namespace EMKE.Routing;

public sealed class OfflineLanguageClassifier : ILanguageClassifier
{
    private const int MinimumEvidenceLetters = 4;
    private const int MaximumNormalizedRunes = 4_096;
    private const double SoftmaxScale = 2;
    private const double UnknownFeatureProbability = 0.000_000_01;
    private const string ResourceName =
        "EMKE.Routing.Resources.language-profile-v1.json";

    private static readonly Lazy<LanguageModel> SharedModel =
        new(LoadModel, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly LanguageModel _model;

    public OfflineLanguageClassifier()
    {
        _model = SharedModel.Value;
    }

    public int ProfileVersion => _model.Version;

    public string GeneratorVersion => _model.GeneratorVersion;

    public string FeatureSha256 => _model.FeatureSha256;

    public ValueTask<LanguageProbabilities> ClassifyAsync(
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        NormalizedText normalized = Normalize(text);
        if (normalized.LetterCount < MinimumEvidenceLetters)
        {
            return ValueTask.FromResult(
                new LanguageProbabilities(1d / 3, 1d / 3, 1d / 3));
        }

        double hanRatio = normalized.HanCount / (double)normalized.LetterCount;
        double latinRatio = normalized.LatinCount / (double)normalized.LetterCount;
        double zhScore =
            ScoreLanguage(normalized.Characters, _model.Zh) + (3 * hanRatio);
        double enScore =
            ScoreLanguage(normalized.Characters, _model.En) + (0.2 * latinRatio);
        double deScore =
            ScoreLanguage(normalized.Characters, _model.De) + (0.2 * latinRatio);
        return ValueTask.FromResult(ToProbabilities(zhScore, enScore, deScore));
    }

    private static LanguageProbabilities ToProbabilities(
        double zhScore,
        double enScore,
        double deScore)
    {
        double maximum = Math.Max(zhScore, Math.Max(enScore, deScore));
        double zh = Math.Exp((zhScore - maximum) * SoftmaxScale);
        double en = Math.Exp((enScore - maximum) * SoftmaxScale);
        double de = Math.Exp((deScore - maximum) * SoftmaxScale);
        double sum = zh + en + de;
        return new LanguageProbabilities(zh / sum, en / sum, de / sum);
    }

    private static double ScoreLanguage(
        IReadOnlyList<string> characters,
        FrozenDictionary<string, double> profile)
    {
        double weightedScore = 0;
        double totalWeight = 0;
        for (int width = 1; width <= 3; width++)
        {
            if (characters.Count < width)
            {
                continue;
            }

            double widthScore = 0;
            int featureCount = 0;
            for (int index = 0; index + width <= characters.Count; index++)
            {
                string feature = width switch
                {
                    1 => characters[index],
                    2 => string.Concat(
                        characters[index],
                        characters[index + 1]),
                    3 => string.Concat(
                        characters[index],
                        characters[index + 1],
                        characters[index + 2]),
                    _ => throw new InvalidOperationException(),
                };
                double probability = profile.GetValueOrDefault(
                    feature,
                    UnknownFeatureProbability);
                widthScore += Math.Log(probability);
                featureCount++;
            }

            weightedScore += width * (widthScore / featureCount);
            totalWeight += width;
        }

        return weightedScore / totalWeight;
    }

    private static NormalizedText Normalize(string text)
    {
#pragma warning disable CA1308 // Generator and runtime share lowercase feature normalization.
        string normalized = text.Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant();
#pragma warning restore CA1308
        List<string> characters = new(
            Math.Min(normalized.Length, MaximumNormalizedRunes));
        bool previousWasSpace = true;
        int letters = 0;
        int han = 0;
        int latin = 0;

        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (characters.Count >= MaximumNormalizedRunes)
            {
                break;
            }

            if (IsLetter(rune))
            {
                characters.Add(rune.ToString());
                previousWasSpace = false;
                letters++;
                if (IsHan(rune))
                {
                    han++;
                }

                if (IsLatin(rune))
                {
                    latin++;
                }
            }
            else if (!previousWasSpace)
            {
                characters.Add(" ");
                previousWasSpace = true;
            }
        }

        if (characters.Count > 0
            && string.Equals(characters[^1], " ", StringComparison.Ordinal))
        {
            characters.RemoveAt(characters.Count - 1);
        }

        return new NormalizedText(characters, letters, han, latin);
    }

    private static bool IsLetter(Rune rune)
    {
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter;
    }

    private static bool IsHan(Rune rune)
    {
        int value = rune.Value;
        return value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x3134F;
    }

    private static bool IsLatin(Rune rune)
    {
        int value = rune.Value;
        return value is >= 0x0041 and <= 0x024F
            or >= 0x1E00 and <= 0x1EFF;
    }

    private static LanguageModel LoadModel()
    {
        Assembly assembly = typeof(OfflineLanguageClassifier).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(
                "The embedded language profile is missing.");
        LanguageProfileDocument document = JsonSerializer.Deserialize(
            stream,
            LanguageProfileJsonContext.Default.LanguageProfileDocument)
            ?? throw new InvalidDataException(
                "The embedded language profile is empty.");

        if (document.Version != 1
            || !string.Equals(
                document.FeatureKind,
                "normalized-character-1-to-3-grams",
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(document.GeneratorVersion)
            || document.FeatureSha256.Length != 64
            || document.Profiles.Count != 3
            || !document.Profiles.TryGetValue("zh", out Dictionary<string, double>? zh)
            || !document.Profiles.TryGetValue("en", out Dictionary<string, double>? en)
            || !document.Profiles.TryGetValue("de", out Dictionary<string, double>? de))
        {
            throw new InvalidDataException(
                "The embedded language profile metadata is invalid.");
        }

        return new LanguageModel(
            document.Version,
            document.GeneratorVersion,
            document.FeatureSha256,
            Freeze(zh),
            Freeze(en),
            Freeze(de));
    }

    private static FrozenDictionary<string, double> Freeze(
        Dictionary<string, double> values)
    {
        if (values.Count == 0
            || values.Any(static pair =>
                string.IsNullOrEmpty(pair.Key)
                || !double.IsFinite(pair.Value)
                || pair.Value < 0))
        {
            throw new InvalidDataException(
                "The embedded language profile contains invalid features.");
        }

        return values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private sealed record NormalizedText(
        IReadOnlyList<string> Characters,
        int LetterCount,
        int HanCount,
        int LatinCount);

    private sealed record LanguageModel(
        int Version,
        string GeneratorVersion,
        string FeatureSha256,
        FrozenDictionary<string, double> Zh,
        FrozenDictionary<string, double> En,
        FrozenDictionary<string, double> De);
}

internal sealed class LanguageProfileDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("generatorVersion")]
    public string GeneratorVersion { get; init; } = string.Empty;

    [JsonPropertyName("featureKind")]
    public string FeatureKind { get; init; } = string.Empty;

    [JsonPropertyName("featureSha256")]
    public string FeatureSha256 { get; init; } = string.Empty;

    [JsonPropertyName("sources")]
    public List<LanguageProfileSource> Sources { get; init; } = [];

    [JsonPropertyName("profiles")]
    public Dictionary<string, Dictionary<string, double>> Profiles { get; init; } =
        new(StringComparer.Ordinal);
}

internal sealed class LanguageProfileSource
{
    [JsonPropertyName("language")]
    public string Language { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("license")]
    public string License { get; init; } = string.Empty;

    [JsonPropertyName("licenseUrl")]
    public string LicenseUrl { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LanguageProfileDocument))]
internal sealed partial class LanguageProfileJsonContext : JsonSerializerContext;
