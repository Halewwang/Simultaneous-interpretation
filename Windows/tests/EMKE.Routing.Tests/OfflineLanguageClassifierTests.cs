using EMKE.Core;
using System.Text.Json;

namespace EMKE.Routing.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class OfflineLanguageClassifierTests
{
    [TestMethod]
    [DataRow("我们今天讨论产品发布计划和客户反馈。", LanguageCode.Zh)]
    [DataRow("We are reviewing the release plan and customer feedback today.", LanguageCode.En)]
    [DataRow("Wir besprechen heute den Veröffentlichungsplan und die Rückmeldungen.", LanguageCode.De)]
    public async Task ClearLanguageEvidenceHasTheExpectedStrongestProbability(
        string text,
        LanguageCode expected)
    {
        OfflineLanguageClassifier classifier = new();

        LanguageProbabilities result =
            await classifier.ClassifyAsync(text, CancellationToken.None);

        Assert.AreEqual(
            expected,
            new[]
            {
                (Language: LanguageCode.Zh, Probability: result.Zh),
                (Language: LanguageCode.En, Probability: result.En),
                (Language: LanguageCode.De, Probability: result.De),
            }.MaxBy(static item => item.Probability).Language);
        Assert.AreEqual(1, result.Zh + result.En + result.De, 0.000_000_001);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("a")]
    [DataRow("ok")]
    [DataRow("123 !")]
    public async Task LowEvidenceNeverCrossesEitherRoutingThreshold(string text)
    {
        OfflineLanguageClassifier classifier = new();

        LanguageProbabilities result =
            await classifier.ClassifyAsync(text, CancellationToken.None);

        Assert.IsLessThan(0.75, result.Zh);
        Assert.IsLessThan(0.60, result.En);
        Assert.IsLessThan(0.60, result.De);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task EmbeddedProfileLoadsOutsideTheRepositoryWorkingDirectory()
    {
        string originalDirectory = Environment.CurrentDirectory;
        string temporaryDirectory = Directory.CreateTempSubdirectory("emke-routing-").FullName;
        try
        {
            Environment.CurrentDirectory = temporaryDirectory;
            OfflineLanguageClassifier classifier = new();
            LanguageProbabilities result =
                await classifier.ClassifyAsync(
                    "Das eingebettete Sprachprofil benötigt kein Arbeitsverzeichnis.",
                    CancellationToken.None);

            Assert.IsGreaterThan(result.En, result.De);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(temporaryDirectory);
        }
    }

    [TestMethod]
    public async Task ClassifierIsImmutableAndSafeForConcurrentCalls()
    {
        OfflineLanguageClassifier classifier = new();
        Task<LanguageProbabilities>[] calls = Enumerable.Range(0, 64)
            .Select(index => classifier.ClassifyAsync(
                index % 2 == 0
                    ? "This sentence contains stable English language evidence."
                    : "Dieser Satz enthält stabile deutsche Sprachmerkmale.",
                CancellationToken.None).AsTask())
            .ToArray();

        LanguageProbabilities[] results = await Task.WhenAll(calls);

        Assert.IsTrue(results
            .Where((_, index) => index % 2 == 0)
            .All(static result => result.En > result.De));
        Assert.IsTrue(results
            .Where((_, index) => index % 2 != 0)
            .All(static result => result.De > result.En));
    }

    [TestMethod]
    public async Task WindowsFinalRoutesAgreeWithRecordedMacOSBaselineAtLeast99Percent()
    {
        using JsonDocument corpus = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                FindLanguageCorpus(),
                CancellationToken.None));
        JsonElement cases = corpus.RootElement.GetProperty("cases");
        OfflineLanguageClassifier classifier = new();
        int agreements = 0;
        int rawClassifierDisagreements = 0;

        foreach (JsonElement testCase in cases.EnumerateArray())
        {
            LanguageCode native = ParseLanguage(
                testCase.GetProperty("nativeLanguage").GetString()!);
            LanguageProbabilities probabilities =
                await classifier.ClassifyAsync(
                    testCase.GetProperty("text").GetString()!,
                    CancellationToken.None);
            Dictionary<string, double> confidence = new(StringComparer.Ordinal)
            {
                ["zh"] = probabilities.Zh,
                ["en"] = probabilities.En,
                ["de"] = probabilities.De,
            };
            InboundLanguageGate gate = new(native, new FixedCorpusClock());
            string windowsDecision = Stable(gate.Observe(confidence).Decision);
            string macOSDecision =
                testCase.GetProperty("macOSBaselineDecision").GetString()!;
            string expectation =
                testCase.GetProperty("expectedFinalRoute").GetString()!;

            Assert.AreEqual(expectation, macOSDecision, testCase.GetProperty("id").GetString());
            if (string.Equals(windowsDecision, macOSDecision, StringComparison.Ordinal))
            {
                agreements++;
            }
            else
            {
                Assert.AreEqual(
                    "undecided",
                    windowsDecision,
                    $"A cross-platform route disagreement escaped the Windows confidence gate for {testCase.GetProperty("id").GetString()}.");
            }

            string windowsPrimary = Stable(new[]
            {
                (Language: LanguageCode.Zh, Probability: probabilities.Zh),
                (Language: LanguageCode.En, Probability: probabilities.En),
                (Language: LanguageCode.De, Probability: probabilities.De),
            }.MaxBy(static item => item.Probability).Language);
            string? macOSPrimary =
                testCase.TryGetProperty(
                    "macOSPrimaryLanguage",
                    out JsonElement primary)
                && primary.ValueKind == JsonValueKind.String
                    ? primary.GetString()
                    : null;
            if (!string.Equals(
                    windowsPrimary,
                    macOSPrimary,
                    StringComparison.Ordinal))
            {
                rawClassifierDisagreements++;
                Assert.AreEqual(
                    "undecided",
                    windowsDecision,
                    $"Raw classifier disagreement escaped the gate for {testCase.GetProperty("id").GetString()}.");
            }

            if (testCase.GetProperty("category").GetString() == "ambiguous")
            {
                Assert.AreEqual("undecided", windowsDecision);
                Assert.AreEqual("undecided", macOSDecision);
            }
        }

        Assert.IsGreaterThanOrEqualTo(
            0.99,
            agreements / (double)cases.GetArrayLength());
        Assert.AreEqual(60, rawClassifierDisagreements);
    }

    private static string FindLanguageCorpus()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        for (int level = 0; level <= 8 && current is not null; level++)
        {
            string candidate = Path.Combine(
                current.FullName,
                "Shared",
                "TestVectors",
                "Routing",
                "LanguageCorpus",
                "language-corpus-v1.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            "Unable to locate the shared routing language corpus.");
    }

    private static LanguageCode ParseLanguage(string value)
    {
        return value switch
        {
            "zh" => LanguageCode.Zh,
            "en" => LanguageCode.En,
            "de" => LanguageCode.De,
            _ => throw new InvalidDataException("Unknown corpus language."),
        };
    }

    private static string Stable(InboundGateDecision value)
    {
        return value switch
        {
            InboundGateDecision.Undecided => "undecided",
            InboundGateDecision.Original => "original",
            InboundGateDecision.Translated => "translated",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private static string Stable(LanguageCode value)
    {
        return value switch
        {
            LanguageCode.Zh => "zh",
            LanguageCode.En => "en",
            LanguageCode.De => "de",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private sealed class FixedCorpusClock : IClock
    {
        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}

#pragma warning restore CA2007
#pragma warning restore CA1515
