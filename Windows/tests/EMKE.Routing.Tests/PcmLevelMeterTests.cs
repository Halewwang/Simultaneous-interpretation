namespace EMKE.Routing.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class PcmLevelMeterTests
{
    [TestMethod]
    public void Default24KhzLevelIsDeterministicAndNormalized()
    {
        PcmLevelMeter first = new();
        PcmLevelMeter second = new();
        byte[] pcm16 = Pcm16(12_000);

        double firstLevel = first.Observe(pcm16);
        double secondLevel = second.Observe(pcm16);

        Assert.IsGreaterThan(0, firstLevel);
        Assert.IsLessThanOrEqualTo(1, firstLevel);
        Assert.AreEqual(firstLevel, secondLevel, 0.000_000_1);
    }

    [TestMethod]
    public void AttackIsFasterThanReleaseAndSilenceDecaysWithoutJumping()
    {
        PcmLevelMeter meter = new();
        double attacked = meter.Observe(Pcm16(18_000));
        double released = meter.Observe(Pcm16(0));

        Assert.IsGreaterThan(0, attacked);
        Assert.IsGreaterThan(0, released);
        Assert.IsLessThan(attacked, released);
    }

    [TestMethod]
    public void OddBytesAndNonPositiveSampleRatesAreRejectedWithoutMutation()
    {
        PcmLevelMeter meter = new();
        double before = meter.Observe(Pcm16(12_000));

        Assert.ThrowsExactly<ArgumentException>(() => meter.Observe(new byte[] { 1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => meter.Observe(Pcm16(1), sampleRate: 0));
        Assert.AreEqual(before, meter.Level);
    }

    [TestMethod]
    public void ResetReturnsTheSmoothedLevelToZero()
    {
        PcmLevelMeter meter = new();
        _ = meter.Observe(Pcm16(18_000));

        meter.Reset();

        Assert.AreEqual(0, meter.Level);
    }

    private static byte[] Pcm16(short amplitude, int sampleCount = 240)
    {
        byte[] result = new byte[sampleCount * sizeof(short)];
        for (int index = 0; index < sampleCount; index++)
        {
            short sample = index % 2 == 0 ? amplitude : (short)-amplitude;
            result[index * 2] = (byte)sample;
            result[(index * 2) + 1] = (byte)(sample >> 8);
        }

        return result;
    }
}

#pragma warning restore CA1515
