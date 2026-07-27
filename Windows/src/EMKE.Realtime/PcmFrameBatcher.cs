using System.Security.Cryptography;
using EMKE.Core;

namespace EMKE.Realtime;

#pragma warning disable CA1032 // Domain exceptions require a stable RuntimeError payload.

public sealed class PcmFrameBatcherException : Exception
{
    public PcmFrameBatcherException(RuntimeError error)
        : base(error?.Code)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public RuntimeError Error { get; }
}

#pragma warning restore CA1032
#pragma warning disable CA1001 // SemaphoreSlim owns no unmanaged resource and lives with the batcher.

public sealed class PcmFrameBatcher
{
    public const int FrameBytes = 9_600;

    private readonly byte[] _frame = GC.AllocateUninitializedArray<byte>(FrameBytes);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _offset;

    public int RetainedByteCount => Volatile.Read(ref _offset);

    public async ValueTask AppendAsync(
        ReadOnlyMemory<byte> pcm16,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if ((pcm16.Length & 1) != 0)
        {
            throw new PcmFrameBatcherException(Error(
                ErrorCategory.Protocol,
                "invalidPCM16ByteCount"));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int sourceOffset = 0;
            while (_offset == FrameBytes || sourceOffset < pcm16.Length)
            {
                if (_offset < FrameBytes)
                {
                    int copyCount = Math.Min(
                        FrameBytes - _offset,
                        pcm16.Length - sourceOffset);
                    pcm16.Span.Slice(sourceOffset, copyCount)
                        .CopyTo(_frame.AsSpan(_offset, copyCount));
                    sourceOffset += copyCount;
                    _offset += copyCount;
                }

                if (_offset == FrameBytes)
                {
                    await sink(_frame, cancellationToken).ConfigureAwait(false);
                    CryptographicOperations.ZeroMemory(_frame);
                    _offset = 0;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public int Stop()
    {
        return Discard();
    }

    public async ValueTask<int> DiscardAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ClearRetained();
        }
        finally
        {
            _gate.Release();
        }
    }

    public int Discard()
    {
        _gate.Wait();
        try
        {
            return ClearRetained();
        }
        finally
        {
            _gate.Release();
        }
    }

    private int ClearRetained()
    {
        int discarded = _offset;
        CryptographicOperations.ZeroMemory(_frame);
        _offset = 0;
        return discarded;
    }

    private static RuntimeError Error(ErrorCategory category, string code)
    {
        return new RuntimeError(
            category,
            code,
            new Dictionary<string, string>(),
            RecoveryAction.Retry);
    }
}

#pragma warning restore CA1001
