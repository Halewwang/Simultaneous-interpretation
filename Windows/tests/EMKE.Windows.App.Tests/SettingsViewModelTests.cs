using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using EMKE.Core;
using EMKE.Platform.Settings;
using EMKE.Windows.App.Commands;
using EMKE.Windows.App.Localization;
using EMKE.Windows.App.Settings;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest provides no UI synchronization context.

[TestClass]
public sealed class SettingsViewModelTests
{
    private static readonly string[] SaveOperations =
        ["secret.save", "settings.save"];
    private static readonly string[] TestOperations =
        ["secret.save", "settings.save", "connection.test"];
    private static readonly string[] StartOperations =
        ["secret.save", "settings.save", "runtime.start"];
    private static readonly string[] SettingsSectionBindings =
    [
        "{Binding ServiceSectionLabel}",
        "{Binding TranslationSectionLabel}",
        "{Binding AudioSectionLabel}",
        "{Binding AppearanceSectionLabel}",
        "{Binding SystemSectionLabel}",
    ];
    private static readonly string[] CloseOperations = ["clear", "hide"];
    private static readonly string[] EnglishTranslationOptions =
        ["Simplified Chinese", "English", "German"];
    private static readonly string[] EnglishInterfaceOptions =
        ["Follow system", "Simplified Chinese", "English"];
    private static readonly string[] ChineseTranslationOptions =
        ["简体中文", "英语", "德语"];
    private static readonly string[] ChineseInterfaceOptions =
        ["跟随系统", "简体中文", "英语"];
    private static readonly string[] PrivacyPreferences = ["privacy-v2"];

    [TestMethod]
    public async Task SavePersistsWhitelistedSettingsAndKeyThenClearsDraft()
    {
        SettingsFixture fixture = new();
        fixture.ViewModel.BaseAddress = "https://example.test/realtime";
        fixture.ViewModel.ModelId = "new-model";
        fixture.ViewModel.NativeLanguage = LanguageCode.En;
        fixture.ViewModel.MeetingLanguage = LanguageCode.De;
        fixture.ViewModel.ReplaceApiKeyDraft(
            "temporary-api-key".AsSpan());
        int clearRequests = 0;
        fixture.ViewModel.ApiKeyClearRequested += (_, _) => clearRequests++;

        await fixture.ViewModel.SaveAsync(CancellationToken.None);

        Assert.IsNotNull(fixture.Settings.Saved);
        Assert.AreEqual(
            "https://example.test/realtime",
            fixture.Settings.Saved.BaseUri.AbsoluteUri);
        Assert.AreEqual("new-model", fixture.Settings.Saved.ModelId);
        Assert.AreEqual(LanguageCode.En, fixture.Settings.Saved.NativeLanguage);
        Assert.AreEqual(LanguageCode.De, fixture.Settings.Saved.MeetingLanguage);
        CollectionAssert.AreEqual(
            PrivacyPreferences,
            fixture.Settings.Saved.OnboardingPreferenceIdentifiers.ToArray());
        Assert.AreEqual(
            "temporary-api-key",
            new string(fixture.Secrets.SavedSecret));
        Assert.IsFalse(fixture.ViewModel.HasApiKeyDraft);
        Assert.AreEqual(1, clearRequests);
        CollectionAssert.AreEqual(
            SaveOperations,
            fixture.Operations);
    }

    [TestMethod]
    public async Task TestConnectionPersistsBeforeProbeAndClearsDraft()
    {
        SettingsFixture fixture = new();
        fixture.ViewModel.ReplaceApiKeyDraft("probe-key".AsSpan());

        await fixture.ViewModel.TestConnectionAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            TestOperations,
            fixture.Operations);
        Assert.IsFalse(fixture.ViewModel.HasApiKeyDraft);
    }

    [TestMethod]
    public async Task StartPersistsBeforeRuntimeCommandAndClearsDraft()
    {
        SettingsFixture fixture = new();
        fixture.ViewModel.ReplaceApiKeyDraft("start-key".AsSpan());

        await fixture.ViewModel.StartAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            StartOperations,
            fixture.Operations);
        Assert.HasCount(1, fixture.Runtime.Commands);
        Assert.IsInstanceOfType<RuntimeCommand.Start>(
            fixture.Runtime.Commands[0]);
        Assert.IsFalse(fixture.ViewModel.HasApiKeyDraft);
    }

    [TestMethod]
    public void CloseClearsDraftWithoutPersistingIt()
    {
        SettingsFixture fixture = new();
        fixture.ViewModel.ReplaceApiKeyDraft("discard-me".AsSpan());

        fixture.ViewModel.Close();

        Assert.IsFalse(fixture.ViewModel.HasApiKeyDraft);
        Assert.IsEmpty(fixture.Operations);
        Assert.IsEmpty(fixture.Secrets.SavedSecret);
    }

    [TestMethod]
    public void ViewModelNeverLoadsOrExposesTheStoredApiKey()
    {
        SettingsFixture fixture = new();

        Type viewModelType = fixture.ViewModel.GetType();
        Assert.IsFalse(
            viewModelType
                .GetProperties()
                .Any(static property =>
                    property.PropertyType == typeof(string)
                    && property.Name is "ApiKey" or "ApiKeyDraft"
                    && property.CanRead));
        Assert.AreEqual(0, fixture.Secrets.LoadCount);
    }

    [TestMethod]
    public async Task FailedSaveStillClearsTheApiKeyDraft()
    {
        SettingsFixture fixture = new()
        {
            SettingsSaveError = new IOException("disk unavailable"),
        };
        fixture.ViewModel.ReplaceApiKeyDraft("clear-on-error".AsSpan());

        await Assert.ThrowsExactlyAsync<IOException>(
            async () =>
                await fixture.ViewModel.SaveAsync(CancellationToken.None));

        Assert.IsFalse(fixture.ViewModel.HasApiKeyDraft);
    }

    [TestMethod]
    public async Task InvalidSettingsNeverPartiallyPersistTheApiKey()
    {
        SettingsFixture fixture = new();
        fixture.ViewModel.BaseAddress = "not-an-absolute-url";
        fixture.ViewModel.ReplaceApiKeyDraft("must-not-save".AsSpan());

        await Assert.ThrowsExactlyAsync<UriFormatException>(
            async () =>
                await fixture.ViewModel.SaveAsync(CancellationToken.None));

        Assert.IsEmpty(fixture.Secrets.SavedSecret);
        Assert.IsFalse(fixture.ViewModel.HasApiKeyDraft);
    }

    [TestMethod]
    public void InterfaceLanguageIsIndependentFromTranslationLanguages()
    {
        SettingsFixture fixture = new();
        fixture.ViewModel.NativeLanguage = LanguageCode.De;
        fixture.ViewModel.MeetingLanguage = LanguageCode.En;

        fixture.ViewModel.InterfaceLanguage =
            AppInterfaceLanguage.ZhHans;

        Assert.AreEqual(LanguageCode.De, fixture.ViewModel.NativeLanguage);
        Assert.AreEqual(LanguageCode.En, fixture.ViewModel.MeetingLanguage);
        Assert.AreEqual(
            AppInterfaceLanguage.ZhHans,
            fixture.Localization.CurrentLanguage);
        Assert.AreEqual("设置", fixture.ViewModel.WindowTitle);
    }

    [TestMethod]
    public void EverySelectorOptionIsLocalizedAndRemapsWithInterfaceLanguage()
    {
        SettingsFixture fixture = new();

        CollectionAssert.AreEqual(
            EnglishTranslationOptions,
            fixture.ViewModel.TranslationLanguageOptions
                .Select(static option => option.Label)
                .ToArray());
        CollectionAssert.AreEqual(
            EnglishInterfaceOptions,
            fixture.ViewModel.InterfaceLanguageOptions
                .Select(static option => option.Label)
                .ToArray());

        fixture.ViewModel.InterfaceLanguage =
            AppInterfaceLanguage.ZhHans;

        CollectionAssert.AreEqual(
            ChineseTranslationOptions,
            fixture.ViewModel.TranslationLanguageOptions
                .Select(static option => option.Label)
                .ToArray());
        CollectionAssert.AreEqual(
            ChineseInterfaceOptions,
            fixture.ViewModel.InterfaceLanguageOptions
                .Select(static option => option.Label)
                .ToArray());
    }

    [TestMethod]
    public void SettingsWindowUsesFiveResourceBackedSectionsAndUnboundPasswordBox()
    {
        XDocument settings = XDocument.Load(SettingsXamlPath());
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement passwordBox = settings
            .Descendants(presentation + "PasswordBox")
            .Single();

        Assert.IsFalse(
            passwordBox.Attributes().Any(static attribute =>
                attribute.Value.Contains(
                    "Binding",
                    StringComparison.Ordinal)));
        CollectionAssert.AreEquivalent(
            SettingsSectionBindings,
            settings
                .Descendants()
                .Attributes()
                .Select(static attribute => attribute.Value)
                .Where(static value => value.EndsWith(
                    "SectionLabel}",
                    StringComparison.Ordinal))
                .ToArray());
    }

    [TestMethod]
    public void SettingsWindowCloseClearsDraftAndHidesWithoutStoppingRuntime()
    {
        List<string> calls = [];
        SettingsWindowLifetime lifetime = new(
            () => calls.Add("clear"),
            () => calls.Add("hide"));

        bool cancelClose = lifetime.HandleClosing();

        Assert.IsTrue(cancelClose);
        CollectionAssert.AreEqual(
            CloseOperations,
            calls);
    }

    private static string SettingsXamlPath(
        [CallerFilePath] string sourceFile = "")
    {
        string testDirectory = Path.GetDirectoryName(sourceFile)
            ?? throw new InvalidOperationException(
                "The test source path is unavailable.");
        return Path.GetFullPath(
            Path.Combine(
                testDirectory,
                "..",
                "..",
                "src",
                "EMKE.Windows.App",
                "Settings",
                "SettingsWindow.xaml"));
    }

    private sealed class SettingsFixture
    {
        private readonly RecordingSettingsStore _settings;

        public SettingsFixture()
        {
            Operations = [];
            _settings = new RecordingSettingsStore(Operations);
            Secrets = new RecordingSecretStore(Operations);
            Runtime = new RecordingRuntimeSink(Operations);
            Localization = new LocalizationService(
                () => CultureInfo.GetCultureInfo("en-US"));
            Localization.ChangeLanguage(AppInterfaceLanguage.English);
            ViewModel = new SettingsViewModel(
                new WindowsProductSettings(
                    new Uri("https://api.302.ai"),
                    "gpt-realtime-translate",
                    LanguageCode.Zh,
                    LanguageCode.En,
                    null,
                    null,
                    followDefaultInput: true,
                    followDefaultOutput: true,
                    "english",
                    PrivacyPreferences),
                _settings,
                Secrets,
                Runtime,
                new RecordingCapabilityTester(Operations),
                new NoOpSettingsSystemActions(),
                Localization);
        }

        public Exception? SettingsSaveError
        {
            init => _settings.SaveError = value;
        }

        public List<string> Operations { get; }

        public RecordingSettingsStore Settings => _settings;

        public RecordingSecretStore Secrets { get; }

        public RecordingRuntimeSink Runtime { get; }

        public LocalizationService Localization { get; }

        public SettingsViewModel ViewModel { get; }
    }

    private sealed class RecordingSettingsStore(List<string> operations)
        : IWindowsProductSettingsStore
    {
        public WindowsProductSettings? Saved { get; private set; }

        public Exception? SaveError { get; set; }

        public ValueTask<WindowsProductSettings> LoadProductSettingsAsync(
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "The initialized view model must not reload settings.");
        }

        public ValueTask SaveProductSettingsAsync(
            WindowsProductSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("settings.save");
            if (SaveError is not null)
            {
                return ValueTask.FromException(SaveError);
            }

            Saved = settings;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSecretStore(List<string> operations)
        : ISecretStore
    {
        public int LoadCount { get; private set; }

        public char[] SavedSecret { get; private set; } = [];

        public ValueTask<ISecretBuffer?> LoadAsync(
            string name,
            CancellationToken cancellationToken)
        {
            _ = name;
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return ValueTask.FromResult<ISecretBuffer?>(null);
        }

        public ValueTask SaveAsync(
            string name,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken)
        {
            _ = name;
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("secret.save");
            SavedSecret = secret.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            string name,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Delete is not part of this test.");
        }
    }

    private sealed class RecordingRuntimeSink(List<string> operations)
        : IRuntimeCommandSink
    {
        public List<RuntimeCommand> Commands { get; } = [];

        public Task<RuntimeError?> SubmitAsync(
            RuntimeCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("runtime.start");
            Commands.Add(command);
            return Task.FromResult<RuntimeError?>(null);
        }
    }

    private sealed class RecordingCapabilityTester(List<string> operations)
        : ISettingsCapabilityTester
    {
        public Task TestConnectionAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("connection.test");
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSettingsSystemActions
        : ISettingsSystemActions
    {
        public ValueTask RunLocalDiagnosticsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask CheckForUpdatesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask ReopenOnboardingAsync(
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask ExportDiagnosticsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}

#pragma warning restore CA2007
#pragma warning restore CA1515
