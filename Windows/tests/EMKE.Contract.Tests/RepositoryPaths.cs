using System.Text.Json;

namespace EMKE.Contract.Tests;

internal static class RepositoryPaths
{
    private const int MaximumParentLevels = 8;
    private const string ContractManifestMarker = "Shared/Contracts/contract-manifest.json";

    internal static string FindContractManifest()
    {
        return FindContractManifestFrom(AppContext.BaseDirectory);
    }

    internal static string FindContractManifestFrom(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        DirectoryInfo? current = new(Path.GetFullPath(startDirectory));
        for (int level = 0; level <= MaximumParentLevels && current is not null; level++)
        {
            string candidate = Path.Combine(
                current.FullName,
                "Shared",
                "Contracts",
                "contract-manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Unable to locate {ContractManifestMarker} within the current directory and eight parent levels.");
    }

    internal static IReadOnlyList<string> ResolveSchemaFiles(
        string contractManifestPath,
        JsonElement entries)
    {
        string contractsRoot = GetContainingDirectory(
            contractManifestPath,
            "contract manifest");
        return ResolveEntries(
            contractsRoot,
            contractsRoot,
            entries,
            "schema");
    }

    internal static string ResolveFixtureManifest(
        string contractManifestPath,
        string entry)
    {
        string contractsRoot = GetContainingDirectory(
            contractManifestPath,
            "contract manifest");
        DirectoryInfo? sharedRoot = Directory.GetParent(contractsRoot);
        if (sharedRoot is null)
        {
            throw new InvalidDataException(
                "Invalid fixture manifest entry: the Shared root is unavailable.");
        }

        string testVectorsRoot = Path.Combine(sharedRoot.FullName, "TestVectors");
        return ResolveEntry(
            contractsRoot,
            testVectorsRoot,
            entry,
            "fixture manifest");
    }

    internal static IReadOnlyList<string> ResolveFixtureFiles(
        string fixtureManifestPath,
        JsonElement entries)
    {
        string testVectorsRoot = GetContainingDirectory(
            fixtureManifestPath,
            "fixture manifest");
        return ResolveEntries(
            testVectorsRoot,
            testVectorsRoot,
            entries,
            "fixture");
    }

    private static List<string> ResolveEntries(
        string baseDirectory,
        string allowedRoot,
        JsonElement entries,
        string entryKind)
    {
        if (entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Invalid {entryKind} manifest entries: an array is required.");
        }

        List<string> resolvedPaths = [];
        HashSet<string> uniquePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in entries.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"Invalid {entryKind} manifest entry: a string is required.");
            }

            string entry = item.GetString()!;
            string resolvedPath = ResolveEntry(
                baseDirectory,
                allowedRoot,
                entry,
                entryKind);
            if (!uniquePaths.Add(resolvedPath))
            {
                throw new InvalidDataException(
                    $"Invalid {entryKind} manifest entries: duplicate files are not allowed.");
            }

            resolvedPaths.Add(resolvedPath);
        }

        return resolvedPaths;
    }

    private static string ResolveEntry(
        string baseDirectory,
        string allowedRoot,
        string entry,
        string entryKind)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            throw new InvalidDataException(
                $"Invalid {entryKind} manifest entry: a non-empty relative path is required.");
        }

        if (IsRootedOnAnySupportedPlatform(entry))
        {
            throw new InvalidDataException(
                $"Invalid {entryKind} manifest entry: rooted paths are not allowed.");
        }

        if (entry.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Invalid {entryKind} manifest entry: the relative path is not canonical.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(
                Path.Combine(
                    baseDirectory,
                    entry.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw new InvalidDataException(
                $"Invalid {entryKind} manifest entry: the relative path is not canonical.");
        }

        string canonicalEntry = Path.GetRelativePath(baseDirectory, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!string.Equals(entry, canonicalEntry, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Invalid {entryKind} manifest entry: the relative path is not canonical.");
        }

        string relativeToAllowedRoot = Path.GetRelativePath(allowedRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (Path.IsPathRooted(relativeToAllowedRoot)
            || string.Equals(relativeToAllowedRoot, "..", StringComparison.Ordinal)
            || relativeToAllowedRoot.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Invalid {entryKind} manifest entry: parent traversal leaves the allowed root.");
        }

        if (Directory.Exists(fullPath))
        {
            throw new InvalidDataException(
                $"Invalid {entryKind} manifest entry: directories are not allowed.");
        }

        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException(
                $"Invalid {entryKind} manifest entry: the file does not exist.");
        }

        return fullPath;
    }

    private static string GetContainingDirectory(string path, string marker)
    {
        string? directory = Path.GetDirectoryName(path);
        return directory
            ?? throw new InvalidDataException(
                $"Invalid {marker} marker: the containing directory is unavailable.");
    }

    private static bool IsRootedOnAnySupportedPlatform(string path)
    {
        return Path.IsPathRooted(path)
            || path.StartsWith('\\')
            || (path.Length >= 2
                && char.IsAsciiLetter(path[0])
                && path[1] == ':');
    }
}
