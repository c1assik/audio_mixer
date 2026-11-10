namespace Mixer.Probe.Audio;

/// <summary>
/// Уровень и поступление кадров, разложенные по корзинам 100 мс от общего секундомера.
/// Пишет только поток захвата, читать после остановки.
/// </summary>
public sealed class LevelStats
{
    public const double BucketSeconds = 0.1;

    private readonly long[] _frames;
    private readonly double[] _sumSquares;
    private readonly float[] _peak;
    private readonly int[] _packets;
    private readonly int[] _silentPackets;
    private readonly int[] _discontinuities;
    private readonly int _channels;
    private int _nonFinite;

    public LevelStats(double maxSeconds, int channels)
    {
        int n = (int)Math.Ceiling(maxSeconds / BucketSeconds) + 1;
        _frames = new long[n];
        _sumSquares = new double[n];
        _peak = new float[n];
        _packets = new int[n];
        _silentPackets = new int[n];
        _discontinuities = new int[n];
        _channels = channels;
    }

    public void Add(double timeSeconds, int frames, ReadOnlySpan<float> samples, bool silent, bool discontinuity)
    {
        int b = Math.Clamp((int)(timeSeconds / BucketSeconds), 0, _frames.Length - 1);
        _frames[b] += frames;
        _packets[b]++;
        if (silent) _silentPackets[b]++;
        if (discontinuity) _discontinuities[b]++;

        double sum = 0;
        float peak = _peak[b];
        foreach (float v in samples)
        {
            if (!float.IsFinite(v))
            {
                _nonFinite++;
                continue;
            }
            sum += (double)v * v;
            float a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        _sumSquares[b] += sum;
        _peak[b] = peak;
    }

    public int NonFiniteSamples => _nonFinite;

    public Summary Summarize(double from, double to)
    {
        long frames = 0;
        double sumSq = 0;
        float peak = 0;
        int packets = 0, silent = 0, discont = 0, buckets = 0;
        // Берём только корзины, целиком попавшие в окно, и длительность считаем по ним же, иначе кадры/с смещены.
        for (int b = 0; b < _frames.Length; b++)
        {
            double t = b * BucketSeconds;
            if (t < from - 1e-9 || t + BucketSeconds > to + 1e-9) continue;
            buckets++;
            frames += _frames[b];
            sumSq += _sumSquares[b];
            peak = Math.Max(peak, _peak[b]);
            packets += _packets[b];
            silent += _silentPackets[b];
            discont += _discontinuities[b];
        }
        long samples = frames * _channels;
        double rmsDb = samples > 0 && sumSq > 0 ? 10 * Math.Log10(sumSq / samples) : double.NegativeInfinity;
        double peakDb = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        return new Summary(buckets * BucketSeconds, frames, rmsDb, peakDb, packets, silent, discont);
    }
}

public sealed record Summary(
    double Seconds,
    long Frames,
    double RmsDb,
    double PeakDb,
    int Packets,
    int SilentPackets,
    int Discontinuities)
{
    public double FramesPerSecond => Seconds > 0 ? Frames / Seconds : 0;
}
