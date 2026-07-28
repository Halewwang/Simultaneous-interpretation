using EMKE.Core;
using EMKE.Windows.App.Presentation;

namespace EMKE.Windows.App.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class CompatibilityManifestResourceTests
{
    [TestMethod]
    public void AppEmbedsAndLoadsExactInternalCompatibilityManifest()
    {
        CompatibilityManifest manifest =
            CompatibilityManifest.LoadEmbedded(
                typeof(AppPresentationMapper).Assembly,
                "EMKE.Windows.App.compatibility.internal.json");

        Assert.AreEqual(new Version(0, 1, 0), manifest.AppVersion);
        Assert.AreEqual(1, manifest.ContractVersion);
        Assert.AreEqual(1, manifest.SettingsSchemaVersion);
        Assert.AreEqual(1, manifest.DriverAbiVersion);
        Assert.AreEqual(new Version(0, 1, 0), manifest.MinimumDriverVersion);
        Assert.AreEqual(
            new Version(0, 1, 0),
            manifest.RecommendedDriverVersion);
        Assert.IsFalse(manifest.DriverPackageAvailable);
        Assert.AreEqual("internal", manifest.Channel);
        Assert.AreEqual(26200, manifest.MinimumWindowsBuild);
        Assert.AreEqual(4, manifest.RequiredEndpointRoleCount);
    }
}
