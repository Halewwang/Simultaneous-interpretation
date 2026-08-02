using EMKE.Platform.Native;

namespace EMKE.Integration.Tests;

[TestClass]
public sealed class WindowsAudioDeviceCatalogTests
{
    [TestMethod]
    public void ProductionAssemblyExposesNativeBackedDeviceCatalog()
    {
        Type? catalogType = typeof(PInvokeNativeAudioApi).Assembly.GetType(
            "EMKE.Platform.Native.WindowsAudioDeviceCatalog",
            throwOnError: false,
            ignoreCase: false);

        Assert.IsNotNull(catalogType);
        Assert.IsTrue(typeof(EMKE.Core.IAudioDeviceCatalog).IsAssignableFrom(catalogType));
    }
}
