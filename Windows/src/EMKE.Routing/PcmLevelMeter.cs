namespace EMKE.Routing;

public sealed class PcmLevelMeter
{
    private readonly double _noiseFloor;
    private readonly double _ceiling;
    private readonly double _attackSeconds;
    private readonly double _releaseSeconds;

    public PcmLevelMeter(
        double noiseFloor = 0.0015,
        double ceiling = 0.1,
        double attackSeconds = 0.08,
        double releaseSeconds = 0.22)
    {
        if (!double.IsFinite(noiseFloor) || noiseFloor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(noiseFloor));
        }

        if (!double.IsFinite(ceiling) || ceiling < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ceiling));
        }

        if (!double.IsFinite(attackSeconds) || attackSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attackSeconds));
        }

        if (!double.IsFinite(releaseSeconds) || releaseSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseSeconds));
        }

        _noiseFloor = noiseFloor;
        _ceiling = ceiling;
        _attackSeconds = attackSeconds;
        _releaseSeconds = releaseSeconds;
    }

    public double Level { get; private set; }

    public double Observe(ReadOnlySpan<byte> pcm16, double sampleRate = 24_000)
    {
        if ((pcm16.Length & 1) != 0)
        {
            throw new ArgumentException(
                "PCM16 input must contain an even number of bytes.",
                nameof(pcm16));
        }

        if (!double.IsFinite(sampleRate) || sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        int sampleCount = pcm16.Length / sizeof(short);
        if (sampleCount == 0)
        {
            return Level;
        }

        double sumOfSquares = 0;
        for (int index = 0; index < pcm16.Length; index += 2)
        {
            ushort bits = (ushort)(pcm16[index] | (pcm16[index + 1] << 8));
            short sample = unchecked((short)bits);
            double normalized = sample / (double)short.MaxValue;
            sumOfSquares += normalized * normalized;
        }

        double rms = Math.Sqrt(sumOfSquares / sampleCount);
        double safeNoiseFloor = Math.Max(_noiseFloor, double.Epsilon);
        double safeCeiling = Math.Max(_ceiling, safeNoiseFloor);
        double floorDecibels = 20 * Math.Log10(safeNoiseFloor);
        double ceilingDecibels = 20 * Math.Log10(safeCeiling);
        double rmsDecibels = rms > 0
            ? 20 * Math.Log10(rms)
            : double.NegativeInfinity;
        double rangeDecibels = Math.Max(
            ceilingDecibels - floorDecibels,
            double.Epsilon);
        double target = Math.Clamp(
            (rmsDecibels - floorDecibels) / rangeDecibels,
            0,
            1);
        double duration = sampleCount / sampleRate;
        double timeConstant = target > Level
            ? _attackSeconds
            : _releaseSeconds;
        double alpha = 1 - Math.Exp(-duration / timeConstant);
        Level += (target - Level) * alpha;
        Level = Math.Clamp(Level, 0, 1);
        return Level;
    }

    public void Reset()
    {
        Level = 0;
    }
}
