namespace Mixer.Audio.Engine;

/// <summary>
/// Трассировка событий провода для разбора на реальных устройствах. Массивы выделены заранее,
/// писатель и читатель пишут каждый в свои; когда место кончилось, запись прекращается. Не для продакшена.
/// </summary>
public sealed class WireTrace
{
    public readonly long[] WriteNow, WriteIdeal;
    public readonly int[] WriteFrames;
    public int WriteCount;

    public readonly long[] ReadNow, ReadIdeal;
    public readonly int[] ReadFrames, ReadAvail;
    public readonly double[] ReadVirtual, ReadEps;
    public int ReadCount;

    public WireTrace(int capacity)
    {
        WriteNow = new long[capacity];
        WriteIdeal = new long[capacity];
        WriteFrames = new int[capacity];
        ReadNow = new long[capacity];
        ReadIdeal = new long[capacity];
        ReadFrames = new int[capacity];
        ReadAvail = new int[capacity];
        ReadVirtual = new double[capacity];
        ReadEps = new double[capacity];
    }

    internal void OnWrite(long now, long ideal, int frames)
    {
        int i = WriteCount;
        if (i >= WriteNow.Length) return;
        WriteNow[i] = now;
        WriteIdeal[i] = ideal;
        WriteFrames[i] = frames;
        Volatile.Write(ref WriteCount, i + 1);
    }

    internal void OnRead(long now, long ideal, int frames, int avail, double virt, double eps)
    {
        int i = ReadCount;
        if (i >= ReadNow.Length) return;
        ReadNow[i] = now;
        ReadIdeal[i] = ideal;
        ReadFrames[i] = frames;
        ReadAvail[i] = avail;
        ReadVirtual[i] = virt;
        ReadEps[i] = eps;
        Volatile.Write(ref ReadCount, i + 1);
    }
}
