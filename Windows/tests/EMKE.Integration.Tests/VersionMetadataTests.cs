using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed partial class VersionMetadataTests
{
    private const int MaximumParentLevels = 8;
    private const int MaximumCapturedCharacters = 16 * 1024;

    [TestMethod]
    public void VersionMetadataDefinesCanonicalInternalRelease()
    {
        string windowsRoot = FindWindowsRoot();
        using JsonDocument version = LoadJson(
            windowsRoot,
            "version.json",
            "Windows version metadata");
        JsonElement root = RequireObjectRoot(version, "Windows version metadata");

        string productVersion = ReadRequiredString(root, "productVersion");
        Assert.AreEqual("0.2.0", productVersion);
        AssertCanonicalThreePartVersion(productVersion, "productVersion");

        string packageVersion = ReadRequiredString(root, "packageVersion");
        Assert.AreEqual("0.2.0.0", packageVersion);
        AssertPackageVersion(packageVersion, productVersion);

        string expectedTag = ReadRequiredString(root, "expectedTag");
        Assert.AreEqual("windows-v0.2.0", expectedTag);
        Assert.IsTrue(
            string.Equals(expectedTag, $"windows-v{productVersion}", StringComparison.Ordinal));
        Assert.IsFalse(
            string.Equals(expectedTag, $"v{productVersion}", StringComparison.Ordinal));

        Assert.AreEqual(1, ReadRequiredInt32(root, "contractVersion"));
        Assert.AreEqual(1, ReadRequiredInt32(root, "settingsSchemaVersion"));
        Assert.AreEqual(1, ReadRequiredInt32(root, "driverAbiVersion"));
        Assert.AreEqual(19045, ReadRequiredInt32(root, "minimumWindowsBuild"));
        Assert.AreEqual("10.0.19041.0", ReadRequiredString(root, "minimumWindowsApiContract"));
        Assert.AreEqual("10.0.26200.0", ReadRequiredString(root, "maximumVersionTested"));
        Assert.AreEqual("x64", ReadRequiredString(root, "architecture"));
        Assert.AreEqual("internal", ReadRequiredString(root, "channel"));
    }

    [TestMethod]
    public void ChannelAndCompatibilityMetadataMatchInternalRelease()
    {
        string windowsRoot = FindWindowsRoot();
        using JsonDocument version = LoadJson(
            windowsRoot,
            "version.json",
            "Windows version metadata");
        using JsonDocument channels = LoadJson(
            windowsRoot,
            Path.Combine("packaging", "channels.json"),
            "Windows channel metadata");
        using JsonDocument compatibility = LoadJson(
            windowsRoot,
            Path.Combine("packaging", "compatibility.internal.json"),
            "Windows compatibility metadata");

        JsonElement versionRoot = RequireObjectRoot(version, "Windows version metadata");
        JsonElement channelsRoot = RequireObjectRoot(channels, "Windows channel metadata");
        JsonElement channelMap = RequireObjectProperty(channelsRoot, "channels");

        Assert.IsTrue(
            channelMap.EnumerateObject().Any(
                property => string.Equals(property.Name, "internal", StringComparison.Ordinal)),
            "Channel metadata must contain the exact key 'internal'.");

        JsonElement internalChannel = RequireObjectProperty(channelMap, "internal");
        Assert.AreEqual(
            "EMKE.Translation.Internal",
            ReadRequiredString(internalChannel, "packageIdentity"));
        Assert.AreEqual(
            "CN=EMKE Internal Test",
            ReadRequiredString(internalChannel, "publisher"));

        JsonElement betaChannel = RequireObjectProperty(channelMap, "beta");
        Assert.AreEqual(
            "EMKE.Translation.Beta",
            ReadRequiredString(betaChannel, "packageIdentity"));

        JsonElement stableChannel = RequireObjectProperty(channelMap, "stable");
        Assert.AreEqual(
            "EMKE.Translation",
            ReadRequiredString(stableChannel, "packageIdentity"));

        JsonElement compatibilityRoot = RequireObjectRoot(
            compatibility,
            "Windows compatibility metadata");

        Assert.AreEqual(
            ReadRequiredString(versionRoot, "productVersion"),
            ReadRequiredString(compatibilityRoot, "appVersion"));
        Assert.AreEqual(
            ReadRequiredInt32(versionRoot, "contractVersion"),
            ReadRequiredInt32(compatibilityRoot, "contractVersion"));
        Assert.AreEqual(
            ReadRequiredInt32(versionRoot, "settingsSchemaVersion"),
            ReadRequiredInt32(compatibilityRoot, "settingsSchemaVersion"));
        Assert.AreEqual(
            ReadRequiredInt32(versionRoot, "driverAbiVersion"),
            ReadRequiredInt32(compatibilityRoot, "driverAbiVersion"));
        Assert.AreEqual(
            ReadRequiredString(versionRoot, "channel"),
            ReadRequiredString(compatibilityRoot, "channel"));
        Assert.AreEqual(
            "1.0.0.2",
            ReadRequiredString(compatibilityRoot, "minimumDriverVersion"));
        Assert.AreEqual(
            "1.0.0.2",
            ReadRequiredString(compatibilityRoot, "recommendedDriverVersion"));

        JsonElement driverPackageAvailable = RequireProperty(
            compatibilityRoot,
            "driverPackageAvailable");
        Assert.AreEqual(
            JsonValueKind.False,
            driverPackageAvailable.ValueKind,
            "driverPackageAvailable must be the JSON Boolean false.");
        Assert.IsFalse(driverPackageAvailable.GetBoolean());

        Assert.IsFalse(
            compatibilityRoot.EnumerateObject().Any(
                property => string.Equals(
                    property.Name,
                    "driverPackageSha256",
                    StringComparison.OrdinalIgnoreCase)),
            "Compatibility metadata must not contain a driver package SHA-256 location.");
        Assert.IsFalse(
            compatibilityRoot.EnumerateObject().Any(
                property => string.Equals(
                    property.Name,
                    "driverPackageUrl",
                    StringComparison.OrdinalIgnoreCase)),
            "Compatibility metadata must not contain a driver package URL.");
    }

    [TestMethod]
    public void MetadataAndResolverContainNoEmbeddedCredentialMaterial()
    {
        string windowsRoot = FindWindowsRoot();
        string[] relativePaths =
        [
            "version.json",
            Path.Combine("packaging", "channels.json"),
            Path.Combine("packaging", "compatibility.internal.json"),
            Path.Combine("tools", "resolve-version.ps1"),
        ];

        foreach (string relativePath in relativePaths)
        {
            string contents = ReadRepositoryFile(
                windowsRoot,
                relativePath,
                "Windows release metadata source");

            Assert.IsFalse(
                EmbeddedCredentialMaterialRegex().IsMatch(contents),
                "Windows release metadata and resolver source must not embed credential material.");
        }
    }

    [TestMethod]
    public async Task ResolverAcceptsWindowsTagAndRejectsMacOsTag()
    {
        string windowsRoot = FindWindowsRoot();
        ProcessResult accepted = await RunResolverAsync(
            windowsRoot,
            "windows-v0.2.0").ConfigureAwait(false);

        Assert.AreEqual(
            0,
            accepted.ExitCode,
            "The resolver must accept the canonical Windows tag.");
        Assert.IsFalse(
            EmbeddedCredentialMaterialRegex().IsMatch(accepted.CombinedOutput),
            "Resolver output must not contain credential material.");

        ProcessResult rejected = await RunResolverAsync(
            windowsRoot,
            "v0.2.0").ConfigureAwait(false);

        Assert.AreNotEqual(
            0,
            rejected.ExitCode,
            "The resolver must reject the macOS tag.");

        string normalizedError = WhitespaceRegex().Replace(
            rejected.CombinedOutput,
            " ");
        Assert.IsTrue(
            normalizedError.Contains("Expected tag", StringComparison.OrdinalIgnoreCase),
            "Resolver rejection must explain the expected tag.");
        Assert.IsTrue(
            normalizedError.Contains("received", StringComparison.OrdinalIgnoreCase),
            "Resolver rejection must explain the received tag.");
        Assert.IsTrue(
            normalizedError.Contains("windows-v0.2.0", StringComparison.Ordinal),
            "Resolver rejection must identify the canonical Windows tag.");
        Assert.IsTrue(
            normalizedError.Contains("v0.2.0", StringComparison.Ordinal),
            "Resolver rejection must identify the received macOS tag.");
        Assert.IsFalse(
            EmbeddedCredentialMaterialRegex().IsMatch(rejected.CombinedOutput),
            "Resolver error output must not contain credential material.");
    }

    private static string FindWindowsRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        for (int level = 0; level <= MaximumParentLevels && current is not null; level++)
        {
            string windowsRoot = Path.Combine(current.FullName, "Windows");
            if (File.Exists(Path.Combine(windowsRoot, "version.json")))
            {
                return windowsRoot;
            }

            current = current.Parent;
        }

        Assert.Fail(
            "Unable to locate Windows/version.json within the test output directory and eight parent levels.");
        return string.Empty;
    }

    private static JsonDocument LoadJson(
        string windowsRoot,
        string relativePath,
        string description)
    {
        string contents = ReadRepositoryFile(windowsRoot, relativePath, description);

        try
        {
            return JsonDocument.Parse(contents);
        }
        catch (JsonException)
        {
            Assert.Fail($"{description} must contain valid JSON.");
            return null!;
        }
    }

    private static string ReadRepositoryFile(
        string windowsRoot,
        string relativePath,
        string description)
    {
        string path = Path.Combine(windowsRoot, relativePath);
        if (!File.Exists(path))
        {
            Assert.Fail($"{description} is required.");
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            Assert.Fail($"{description} could not be read.");
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Fail($"{description} could not be read.");
            return string.Empty;
        }
    }

    private static JsonElement RequireObjectRoot(
        JsonDocument document,
        string description)
    {
        Assert.AreEqual(
            JsonValueKind.Object,
            document.RootElement.ValueKind,
            $"{description} root must be a JSON object.");
        return document.RootElement;
    }

    private static JsonElement RequireObjectProperty(
        JsonElement parent,
        string propertyName)
    {
        JsonElement property = RequireProperty(parent, propertyName);
        Assert.AreEqual(
            JsonValueKind.Object,
            property.ValueKind,
            $"'{propertyName}' must be a JSON object.");
        return property;
    }

    private static JsonElement RequireProperty(
        JsonElement parent,
        string propertyName)
    {
        Assert.IsTrue(
            parent.TryGetProperty(propertyName, out JsonElement property),
            $"Required JSON property '{propertyName}' is missing.");
        return property;
    }

    private static string ReadRequiredString(
        JsonElement parent,
        string propertyName)
    {
        JsonElement property = RequireProperty(parent, propertyName);
        Assert.AreEqual(
            JsonValueKind.String,
            property.ValueKind,
            $"'{propertyName}' must be a JSON string.");
        return property.GetString()!;
    }

    private static int ReadRequiredInt32(
        JsonElement parent,
        string propertyName)
    {
        JsonElement property = RequireProperty(parent, propertyName);
        Assert.AreEqual(
            JsonValueKind.Number,
            property.ValueKind,
            $"'{propertyName}' must be a JSON number.");
        Assert.IsTrue(
            property.TryGetInt32(out int value),
            $"'{propertyName}' must be a JSON integer in the Int32 range.");
        return value;
    }

    private static void AssertCanonicalThreePartVersion(
        string value,
        string propertyName)
    {
        string[] segments = value.Split('.');
        Assert.HasCount(
            3,
            segments,
            $"{propertyName} must contain exactly three numeric parts.");

        foreach (string segment in segments)
        {
            Assert.IsTrue(
                segment.Length > 0 && segment.All(character => character is >= '0' and <= '9'),
                $"{propertyName} parts must contain only ASCII digits.");
            Assert.IsFalse(
                segment.Length > 1 && segment[0] == '0',
                $"{propertyName} parts must use canonical SemVer numeric notation.");
        }
    }

    private static void AssertPackageVersion(
        string packageVersion,
        string productVersion)
    {
        string[] segments = packageVersion.Split('.');
        Assert.HasCount(
            4,
            segments,
            "packageVersion must contain exactly four numeric parts.");

        foreach (string segment in segments)
        {
            Assert.IsTrue(
                segment.Length > 0 && segment.All(character => character is >= '0' and <= '9'),
                "packageVersion parts must contain only ASCII digits.");
            Assert.IsTrue(
                ushort.TryParse(
                    segment,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _),
                "Every packageVersion part must be between 0 and 65535.");
        }

        Assert.AreEqual(
            productVersion,
            string.Join('.', segments[..3]),
            "The first three packageVersion parts must match productVersion.");
    }

    private static async Task<ProcessResult> RunResolverAsync(
        string windowsRoot,
        string requiredTag)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(windowsRoot, "tools", "resolve-version.ps1"));
        startInfo.ArgumentList.Add("-VersionFile");
        startInfo.ArgumentList.Add(Path.Combine(windowsRoot, "version.json"));
        startInfo.ArgumentList.Add("-RequireTag");
        startInfo.ArgumentList.Add(requiredTag);

        using Process process = new()
        {
            StartInfo = startInfo,
        };

        try
        {
            if (!process.Start())
            {
                Assert.Fail("PowerShell executable 'pwsh' could not be started.");
            }
        }
        catch (Win32Exception)
        {
            Assert.Fail(
                "PowerShell executable 'pwsh' is required for version metadata integration tests.");
        }

        Task<string> standardOutputTask = ReadBoundedAsync(process.StandardOutput);
        Task<string> standardErrorTask = ReadBoundedAsync(process.StandardError);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The resolver exited while timeout handling was in progress.
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            Assert.Fail("The version metadata resolver exceeded the 15-second timeout.");
        }

        string standardOutput = await standardOutputTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        char[] buffer = new char[1024];
        StringBuilder captured = new(MaximumCapturedCharacters);

        while (true)
        {
            int charactersRead = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (charactersRead == 0)
            {
                break;
            }

            int remainingCapacity = MaximumCapturedCharacters - captured.Length;
            if (remainingCapacity > 0)
            {
                captured.Append(buffer, 0, Math.Min(charactersRead, remainingCapacity));
            }
        }

        return captured.ToString();
    }

    [GeneratedRegex(
        """(?ix)(?:-----BEGIN\s+(?:RSA\s+|EC\s+|OPENSSH\s+)?PRIVATE\s+KEY-----|\.pfx\b|\bsk-[A-Za-z0-9_-]{16,}\b|(?:["']?(?:password|pfxPassword|privateKey|private_key|privateKeyBase64|pfxBase64|secretBase64)["']?)\s*[:=]\s*["'][^"'\r\n]+["'])""",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedCredentialMaterialRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public string CombinedOutput => $"{StandardOutput}\n{StandardError}";
    }
}
