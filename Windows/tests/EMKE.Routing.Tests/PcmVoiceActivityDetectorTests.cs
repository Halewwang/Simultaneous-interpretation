namespace EMKE.Routing.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class PcmVoiceActivityDetectorTests
{
    [TestMethod]
    public void SpeechStartsOnceAndEndsAtTheConfiguredSilentFrameBoundary()
    {
        PcmVoiceActivityDetector detector = new(silenceFrameLimit: 3);

        Assert.AreEqual(
            PcmVoiceActivityEvent.SpeechStarted,
            detector.Observe(Pcm16(8_000)));
        Assert.AreEqual(PcmVoiceActivityEvent.None, detector.Observe(Pcm16(8_000)));
        Assert.AreEqual(PcmVoiceActivityEvent.None, detector.Observe(Pcm16(0)));
        Assert.AreEqual(PcmVoiceActivityEvent.None, detector.Observe(Pcm16(0)));
        Assert.AreEqual(
            PcmVoiceActivityEvent.SpeechEnded,
            detector.Observe(Pcm16(0)));
        Assert.IsFalse(detector.IsSpeaking);
    }

    [TestMethod]
    public void OddPcm16IsRejectedWithoutMutatingSpeakingState()
    {
        PcmVoiceActivityDetector detector = new();
        _ = detector.Observe(Pcm16(8_000));

        Assert.ThrowsExactly<ArgumentException>(
            () => detector.Observe(new byte[] { 1 }));
        Assert.IsTrue(detector.IsSpeaking);
    }

    [TestMethod]
    public void ResetClearsSpeechAndSilenceHistory()
    {
        PcmVoiceActivityDetector detector = new(silenceFrameLimit: 2);
        _ = detector.Observe(Pcm16(8_000));
        _ = detector.Observe(Pcm16(0));

        detector.Reset();

        Assert.IsFalse(detector.IsSpeaking);
        Assert.AreEqual(PcmVoiceActivityEvent.None, detector.Observe(Pcm16(0)));
    }

    private static byte[] Pcm16(short amplitude, int sampleCount = 240)
    {
        byte[] result = new byte[sampleCount * sizeof(short)];
        for (int index = 0; index < sampleCount; index++)
        {
            result[index * 2] = (byte)amplitude;
            result[(index * 2) + 1] = (byte)(amplitude >> 8);
        }

        return result;
    }
}

#pragma warning restore CA1515
