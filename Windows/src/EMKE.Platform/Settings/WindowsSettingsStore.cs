using System.Text;
using System.Text.Json;
using EMKE.Core;

namespace EMKE.Platform.Settings;

public sealed record WindowsProductSettings
{
    public WindowsProductSettings(
        Uri baseUri,
        string modelId,
        LanguageCode nativeLanguage,
        LanguageCode meetingLanguage,
        string? inputEndpointId,
        string? outputEndpointId,
        bool followDefaultInput,
        bool followDefaultOutput,
        string interfaceLanguage,
        IEnumerable<string> onboardingPreferenceIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri
            || baseUri.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException(
                "Base URL must be an absolute HTTP or HTTPS URL.",
                nameof(baseUri));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _ = SerializeLanguage(nativeLanguage);
        _ = SerializeLanguage(meetingLanguage);
        ValidateEndpoint(inputEndpointId, nameof(inputEndpointId));
        ValidateEndpoint(outputEndpointId, nameof(outputEndpointId));
        if (interfaceLanguage is not ("system" or "zhHans" or "english"))
        {
            throw new ArgumentException(
                "Interface language must be system, zhHans, or english.",
                nameof(interfaceLanguage));
        }

        ArgumentNullException.ThrowIfNull(onboardingPreferenceIdentifiers);
        string[] preferenceIdentifiers =
            onboardingPreferenceIdentifiers.ToArray();
        if (preferenceIdentifiers.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Onboarding preference identifiers must not be empty.",
                nameof(onboardingPreferenceIdentifiers));
        }

        BaseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/'), UriKind.Absolute);
        ModelId = modelId.Trim();
        NativeLanguage = nativeLanguage;
        MeetingLanguage = meetingLanguage;
        InputEndpointId = inputEndpointId;
        OutputEndpointId = outputEndpointId;
        FollowDefaultInput = followDefaultInput;
        FollowDefaultOutput = followDefaultOutput;
        InterfaceLanguage = interfaceLanguage;
        OnboardingPreferenceIdentifiers = Array.AsReadOnly(
            [.. preferenceIdentifiers.Distinct(StringComparer.Ordinal)]);
    }

    public Uri BaseUri { get; }

    public string ModelId { get; }

    public LanguageCode NativeLanguage { get; }

    public LanguageCode MeetingLanguage { get; }

    public string? InputEndpointId { get; }

    public string? OutputEndpointId { get; }

    public bool FollowDefaultInput { get; }

    public bool FollowDefaultOutput { get; }

    public string InterfaceLanguage { get; }

    public IReadOnlyList<string> OnboardingPreferenceIdentifiers { get; }

    public static WindowsProductSettings SafeDefaults { get; } =
        FromDocument(WindowsSettingsDocument.SafeDefaults);

    internal WindowsSettingsDocument ToDocument()
    {
        return new WindowsSettingsDocument(
            WindowsSettingsDocument.SafeDefaults.SchemaVersion,
            BaseUri.OriginalString,
            ModelId,
            SerializeLanguage(NativeLanguage),
            SerializeLanguage(MeetingLanguage),
            InputEndpointId,
            OutputEndpointId,
            FollowDefaultInput,
            FollowDefaultOutput,
            InterfaceLanguage,
            [.. OnboardingPreferenceIdentifiers]);
    }

    internal static WindowsProductSettings FromDocument(
        WindowsSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new WindowsProductSettings(
            new Uri(document.BaseUrl, UriKind.Absolute),
            document.ModelId,
            ParseLanguage(document.NativeLanguage),
            ParseLanguage(document.MeetingLanguage),
            document.InputEndpointId,
            document.OutputEndpointId,
            document.FollowDefaultInput,
            document.FollowDefaultOutput,
            document.InterfaceLanguage,
            document.OnboardingPreferenceIdentifiers);
    }

    internal static LanguageCode ParseLanguage(string stableValue)
    {
        return stableValue switch
        {
            "zh" => LanguageCode.Zh,
            "en" => LanguageCode.En,
            "de" => LanguageCode.De,
            _ => throw new InvalidDataException(
                $"Unsupported settings language: {stableValue}"),
        };
    }

    internal static string SerializeLanguage(LanguageCode language)
    {
        return language switch
        {
            LanguageCode.Zh => "zh",
            LanguageCode.En => "en",
            LanguageCode.De => "de",
            _ => throw new ArgumentOutOfRangeException(
                nameof(language),
                language,
                "Undefined language."),
        };
    }

    private static void ValidateEndpoint(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Endpoint ID must be null or non-empty.",
                parameterName);
        }
    }
}

public interface IWindowsProductSettingsStore
{
    ValueTask<WindowsProductSettings> LoadProductSettingsAsync(
        CancellationToken cancellationToken);

    ValueTask SaveProductSettingsAsync(
        WindowsProductSettings settings,
        CancellationToken cancellationToken);
}

public interface IWindowsSettingsPersistence
{
    ValueTask<string?> ReadAsync(CancellationToken cancellationToken);

    ValueTask OverwriteAsync(
        string canonicalJson,
        CancellationToken cancellationToken);

    ValueTask QuarantineAsync(
        string invalidJson,
        CancellationToken cancellationToken);
}

internal enum AtomicFileOperationKind
{
    WriteAndFlush,
    MoveReplace,
    MoveNoReplace,
}

internal sealed record AtomicFileOperation(
    AtomicFileOperationKind Kind,
    string SourcePath,
    string? DestinationPath);

internal interface IAtomicSettingsFileSystem
{
    ValueTask<string?> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken);

    ValueTask WriteAndFlushAsync(
        string path,
        string contents,
        CancellationToken cancellationToken);

    void Move(string sourcePath, string destinationPath, bool overwrite);

    bool FileExists(string path);
}

internal sealed class SystemAtomicSettingsFileSystem
    : IAtomicSettingsFileSystem
{
    private static readonly UTF8Encoding Utf8WithoutBom =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async ValueTask<string?> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(
                    path,
                    Utf8WithoutBom,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async ValueTask WriteAndFlushAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Utf8WithoutBom.GetBytes(contents);
        try
        {
            FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Flush(true) is the durable-write contract before atomic rename.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public void Move(
        string sourcePath,
        string destinationPath,
        bool overwrite)
    {
        File.Move(sourcePath, destinationPath, overwrite);
    }

    public bool FileExists(string path)
    {
        return File.Exists(path);
    }
}

public sealed class FileSystemWindowsSettingsPersistence
    : IWindowsSettingsPersistence
{
    private readonly string _settingsPath;
    private readonly IAtomicSettingsFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;

    public FileSystemWindowsSettingsPersistence()
        : this(DefaultSettingsPath)
    {
    }

    public FileSystemWindowsSettingsPersistence(string settingsPath)
        : this(
            settingsPath,
            new SystemAtomicSettingsFileSystem(),
            TimeProvider.System)
    {
    }

    internal FileSystemWindowsSettingsPersistence(
        string settingsPath,
        TimeProvider timeProvider)
        : this(
            settingsPath,
            new SystemAtomicSettingsFileSystem(),
            timeProvider)
    {
    }

    internal FileSystemWindowsSettingsPersistence(
        string settingsPath,
        IAtomicSettingsFileSystem fileSystem,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
        _fileSystem = fileSystem
            ?? throw new ArgumentNullException(nameof(fileSystem));
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public static string DefaultSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "EMKE Translation",
            "settings.json");

    public ValueTask<string?> ReadAsync(CancellationToken cancellationToken)
    {
        return _fileSystem.ReadAllTextAsync(
            _settingsPath,
            cancellationToken);
    }

    public async ValueTask OverwriteAsync(
        string canonicalJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException(
                "The settings path must have a parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            await _fileSystem.WriteAndFlushAsync(
                    temporaryPath,
                    canonicalJson,
                    cancellationToken)
                .ConfigureAwait(false);
            _fileSystem.Move(
                temporaryPath,
                _settingsPath,
                overwrite: true);
        }
        finally
        {
            if (_fileSystem.FileExists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async ValueTask QuarantineAsync(
        string invalidJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invalidJson);
        cancellationToken.ThrowIfCancellationRequested();
        string? current = await ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(current, invalidJson, StringComparison.Ordinal))
        {
            return;
        }

        string directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException(
                "The settings path must have a parent directory.");
        DateTimeOffset timestamp = _timeProvider.GetUtcNow();
        string quarantinePath;
        do
        {
            quarantinePath = Path.Combine(
                directory,
                $"settings.corrupt.{timestamp:yyyyMMdd'T'HHmmssfff'Z'}.json");
            timestamp = timestamp.AddMilliseconds(1);
        }
        while (_fileSystem.FileExists(quarantinePath));

        _fileSystem.Move(
            _settingsPath,
            quarantinePath,
            overwrite: false);
    }
}

public sealed class WindowsSettingsStore :
    ISettingsStore,
    IWindowsProductSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IWindowsSettingsPersistence _persistence;
    private readonly IWindowsSettingsMigrationDiagnostics _diagnostics;
    private WindowsSettingsDocument _currentDocument =
        WindowsSettingsDocument.SafeDefaults;

    public WindowsSettingsStore(IWindowsSettingsPersistence persistence)
        : this(persistence, NullWindowsSettingsMigrationDiagnostics.Instance)
    {
    }

    internal WindowsSettingsStore(
        IWindowsSettingsPersistence persistence,
        IWindowsSettingsMigrationDiagnostics diagnostics)
    {
        _persistence =
            persistence ?? throw new ArgumentNullException(nameof(persistence));
        _diagnostics =
            diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public async ValueTask<RuntimeSettings?> LoadAsync(
        CancellationToken cancellationToken)
    {
        WindowsSettingsDocument? document =
            await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        return document is null
            ? null
            : ToRuntimeSettings(document);
    }

    public async ValueTask<WindowsProductSettings> LoadProductSettingsAsync(
        CancellationToken cancellationToken)
    {
        WindowsSettingsDocument? document =
            await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        return WindowsProductSettings.FromDocument(
            document ?? WindowsSettingsDocument.SafeDefaults);
    }

    public async ValueTask SaveAsync(
        RuntimeSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        WindowsSettingsDocument current = Volatile.Read(ref _currentDocument);
        WindowsSettingsDocument updated = current with
        {
            ModelId = settings.Model,
            NativeLanguage =
                WindowsProductSettings.SerializeLanguage(settings.SourceLanguage),
            MeetingLanguage =
                WindowsProductSettings.SerializeLanguage(settings.TargetLanguage),
        };
        await SaveDocumentAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SaveProductSettingsAsync(
        WindowsProductSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SaveDocumentAsync(settings.ToDocument(), cancellationToken);
    }

    private async ValueTask<WindowsSettingsDocument?> LoadDocumentAsync(
        CancellationToken cancellationToken)
    {
        string? persistedJson =
            await _persistence.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (persistedJson is null)
        {
            return null;
        }

        WindowsSettingsMigrationResult migration =
            WindowsSettingsMigrationPolicy.Migrate(persistedJson);
        if (migration.Quarantine)
        {
            await _persistence.QuarantineAsync(
                    persistedJson,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (migration.Overwrite)
        {
            await _persistence.OverwriteAsync(
                    Serialize(migration.Settings),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Volatile.Write(ref _currentDocument, migration.Settings);
        _diagnostics.Record(new WindowsSettingsMigrationObservation(
            migration.Outcome,
            migration.Overwrite,
            migration.Quarantine,
            migration.Settings));
        return migration.Settings;
    }

    private async ValueTask SaveDocumentAsync(
        WindowsSettingsDocument document,
        CancellationToken cancellationToken)
    {
        await _persistence.OverwriteAsync(
                Serialize(document),
                cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _currentDocument, document);
    }

    private static string Serialize(WindowsSettingsDocument settings)
    {
        return JsonSerializer.Serialize(settings, SerializerOptions);
    }

    private static RuntimeSettings ToRuntimeSettings(
        WindowsSettingsDocument settings)
    {
        return new RuntimeSettings(
            WindowsProductSettings.ParseLanguage(settings.NativeLanguage),
            WindowsProductSettings.ParseLanguage(settings.MeetingLanguage),
            settings.ModelId,
            inboundBypass: false,
            outboundBypass: false);
    }
}
