using EMKE.Core;

namespace EMKE.Routing.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.

[TestClass]
public sealed class InboundUtteranceBufferTests
{
    [TestMethod]
    public void NativeDecisionFlushesOnlyOriginalAndLocksTheRoute()
    {
        InboundUtteranceBuffer buffer = new(
            LanguageCode.Zh,
            new FixedClock(),
            maximumPcm16BytesPerCandidate: 16,
            maximumTranscriptCharacters: 16);
        buffer.Begin();

        Assert.IsEmpty(buffer.AppendOriginal(new byte[] { 1, 1 }));
        Assert.IsEmpty(buffer.AppendTranslation(new byte[] { 2, 2 }));
        IReadOnlyList<byte[]> output =
            buffer.Observe(new Dictionary<string, double> { ["zh"] = 0.9 });

        Assert.HasCount(1, output);
        CollectionAssert.AreEqual(new byte[] { 1, 1 }, output[0]);
        Assert.HasCount(1, buffer.AppendOriginal(new byte[] { 3, 3 }));
        Assert.IsEmpty(buffer.AppendTranslation(new byte[] { 4, 4 }));
    }

    [TestMethod]
    public void ForeignDecisionFlushesOnlyTranslation()
    {
        InboundUtteranceBuffer buffer = new(LanguageCode.Zh, new FixedClock());
        buffer.Begin();
        _ = buffer.AppendOriginal(new byte[] { 1, 1 });
        _ = buffer.AppendTranslation(new byte[] { 2, 2 });

        IReadOnlyList<byte[]> output =
            buffer.Observe(new Dictionary<string, double> { ["de"] = 0.8 });

        Assert.HasCount(1, output);
        CollectionAssert.AreEqual(new byte[] { 2, 2 }, output[0]);
    }

    [TestMethod]
    public void PcmCapacityFailsOpenWithoutGrowingAndOddInputIsRejectedFirst()
    {
        InboundUtteranceBuffer buffer = new(
            LanguageCode.En,
            new FixedClock(),
            maximumPcm16BytesPerCandidate: 4,
            maximumTranscriptCharacters: 8);
        buffer.Begin();
        _ = buffer.AppendOriginal(new byte[] { 1, 1 });

        Assert.ThrowsExactly<ArgumentException>(
            () => buffer.AppendOriginal(new byte[] { 2 }));
        Assert.AreEqual(2, buffer.BufferedPcm16ByteCount);

        IReadOnlyList<byte[]> output =
            buffer.AppendOriginal(new byte[] { 3, 3 });
        Assert.HasCount(2, output);
        CollectionAssert.AreEqual(new byte[] { 1, 1 }, output[0]);
        CollectionAssert.AreEqual(new byte[] { 3, 3 }, output[1]);
        Assert.AreEqual(InboundGateDecision.Original, buffer.Decision);
        Assert.AreEqual(0, buffer.BufferedPcm16ByteCount);
    }

    [TestMethod]
    public void OversizedSingleChunkIsBoundedBeforeAnyCandidateCopy()
    {
        InboundUtteranceBuffer buffer = new(
            LanguageCode.En,
            new FixedClock(),
            maximumPcm16BytesPerCandidate: 4,
            maximumTranscriptCharacters: 8);
        buffer.Begin();
        byte[] oversized = Enumerable.Range(0, 65_536)
            .Select(static value => (byte)value)
            .ToArray();

        IReadOnlyList<byte[]> output = buffer.AppendOriginal(oversized);

        Assert.HasCount(1, output);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 3 }, output[0]);
        Assert.AreEqual(0, buffer.BufferedPcm16ByteCount);
        Assert.AreEqual(InboundGateDecision.Original, buffer.Decision);
    }

    [TestMethod]
    public void CumulativeCandidateStopsExactlyAtTheHardPcmBoundary()
    {
        InboundUtteranceBuffer buffer = new(
            LanguageCode.Zh,
            new FixedClock(),
            maximumPcm16BytesPerCandidate: 6,
            maximumTranscriptCharacters: 8);
        buffer.Begin();
        _ = buffer.AppendTranslation(new byte[] { 1, 2, 3, 4 });

        IReadOnlyList<byte[]> output =
            buffer.AppendTranslation(new byte[] { 5, 6, 7, 8 });

        Assert.HasCount(2, output);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, output[0]);
        CollectionAssert.AreEqual(new byte[] { 5, 6 }, output[1]);
        Assert.AreEqual(0, buffer.BufferedPcm16ByteCount);
        Assert.AreEqual(InboundGateDecision.Translated, buffer.Decision);
    }

    [TestMethod]
    public void TranscriptCapacityIsBoundedAndStopOrResetClearsAllMemory()
    {
        InboundUtteranceBuffer buffer = new(
            LanguageCode.De,
            new FixedClock(),
            maximumPcm16BytesPerCandidate: 8,
            maximumTranscriptCharacters: 5);
        buffer.Begin();
        buffer.AppendTranscript("abc");
        buffer.AppendTranscript("def");
        _ = buffer.AppendOriginal(new byte[] { 1, 1 });
        _ = buffer.AppendTranslation(new byte[] { 2, 2 });

        Assert.AreEqual("abcde", buffer.Transcript);
        Assert.AreEqual(4, buffer.BufferedPcm16ByteCount);

        buffer.Stop();
        Assert.AreEqual(string.Empty, buffer.Transcript);
        Assert.AreEqual(0, buffer.BufferedPcm16ByteCount);
        Assert.AreEqual(InboundGateDecision.Undecided, buffer.Decision);

        buffer.Begin();
        buffer.AppendTranscript("xy");
        buffer.Reset();
        Assert.AreEqual(string.Empty, buffer.Transcript);
        Assert.AreEqual(0, buffer.BufferedPcm16ByteCount);
    }

    private sealed class FixedClock : IClock
    {
        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}

#pragma warning restore CA1515
