namespace Mixer.Audio.Dsp;

/// <summary>
/// Пик по L и R между двумя опросами. Аудиопоток пишет через CAS-максимум, интерфейс забирает и обнуляет.
/// Без блокировок.
/// </summary>
public sealed class PeakMeter
{
    private int _l;
    private int _r;

    /// <summary>Аудиопоток: учесть пики блока interleaved-стерео.</summary>
    public void Update(ReadOnlySpan<float> stereo, int frames)
    {
        float pl = 0, pr = 0;
        for (int i = 0, j = 0; i < frames; i++, j += 2)
        {
            float l = MathF.Abs(stereo[j]), r = MathF.Abs(stereo[j + 1]);
            if (l > pl) pl = l;
            if (r > pr) pr = r;
        }
        Max(ref _l, pl);
        Max(ref _r, pr);
    }

    /// <summary>Аудиопоток: пики блока, умноженные на усиление (сигнал, который ушёл дальше с этим усилением).</summary>
    public void UpdateScaled(ReadOnlySpan<float> stereo, int frames, float gain)
    {
        if (gain <= 0f) return;
        float pl = 0, pr = 0;
        for (int i = 0, j = 0; i < frames; i++, j += 2)
        {
            float l = MathF.Abs(stereo[j]), r = MathF.Abs(stereo[j + 1]);
            if (l > pl) pl = l;
            if (r > pr) pr = r;
        }
        Max(ref _l, pl * gain);
        Max(ref _r, pr * gain);
    }

    private static void Max(ref int slot, float value)
    {
        // для неотрицательных float порядок битов совпадает с порядком чисел
        int bits = BitConverter.SingleToInt32Bits(value);
        int cur = Volatile.Read(ref slot);
        while (bits > cur)
        {
            int seen = Interlocked.CompareExchange(ref slot, bits, cur);
            if (seen == cur) return;
            cur = seen;
        }
    }

    /// <summary>Интерфейс: забрать пики с прошлого опроса и обнулить.</summary>
    public (float L, float R) Take() =>
        (BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _l, 0)),
         BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _r, 0)));
}
