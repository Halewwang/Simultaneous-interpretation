using System.Buffers.Binary;
using System.Text.Json;
using System.Xml.Linq;

namespace EMKE.Integration.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class MsixMetadataTests
{
    private const int MaximumParentLevels = 8;
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap10 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace RestrictedCapability =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    [TestMethod]
    public void InternalManifestMatchesVersionAndChannelMetadata()
    {
        string repositoryRoot = FindRepositoryRoot();
        string windowsRoot = Path.Combine(repositoryRoot, "Windows");
        XDocument manifest = XDocument.Load(
            Path.Combine(
                windowsRoot,
                "packaging",
                "App",
                "AppxManifest.internal.xml"),
            LoadOptions.None);
        using JsonDocument version = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(windowsRoot, "version.json")));
        using JsonDocument channels = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(windowsRoot, "packaging", "channels.json")));

        XElement identity = RequireElement(manifest.Root, Foundation + "Identity");
        XElement dependencies = RequireElement(
            manifest.Root,
            Foundation + "Dependencies");
        XElement target = RequireElement(
            dependencies,
            Foundation + "TargetDeviceFamily");
        XElement applications = RequireElement(
            manifest.Root,
            Foundation + "Applications");
        XElement application = RequireElement(
            applications,
            Foundation + "Application");
        XElement capabilities = RequireElement(
            manifest.Root,
            Foundation + "Capabilities");
        XElement capability = RequireElement(
            capabilities,
            RestrictedCapability + "Capability");

        JsonElement versionRoot = version.RootElement;
        JsonElement internalChannel = channels.RootElement
            .GetProperty("channels")
            .GetProperty("internal");
        int minimumBuild = versionRoot.GetProperty("minimumWindowsBuild").GetInt32();

        Assert.AreEqual(
            internalChannel.GetProperty("packageIdentity").GetString(),
            ReadAttribute(identity, "Name"));
        Assert.AreEqual(
            internalChannel.GetProperty("publisher").GetString(),
            ReadAttribute(identity, "Publisher"));
        Assert.AreEqual(
            versionRoot.GetProperty("packageVersion").GetString(),
            ReadAttribute(identity, "Version"));
        Assert.AreEqual(
            versionRoot.GetProperty("architecture").GetString(),
            ReadAttribute(identity, "ProcessorArchitecture"));
        Assert.AreEqual("Windows.Desktop", ReadAttribute(target, "Name"));
        Assert.AreEqual(
            $"10.0.{minimumBuild}.0",
            ReadAttribute(target, "MinVersion"));
        Assert.AreEqual(
            $"10.0.{minimumBuild}.0",
            ReadAttribute(target, "MaxVersionTested"));
        Assert.AreEqual("EMKETranslation", ReadAttribute(application, "Id"));
        Assert.AreEqual(
            "EMKE.Windows.App.exe",
            ReadAttribute(application, "Executable"));
        Assert.AreEqual(
            "Windows.FullTrustApplication",
            ReadAttribute(application, "EntryPoint"));
        Assert.AreEqual(
            "packagedClassicApp",
            ReadAttribute(application, Uap10 + "RuntimeBehavior"));
        Assert.AreEqual(
            "mediumIL",
            ReadAttribute(application, Uap10 + "TrustLevel"));
        Assert.AreEqual("runFullTrust", ReadAttribute(capability, "Name"));
    }

    [TestMethod]
    public void PackageAssetsUseApprovedMasterAndExactRasterSizes()
    {
        string repositoryRoot = FindRepositoryRoot();
        string assetRoot = Path.Combine(
            repositoryRoot,
            "Windows",
            "packaging",
            "App",
            "Assets");
        byte[] approved = File.ReadAllBytes(
            Path.Combine(
                repositoryRoot,
                "Packaging",
                "Assets",
                "EMKE-AppIcon-Approved.png"));
        byte[] packagedApproved = File.ReadAllBytes(
            Path.Combine(assetRoot, "EMKE-AppIcon-Approved.png"));

        CollectionAssert.AreEqual(approved, packagedApproved);
        AssertPngSize(
            Path.Combine(assetRoot, "Square44x44Logo.png"),
            44,
            44);
        AssertPngSize(
            Path.Combine(assetRoot, "Square150x150Logo.png"),
            150,
            150);
        AssertPngSize(Path.Combine(assetRoot, "StoreLogo.png"), 50, 50);
    }

    private static void AssertPngSize(
        string path,
        uint expectedWidth,
        uint expectedHeight)
    {
        byte[] bytes = File.ReadAllBytes(path);
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];

        Assert.IsGreaterThanOrEqualTo(
            24,
            bytes.Length,
            $"{path} must contain a PNG header.");
        CollectionAssert.AreEqual(
            signature,
            bytes[..signature.Length],
            $"{path} must be a PNG file.");
        Assert.AreEqual(
            expectedWidth,
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.AreEqual(
            expectedHeight,
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    private static XElement RequireElement(
        XContainer? parent,
        XName elementName)
    {
        XElement? element = parent?.Element(elementName);
        Assert.IsNotNull(element, $"Required element {elementName} is unavailable.");
        return element;
    }

    private static string ReadAttribute(XElement element, XName name)
    {
        XAttribute? attribute = element.Attribute(name);
        Assert.IsNotNull(attribute, $"Required attribute {name} is unavailable.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(attribute.Value));
        return attribute.Value;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        for (int level = 0; level <= MaximumParentLevels && current is not null; level++)
        {
            if (File.Exists(Path.Combine(current.FullName, "Windows", "version.json"))
                && File.Exists(
                    Path.Combine(
                        current.FullName,
                        "Packaging",
                        "Assets",
                        "EMKE-AppIcon-Approved.png")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        Assert.Fail(
            "Unable to locate the repository root within the test output directory and eight parent levels.");
        return string.Empty;
    }
}

#pragma warning restore CA1515
