using EMKE.Setup.Elevated;

namespace EMKE.Setup.Tests;

#pragma warning disable CA1515

[TestClass]
public sealed class ProgramTests
{
    [TestMethod]
    public void UnknownOrConfusedCommandLineIsRejected()
    {
        Assert.AreEqual(2, Program.Main(["--install", "C:\\payload"]));
        Assert.AreEqual(2, Program.Main(["--verify-self-v1", "extra"]));
        Assert.AreEqual(2, Program.Main([
            SetupElevatedHelperArguments.FixedSwitch,
            "bad-pipe",
            new string('0', 64),
        ]));
    }
}

#pragma warning restore CA1515
