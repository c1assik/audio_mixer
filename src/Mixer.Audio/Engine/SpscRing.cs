namespace Mixer.Audio.Engine;

/// <summary>
/// Кольцо стерео-кадров float на одного писателя и одного читателя, без блокировок.
/// Писатель — поток захвата источника, читатель — поток воспроизведения выхода.
/// Счётчики позиций 64-битные и только растут, индекс в массиве — по маске.
/// </summary>
public sealed class SpscRing
{
    private readonly float[] _buf; // interleaved L,R
    private readonly int _mask;    // в кадрах
    private long _write;           // пишет только писатель
    private long _read;            // пишет только читатель

    /// <summary>Физическая ёмкость в кадрах (степень двойки).</summary>
    public int Capacity { get; }

    /// <summary>Логическая ёмкость: больше писатель не пишет (кадры отбрасываются).</summary>
    public int Limit { get; set; }

    public SpscRing(int capacityFrames)
    {
        Capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(capacityFrames, 16));
        _mask = Capacity - 1;
        _buf = new float[Capacity * 2];
        Limit = Capacity;
    }

    public long WritePosition => Volatile.Read(ref _write);

    /// <summary>Сколько кадров можно прочитать (для читателя).</summary>
    public int Available => (int)(Volatile.Read(ref _write) - _read);

    // ── писатель ────────────────────────────────────────────────

    /// <summary>Пишет до frames кадров; возвращает, сколько поместилось (остальное отброшено).</summary>
    public int Write(ReadOnlySpan<float> stereo, int frames)
    {
        long w = _write;
        int used = (int)(w - Volatile.Read(ref _read));
        int free = Math.Min(Limit, Capacity) - used;
        int n = Math.Min(frames, Math.Max(free, 0));
        if (n == 0) return 0;

        int start = (int)(w & _mask);
        int first = Math.Min(n, Capacity - start);
        stereo[..(first * 2)].CopyTo(_buf.AsSpan(start * 2));
        if (first < n) stereo.Slice(first * 2, (n - first) * 2).CopyTo(_buf.AsSpan(0));
        Volatile.Write(ref _write, w + n);
        return n;
    }

    // ── читатель ────────────────────────────────────────────────

    /// <summary>Читает frames кадров в раздельные массивы L и R. Вызывающий проверил Available.</summary>
    public void ReadPlanar(Span<float> left, Span<float> right, int frames)
    {
        long r = _read;
        int pos = (int)(r & _mask);
        for (int i = 0; i < frames; i++)
        {
            int j = pos * 2;
            left[i] = _buf[j];
            right[i] = _buf[j + 1];
            pos = (pos + 1) & _mask;
        }
        Volatile.Write(ref _read, r + frames);
    }

    /// <summary>Читает frames кадров interleaved. Вызывающий проверил Available.</summary>
    public void Read(Span<float> stereo, int frames)
    {
        long r = _read;
        int start = (int)(r & _mask);
        int first = Math.Min(frames, Capacity - start);
        _buf.AsSpan(start * 2, first * 2).CopyTo(stereo);
        if (first < frames) _buf.AsSpan(0, (frames - first) * 2).CopyTo(stereo[(first * 2)..]);
        Volatile.Write(ref _read, r + frames);
    }

    public void Skip(int frames)
    {
        frames = Math.Min(frames, Available);
        Volatile.Write(ref _read, _read + frames);
    }

    /// <summary>Выбросить всё накопленное (только читатель).</summary>
    public void Flush() => Volatile.Write(ref _read, Volatile.Read(ref _write));
}
