using System.Text.Json;

namespace EMKE.Realtime.Tests;

#pragma warning disable CA1515 // MSTest requires discoverable public test classes.
#pragma warning disable CA2007 // MSTest owns the test synchronization context.

[TestClass]
public sealed class PcmFrameBatcherTests
{
    [TestMethod]
    public async Task SharedPcmBatchingFixtureIsExecutedByTheOwnedBatcher()
    {
        using JsonDocument fixture = LoadFixture("Audio", "pcm-batching.json");
        Assert.AreEqual(
            PcmFrameBatcher.FrameBytes,
            fixture.RootElement
                .GetProperty("metadata")
                .GetProperty("networkBatch")
                .GetProperty("byteCount")
                .GetInt32());

        foreach (JsonElement fixtureCase in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            PcmFrameBatcher batcher = new();
            List<int> emitted = [];
            JsonElement input = fixtureCase.GetProperty("input");

            foreach (JsonElement appendByteCount in input.GetProperty("appendByteCounts").EnumerateArray())
            {
                int byteCount = appendByteCount.GetInt32();
                if (fixtureCase.GetProperty("expected").TryGetProperty("errorCode", out JsonElement errorCode))
                {
                    PcmFrameBatcherException exception =
                        await Assert.ThrowsExactlyAsync<PcmFrameBatcherException>(
                            () => batcher.AppendAsync(
                                new byte[byteCount],
                                CaptureAsync,
                                CancellationToken.None).AsTask());
                    Assert.AreEqual(errorCode.GetString(), exception.Error.Code);
                }
                else
                {
                    await batcher.AppendAsync(
                        new byte[byteCount],
                        CaptureAsync,
                        CancellationToken.None);
                }
            }

            JsonElement expected = fixtureCase.GetProperty("expected");
            int[] expectedFrames = expected.TryGetProperty(
                "emittedFrameByteCounts",
                out JsonElement emittedFrameByteCounts)
                ? emittedFrameByteCounts
                    .EnumerateArray()
                    .Select(static element => element.GetInt32())
                    .ToArray()
                : [];
            CollectionAssert.AreEqual(expectedFrames, emitted);

            if (expected.TryGetProperty("retainedByteCountBeforeFlush", out JsonElement retainedBefore))
            {
                Assert.AreEqual(retainedBefore.GetInt32(), batcher.RetainedByteCount);
                Assert.AreEqual(
                    expected.GetProperty("discardedByteCount").GetInt32(),
                    batcher.Stop());
                Assert.AreEqual(
                    expected.GetProperty("retainedByteCountAfterFlush").GetInt32(),
                    batcher.RetainedByteCount);
            }
            else
            {
                Assert.AreEqual(
                    expected.GetProperty("retainedByteCount").GetInt32(),
                    batcher.RetainedByteCount);
            }

            ValueTask CaptureAsync(ReadOnlyMemory<byte> frame, CancellationToken _)
            {
                emitted.Add(frame.Length);
                return ValueTask.CompletedTask;
            }
        }
    }

    [TestMethod]
    public async Task OddAppendIsRejectedBeforeMutatingExistingTail()
    {
        PcmFrameBatcher batcher = new();
        await batcher.AppendAsync(
            new byte[2400],
            static (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);

        PcmFrameBatcherException exception =
            await Assert.ThrowsExactlyAsync<PcmFrameBatcherException>(
                () => batcher.AppendAsync(
                    new byte[3],
                    static (_, _) => ValueTask.CompletedTask,
                    CancellationToken.None).AsTask());

        Assert.AreEqual("invalidPCM16ByteCount", exception.Error.Code);
        Assert.AreEqual(2400, batcher.RetainedByteCount);
    }

    [TestMethod]
    public async Task LargeAppendAwaitsEachFrameBeforeReusingItsOnlyBuffer()
    {
        PcmFrameBatcher batcher = new();
        byte[] source = Enumerable.Range(0, PcmFrameBatcher.FrameBytes * 2 + 200)
            .Select(static value => (byte)value)
            .ToArray();
        List<byte[]> observed = [];
        TaskCompletionSource firstFrameMayComplete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        Task append = batcher.AppendAsync(source, CaptureAsync, CancellationToken.None).AsTask();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);

        Assert.IsFalse(append.IsCompleted);
        CollectionAssert.AreEqual(source[..PcmFrameBatcher.FrameBytes], observed[0]);

        firstFrameMayComplete.SetResult();
        await append;

        Assert.AreEqual(2, calls);
        CollectionAssert.AreEqual(
            source[PcmFrameBatcher.FrameBytes..(PcmFrameBatcher.FrameBytes * 2)],
            observed[1]);
        Assert.AreEqual(200, batcher.RetainedByteCount);

        async ValueTask CaptureAsync(
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken)
        {
            observed.Add(frame.ToArray());
            if (Interlocked.Increment(ref calls) == 1)
            {
                await firstFrameMayComplete.Task.WaitAsync(cancellationToken);
            }
        }
    }

    [TestMethod]
    public async Task ConcurrentAppendsAreSerializedAndCancellationRetainsCompletedFrame()
    {
        PcmFrameBatcher batcher = new();
        TaskCompletionSource sinkEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSink =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource canceled = new();

        Task first = batcher.AppendAsync(
            new byte[PcmFrameBatcher.FrameBytes],
            async (_, cancellationToken) =>
            {
                sinkEntered.SetResult();
                await releaseSink.Task.WaitAsync(cancellationToken);
            },
            CancellationToken.None).AsTask();
        await sinkEntered.Task;

        Task second = batcher.AppendAsync(
            new byte[200],
            static (_, _) => ValueTask.CompletedTask,
            canceled.Token).AsTask();
        await canceled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => second);

        releaseSink.SetResult();
        await first;
        Assert.AreEqual(0, batcher.RetainedByteCount);
    }

    [TestMethod]
    public async Task SinkFailureDropsAndZerosAttemptedFrameWithoutRepeatingIt()
    {
        PcmFrameBatcher batcher = new();
        ReadOnlyMemory<byte> attempted = default;

        await Assert.ThrowsExactlyAsync<IOException>(
            () => batcher.AppendAsync(
                new byte[PcmFrameBatcher.FrameBytes],
                (frame, _) =>
                {
                    attempted = frame;
                    return ValueTask.FromException(new IOException("safe failure"));
                },
                CancellationToken.None).AsTask());

        Assert.AreEqual(0, batcher.RetainedByteCount);
        Assert.IsTrue(attempted.Span.ToArray().All(static value => value == 0));

        int sends = 0;
        await batcher.AppendAsync(
            new byte[PcmFrameBatcher.FrameBytes],
            (_, _) =>
            {
                sends++;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        Assert.AreEqual(1, sends);
        Assert.AreEqual(0, batcher.RetainedByteCount);
    }

    [TestMethod]
    public async Task SinkCancellationDropsAttemptedFrameAndNextAppendDoesNotRepeatIt()
    {
        PcmFrameBatcher batcher = new();
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => batcher.AppendAsync(
                new byte[PcmFrameBatcher.FrameBytes],
                async (frame, cancellationToken) =>
                {
                    Assert.AreEqual(PcmFrameBatcher.FrameBytes, frame.Length);
                    await cancellation.CancelAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                },
                cancellation.Token).AsTask());

        Assert.AreEqual(0, batcher.RetainedByteCount);
        int nextSends = 0;
        await batcher.AppendAsync(
            new byte[PcmFrameBatcher.FrameBytes],
            (_, _) =>
            {
                nextSends++;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        Assert.AreEqual(1, nextSends);
    }

    [TestMethod]
    public async Task PartialFrameBeforeSinkRemainsRetainedWhenNextAppendIsCanceled()
    {
        PcmFrameBatcher batcher = new();
        await batcher.AppendAsync(
            new byte[2400],
            static (_, _) => throw new InvalidOperationException("sink must not run"),
            CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => batcher.AppendAsync(
                new byte[200],
                static (_, _) => throw new InvalidOperationException("sink must not run"),
                cancellation.Token).AsTask());

        Assert.AreEqual(2400, batcher.RetainedByteCount);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static JsonDocument LoadFixture(string category, string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth <= 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "Shared",
                "TestVectors",
                category,
                fileName);
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllBytes(candidate));
            }
        }

        throw new FileNotFoundException($"Unable to locate Shared/TestVectors/{category}/{fileName}.");
    }
}
