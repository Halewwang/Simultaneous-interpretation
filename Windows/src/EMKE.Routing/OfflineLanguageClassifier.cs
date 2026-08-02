using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
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
    private const string ExpectedFeatureSha256 =
        "a64f8e589f873628197ebcb3efbc5c09b031d3190626697c49d12be86ca7a603";
    private const string ResourceName =
        "EMKE.Routing.Resources.language-profile-v1.json";
    private static readonly string[] ProfileLanguages = ["zh", "en", "de"];

    private static readonly Lazy<LanguageModel> SharedModel =
        new(LoadModel, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly LanguageModel _model;

    public OfflineLanguageClassifier()
    {
        _model = SharedModel.Value;
    }

    internal OfflineLanguageClassifier(Func<Stream?> openProfile)
    {
        ArgumentNullException.ThrowIfNull(openProfile);
        _model = LoadModel(openProfile);
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
        return LoadModel(
            () => assembly.GetManifestResourceStream(ResourceName));
    }

    private static LanguageModel LoadModel(Func<Stream?> openProfile)
    {
        using Stream stream = openProfile()
            ?? throw new InvalidDataException(
                "The embedded language profile is missing.");
        return ParseModel(stream);
    }

    private static LanguageModel ParseModel(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        JsonDocument raw;
        try
        {
            raw = JsonDocument.Parse(stream);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The embedded language profile is invalid.",
                exception);
        }

        using (raw)
        {
            return ParseModelDocument(raw);
        }
    }

    private static LanguageModel ParseModelDocument(JsonDocument raw)
    {
        LanguageProfileDocument document = raw.RootElement.Deserialize(
            LanguageProfileJsonContext.Default.LanguageProfileDocument)
            ?? throw new InvalidDataException(
                "The embedded language profile is empty.");

        if (document.Version != 1
            || !string.Equals(
                document.FeatureKind,
                "normalized-character-1-to-3-grams",
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(document.GeneratorVersion)
            || document.Profiles.Count != 3
            || !document.Profiles.TryGetValue("zh", out Dictionary<string, double>? zh)
            || !document.Profiles.TryGetValue("en", out Dictionary<string, double>? en)
            || !document.Profiles.TryGetValue("de", out Dictionary<string, double>? de))
        {
            throw new InvalidDataException(
                "The embedded language profile metadata is invalid.");
        }

        ValidateFeatureHashFormat(document.FeatureSha256);
        JsonElement rawProfiles = raw.RootElement.GetProperty("profiles");
        ValidateRawProfiles(rawProfiles);
        ValidateProfileIntegrity(rawProfiles, document.FeatureSha256);

        return new LanguageModel(
            document.Version,
            document.GeneratorVersion,
            document.FeatureSha256,
            Freeze(zh),
            Freeze(en),
            Freeze(de));
    }

    private static void ValidateFeatureHashFormat(string hash)
    {
        if (hash.Length != 64
            || hash.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "The embedded featureSha256 must be 64 lowercase hexadecimal characters.");
        }
    }

    private static void ValidateRawProfiles(JsonElement profiles)
    {
        if (profiles.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The embedded language profile contains invalid features.");
        }

        string[] languages = profiles.EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        if (languages.Length != ProfileLanguages.Length
            || languages.Distinct(StringComparer.Ordinal).Count()
                != ProfileLanguages.Length
            || ProfileLanguages.Any(language =>
                !languages.Contains(language, StringComparer.Ordinal)))
        {
            throw new InvalidDataException(
                "The embedded language profile contains invalid features.");
        }

        foreach (string language in ProfileLanguages)
        {
            JsonElement profile = profiles.GetProperty(language);
            if (profile.ValueKind != JsonValueKind.Object
                || !profile.EnumerateObject().Any())
            {
                throw new InvalidDataException(
                    "The embedded language profile contains invalid features.");
            }

            HashSet<string> unique = new(StringComparer.Ordinal);
            foreach (JsonProperty feature in profile.EnumerateObject())
            {
                int width = feature.Name.EnumerateRunes().Count();
                if (!unique.Add(feature.Name)
                    || width is < 1 or > 3
                    || feature.Value.ValueKind != JsonValueKind.Number
                    || !feature.Value.TryGetDouble(out double probability)
                    || !double.IsFinite(probability)
                    || probability <= 0
                    || probability > 1)
                {
                    throw new InvalidDataException(
                        "The embedded language profile contains invalid features.");
                }
            }
        }
    }

    private static void ValidateProfileIntegrity(
        JsonElement profiles,
        string declaredHash)
    {
        byte[] canonical = CanonicalizeProfiles(profiles);
        byte[] computedHash = SHA256.HashData(canonical);
        byte[] declaredHashBytes = Convert.FromHexString(declaredHash);
        byte[] expectedHashBytes = Convert.FromHexString(
            ExpectedFeatureSha256);
        if (!CryptographicOperations.FixedTimeEquals(
                computedHash,
                declaredHashBytes)
            || !CryptographicOperations.FixedTimeEquals(
                computedHash,
                expectedHashBytes))
        {
            throw new InvalidDataException(
                "The embedded language profile failed canonical integrity validation.");
        }
    }

    private static byte[] CanonicalizeProfiles(JsonElement profiles)
    {
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(
            output,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
        {
            writer.WriteStartObject();
            foreach (string language in ProfileLanguages)
            {
                writer.WritePropertyName(language);
                writer.WriteStartObject();
                foreach (JsonProperty feature in profiles.GetProperty(language)
                             .EnumerateObject()
                             .OrderBy(
                                 static property => property.Name,
                                 UnicodeCodePointComparer.Instance))
                {
                    writer.WritePropertyName(feature.Name);
                    writer.WriteRawValue(
                        feature.Value.GetRawText(),
                        skipInputValidation: false);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return output.ToArray();
    }

    private static FrozenDictionary<string, double> Freeze(
        Dictionary<string, double> values)
    {
        if (values.Count == 0
            || values.Any(static pair =>
                pair.Key.EnumerateRunes().Count() is < 1 or > 3
                || !double.IsFinite(pair.Value)
                || pair.Value <= 0
                || pair.Value > 1))
        {
            throw new InvalidDataException(
                "The embedded language profile contains invalid features.");
        }

        return values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private sealed class UnicodeCodePointComparer : IComparer<string>
    {
        public static UnicodeCodePointComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var leftRunes = left.EnumerateRunes().GetEnumerator();
            var rightRunes = right.EnumerateRunes().GetEnumerator();
            while (true)
            {
                bool hasLeft = leftRunes.MoveNext();
                bool hasRight = rightRunes.MoveNext();
                if (!hasLeft || !hasRight)
                {
                    return hasLeft.CompareTo(hasRight);
                }

                int comparison =
                    leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
        }
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
