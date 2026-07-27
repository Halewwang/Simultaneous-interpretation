using EMKE.Core;

namespace EMKE.Routing;

public sealed class InboundUtteranceBuffer
{
    public const int DefaultMaximumPcm16BytesPerCandidate = 240_000;
    public const int DefaultMaximumTranscriptCharacters = 16_384;

    private readonly InboundLanguageGate _gate;
    private readonly int _maximumPcm16BytesPerCandidate;
    private readonly int _maximumTranscriptCharacters;
    private readonly List<byte[]> _originalChunks = [];
    private readonly List<byte[]> _translatedChunks = [];
    private readonly char[] _transcript;
    private int _transcriptLength;
    private long _originalByteCount;
    private long _translatedByteCount;
    private bool _isActive;

    public InboundUtteranceBuffer(
        LanguageCode nativeLanguage,
        IClock clock,
        int maximumPcm16BytesPerCandidate =
            DefaultMaximumPcm16BytesPerCandidate,
        int maximumTranscriptCharacters =
            DefaultMaximumTranscriptCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumPcm16BytesPerCandidate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumTranscriptCharacters);

        _gate = new InboundLanguageGate(nativeLanguage, clock);
        _maximumPcm16BytesPerCandidate = maximumPcm16BytesPerCandidate;
        _maximumTranscriptCharacters = maximumTranscriptCharacters;
        _transcript = new char[maximumTranscriptCharacters];
    }

    public InboundGateDecision Decision => _gate.Snapshot.Decision;

    public long BufferedPcm16ByteCount =>
        checked(_originalByteCount + _translatedByteCount);

    public string Transcript => new(_transcript, 0, _transcriptLength);

    public void Begin()
    {
        Clear();
        _gate.Reset();
        _isActive = true;
    }

    public IReadOnlyList<byte[]> AppendOriginal(ReadOnlySpan<byte> pcm16)
    {
        ValidatePcm16(pcm16);
        EnsureActive();

        return Decision switch
        {
            InboundGateDecision.Original => [pcm16.ToArray()],
            InboundGateDecision.Translated => [],
            _ => BufferOriginal(pcm16),
        };
    }

    public IReadOnlyList<byte[]> AppendTranslation(ReadOnlySpan<byte> pcm16)
    {
        ValidatePcm16(pcm16);
        EnsureActive();

        return Decision switch
        {
            InboundGateDecision.Translated => [pcm16.ToArray()],
            InboundGateDecision.Original => [],
            _ => BufferTranslation(pcm16),
        };
    }

    public void AppendTranscript(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnsureActive();

        int remaining = _maximumTranscriptCharacters - _transcriptLength;
        if (remaining > 0)
        {
            int copyCount = Math.Min(text.Length, remaining);
            text.AsSpan(0, copyCount)
                .CopyTo(_transcript.AsSpan(_transcriptLength, copyCount));
            _transcriptLength += copyCount;
        }

        _gate.ObserveLateTranscript();
    }

    public IReadOnlyList<byte[]> Observe(
        IReadOnlyDictionary<string, double> confidenceByTag)
    {
        EnsureActive();
        _gate.Observe(confidenceByTag);
        return FlushSelectedCandidate();
    }

    public IReadOnlyList<byte[]> ResolveDecisionDeadline(bool voiced)
    {
        EnsureActive();
        _gate.ResolveDecisionDeadline(voiced);
        return FlushSelectedCandidate();
    }

    public IReadOnlyList<byte[]> Finish(bool voiced)
    {
        if (!_isActive)
        {
            return [];
        }

        if (Decision == InboundGateDecision.Undecided)
        {
            bool translatedAudioAvailable =
                voiced && _translatedChunks.Count > 0;
            _gate.ForceDecision(translatedAudioAvailable);
        }

        IReadOnlyList<byte[]> output = FlushSelectedCandidate();
        Clear();
        _gate.Reset();
        _isActive = false;
        return output;
    }

    public void Stop()
    {
        Reset();
    }

    public void Reset()
    {
        Clear();
        _gate.Reset();
        _isActive = false;
    }

    private static void ValidatePcm16(ReadOnlySpan<byte> pcm16)
    {
        if ((pcm16.Length & 1) != 0)
        {
            throw new ArgumentException(
                "PCM16 input must contain an even number of bytes.",
                nameof(pcm16));
        }
    }

    private byte[][] BufferOriginal(ReadOnlySpan<byte> pcm16)
    {
        int copyCount = BoundedCopyCount(_originalByteCount, pcm16.Length);
        if (copyCount > 0)
        {
            _originalChunks.Add(pcm16[..copyCount].ToArray());
            _originalByteCount = checked(_originalByteCount + copyCount);
        }

        _gate.ObserveLateAudio();
        if (_originalByteCount < _maximumPcm16BytesPerCandidate)
        {
            return [];
        }

        _gate.ForceDecision(voiced: false);
        return FlushSelectedCandidate();
    }

    private byte[][] BufferTranslation(ReadOnlySpan<byte> pcm16)
    {
        int copyCount = BoundedCopyCount(_translatedByteCount, pcm16.Length);
        if (copyCount > 0)
        {
            _translatedChunks.Add(pcm16[..copyCount].ToArray());
            _translatedByteCount = checked(_translatedByteCount + copyCount);
        }

        _gate.ObserveLateAudio();
        if (_translatedByteCount < _maximumPcm16BytesPerCandidate)
        {
            return [];
        }

        _gate.ForceDecision(voiced: true);
        return FlushSelectedCandidate();
    }

    private int BoundedCopyCount(long currentCount, int offeredCount)
    {
        long remaining = checked(
            (long)_maximumPcm16BytesPerCandidate - currentCount);
        return (int)Math.Min(remaining, offeredCount);
    }

    private byte[][] FlushSelectedCandidate()
    {
        if (Decision == InboundGateDecision.Undecided)
        {
            return [];
        }

        byte[][] output = (Decision == InboundGateDecision.Original
                ? _originalChunks
                : _translatedChunks)
            .Select(static chunk => chunk.ToArray())
            .ToArray();
        ClearPcm();
        return output;
    }

    private void EnsureActive()
    {
        if (!_isActive)
        {
            Begin();
        }
    }

    private void Clear()
    {
        ClearPcm();
        Array.Clear(_transcript);
        _transcriptLength = 0;
    }

    private void ClearPcm()
    {
        foreach (byte[] chunk in _originalChunks)
        {
            Array.Clear(chunk);
        }

        foreach (byte[] chunk in _translatedChunks)
        {
            Array.Clear(chunk);
        }

        _originalChunks.Clear();
        _translatedChunks.Clear();
        _originalByteCount = 0;
        _translatedByteCount = 0;
    }
}
