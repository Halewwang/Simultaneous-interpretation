using System.Xml.Linq;
using EMKE.Windows.App.Localization;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class LocalizationTests
{
    [TestMethod]
    public void InvariantAndSimplifiedChineseResourcesHaveIdenticalNonEmptyKeys()
    {
        Dictionary<string, string> english =
            ReadResource("Strings.resx");
        Dictionary<string, string> simplifiedChinese =
            ReadResource("Strings.zh-CN.resx");

        CollectionAssert.AreEquivalent(
            english.Keys.ToArray(),
            simplifiedChinese.Keys.ToArray());
        Assert.IsTrue(english.Values.All(static value =>
            !string.IsNullOrWhiteSpace(value)));
        Assert.IsTrue(simplifiedChinese.Values.All(static value =>
            !string.IsNullOrWhiteSpace(value)));
    }

    [TestMethod]
    public void ResourceKeysDoNotEncodeTranslationLanguageVariants()
    {
        Dictionary<string, string> english =
            ReadResource("Strings.resx");

        string[] forbiddenSuffixes =
        [
            ".en",
            ".english",
            ".zh",
            ".zh-CN",
            ".zhHans",
            "_en",
            "_english",
            "_zh",
            "_zh-CN",
            "_zhHans",
        ];

        Assert.IsFalse(english.Keys.Any(key =>
            forbiddenSuffixes.Any(suffix =>
                key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))));
    }

    [TestMethod]
    public void InvariantResourceIsEnglishAndSatelliteIsSimplifiedChinese()
    {
        Dictionary<string, string> english =
            ReadResource("Strings.resx");
        Dictionary<string, string> simplifiedChinese =
            ReadResource("Strings.zh-CN.resx");

        Assert.AreEqual("Start", english["ActionStart"]);
        Assert.AreEqual("开始", simplifiedChinese["ActionStart"]);
        Assert.AreEqual("Translation stopped", english["RuntimeStopped"]);
        Assert.AreEqual("翻译已停止", simplifiedChinese["RuntimeStopped"]);
    }

    [TestMethod]
    public void InterfaceLanguageStableValuesAreExact()
    {
        Assert.AreEqual(
            "system",
            AppInterfaceLanguage.System.ToStableValue());
        Assert.AreEqual(
            "zhHans",
            AppInterfaceLanguage.ZhHans.ToStableValue());
        Assert.AreEqual(
            "english",
            AppInterfaceLanguage.English.ToStableValue());
        Assert.AreEqual(
            AppInterfaceLanguage.ZhHans,
            AppInterfaceLanguageExtensions.ParseStableValue("zhHans"));
    }

    [DataRow("zh-Hans", "zh-CN")]
    [DataRow("zh-CN", "zh-CN")]
    [DataRow("zh-SG", "zh-CN")]
    [DataRow("en-US", "")]
    [DataRow("zh-Hant", "")]
    [DataRow("fr-FR", "")]
    [TestMethod]
    public void SystemLanguageUsesOnlySupportedSimplifiedChineseCultures(
        string systemCulture,
        string expectedResourceCulture)
    {
        Assert.AreEqual(
            expectedResourceCulture,
            LocalizationService.ResolveResourceCulture(
                AppInterfaceLanguage.System,
                System.Globalization.CultureInfo.GetCultureInfo(systemCulture))
                .Name);
    }

    [TestMethod]
    public void ExplicitInterfaceLanguageOverridesSystemCulture()
    {
        LocalizationService localization = new(
            () => System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

        Assert.AreEqual(
            "开始",
            localization.Get(
                LocalizedString.ActionStart,
                AppInterfaceLanguage.ZhHans));
        Assert.AreEqual(
            "Start",
            localization.Get(
                LocalizedString.ActionStart,
                AppInterfaceLanguage.English));
    }

    [TestMethod]
    public void ChangingInterfaceLanguageNotifiesOpenPresentersOnce()
    {
        LocalizationService localization = new(
            () => System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        List<AppInterfaceLanguage> observed = [];
        localization.LanguageChanged += (_, eventArgs) =>
            observed.Add(eventArgs.Language);

        localization.ChangeLanguage(AppInterfaceLanguage.ZhHans);
        localization.ChangeLanguage(AppInterfaceLanguage.ZhHans);
        localization.ChangeLanguage(AppInterfaceLanguage.English);

        CollectionAssert.AreEqual(
            new[]
            {
                AppInterfaceLanguage.ZhHans,
                AppInterfaceLanguage.English,
            },
            observed);
    }

    private static Dictionary<string, string> ReadResource(string fileName)
    {
        string path = TestSourceLocator.Find(
            Path.Combine("Localization", fileName));
        XDocument document = XDocument.Load(path);

        return document
            .Root!
            .Elements("data")
            .ToDictionary(
                element => (string)element.Attribute("name")!,
                element => (string?)element.Element("value") ?? string.Empty,
                StringComparer.Ordinal);
    }
}
