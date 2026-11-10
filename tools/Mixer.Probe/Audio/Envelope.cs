using System.Diagnostics;

namespace Mixer.Probe.Audio;

/// <summary>Время по QPC в единицах 100 нс: в них же GetBuffer отдаёт qpcPosition пакета.</summary>
public static class Qpc
{
    public static ulong Now100ns => To100ns(Stopwatch.GetTimestamp());

    public static ulong To100ns(long stopwatchTicks) => (ulong)(stopwatchTicks * (10_000_000.0 / Stopwatch.Frequency));

    public static double Ms(ulong from100ns, ulong to100ns) => ((double)to100ns - from100ns) / 10_000.0;
}

/// <summary>
/// Огибающая RMS по окнам фиксированной длины с отметкой времени по QPC (время первого кадра окна).
/// Пишет поток захвата (через <see cref="CaptureStream.OnPacket"/>), читать список после остановки.
/// Последнее окно доступно сразу, через <see cref="LatestDb"/>.
/// </summary>
public sealed class Envelope
{
    private readonly int _windowFrames;
    private readonly int _channels;
    private readonly int _rate;
    private double _sum;
    private int _filled;
    private ulong _windowStart;
    private long _latestBits = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);

    public double WindowMs { get; }
    public List<(ulong T100ns, float Db)> Windows { get; }

    public Envelope(int sampleRate, int channels, double windowMs, double maxSeconds)
    {
        _rate = sampleRate;
        _channels = channels;
        WindowMs = windowMs;
        _windowFrames = Math.Max(1, (int)Math.Round(sampleRate * windowMs / 1000));
        Windows = new List<(ulong, float)>((int)(maxSeconds * 1000 / windowMs) + 16);
    }

    /// <summary>Уровень последнего законченного окна, dBFS.</summary>
    public double LatestDb => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestBits));

    public void Add(in PacketInfo packet, ReadOnlySpan<float> samples)
    {
        int frames = samples.Length / _channels;
        for (int i = 0; i < frames; i++)
        {
            if (_filled == 0) _windowStart = packet.Qpc100ns + (ulong)(i * 10_000_000.0 / _rate);
            for (int c = 0; c < _channels; c++)
            {
                float v = samples[i * _channels + c];
                _sum += (double)v * v;
            }
            if (++_filled == _windowFrames)
            {
                double ms = _sum / (_windowFrames * _channels);
                float db = ms > 0 ? (float)(10 * Math.Log10(ms)) : float.NegativeInfinity;
                if (Windows.Count < Windows.Capacity) Windows.Add((_windowStart, db));
                Interlocked.Exchange(ref _latestBits, BitConverter.DoubleToInt64Bits(db));
                _sum = 0;
                _filled = 0;
            }
        }
    }

    /// <summary>Медиана уровня окон в интервале [from, to).</summary>
    public double Median(ulong from, ulong to)
    {
        var v = Windows.Where(w => w.T100ns >= from && w.T100ns < to).Select(w => (double)w.Db).OrderBy(d => d).ToList();
        return v.Count == 0 ? double.NaN : v[v.Count / 2];
    }
}

/// <summary>Кольцо стерео-кадров float с блокировкой: для утилиты простота важнее, чем lock-free.</summary>
public sealed class FloatRing
{
    private readonly float[] _buf;
    private readonly object _lock = new();
    private int _read, _count;

    public FloatRing(int capacityFrames) => _buf = new float[capacityFrames * 2];

    public int CapacityFrames => _buf.Length / 2;

    public int AvailableFrames
    {
        get
        {
            lock (_lock) return _count / 2;
        }
    }

    /// <summary>Пишет интерливинг-стерео; при переполнении теряются самые старые кадры.</summary>
    public void Write(ReadOnlySpan<float> stereo)
    {
        lock (_lock)
        {
            foreach (float v in stereo)
            {
                int w = (_read + _count) % _buf.Length;
                _buf[w] = v;
                if (_count < _buf.Length) _count++;
                else _read = (_read + 1) % _buf.Length;
            }
        }
    }

    /// <summary>Добавляет в dst (stereo) сколько есть; возвращает число прочитанных кадров.</summary>
    public int ReadAdd(Span<float> dst)
    {
        lock (_lock)
        {
            int n = Math.Min(dst.Length, _count) & ~1;
            for (int i = 0; i < n; i++)
            {
                dst[i] += _buf[_read];
                _read = (_read + 1) % _buf.Length;
            }
            _count -= n;
            return n / 2;
        }
    }

    /// <summary>Отбрасывает старые кадры, оставляя не больше keepFrames самых свежих.</summary>
    public int TrimTo(int keepFrames)
    {
        lock (_lock)
        {
            int drop = Math.Max(0, _count - keepFrames * 2);
            _read = (_read + drop) % _buf.Length;
            _count -= drop;
            return drop / 2;
        }
    }
}
