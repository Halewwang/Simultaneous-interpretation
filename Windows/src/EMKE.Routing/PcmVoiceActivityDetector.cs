namespace EMKE.Routing;

public enum PcmVoiceActivityEvent
{
    None,
    SpeechStarted,
    SpeechEnded,
}

public sealed class PcmVoiceActivityDetector
{
    private int _consecutiveSilentFrames;

    public PcmVoiceActivityDetector(
        double speechThreshold = 0.015,
        int silenceFrameLimit = 30)
    {
        if (!double.IsFinite(speechThreshold) || speechThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speechThreshold));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(silenceFrameLimit);

        SpeechThreshold = speechThreshold;
        SilenceFrameLimit = silenceFrameLimit;
    }

    public double SpeechThreshold { get; }

    public int SilenceFrameLimit { get; }

    public bool IsSpeaking { get; private set; }

    public PcmVoiceActivityEvent Observe(ReadOnlySpan<byte> pcm16)
    {
        if ((pcm16.Length & 1) != 0)
        {
            throw new ArgumentException(
                "PCM16 input must contain an even number of bytes.",
                nameof(pcm16));
        }

        if (pcm16.IsEmpty)
        {
            return PcmVoiceActivityEvent.None;
        }

        double squareSum = 0;
        for (int index = 0; index < pcm16.Length; index += 2)
        {
            ushort bits = (ushort)(pcm16[index] | (pcm16[index + 1] << 8));
            short sample = unchecked((short)bits);
            double normalized = sample / 32_768d;
            squareSum += normalized * normalized;
        }

        double rms = Math.Sqrt(squareSum / (pcm16.Length / 2d));
        if (rms >= SpeechThreshold)
        {
            _consecutiveSilentFrames = 0;
            if (!IsSpeaking)
            {
                IsSpeaking = true;
                return PcmVoiceActivityEvent.SpeechStarted;
            }

            return PcmVoiceActivityEvent.None;
        }

        if (!IsSpeaking)
        {
            return PcmVoiceActivityEvent.None;
        }

        _consecutiveSilentFrames++;
        if (_consecutiveSilentFrames < SilenceFrameLimit)
        {
            return PcmVoiceActivityEvent.None;
        }

        IsSpeaking = false;
        _consecutiveSilentFrames = 0;
        return PcmVoiceActivityEvent.SpeechEnded;
    }

    public void Reset()
    {
        IsSpeaking = false;
        _consecutiveSilentFrames = 0;
    }
}
