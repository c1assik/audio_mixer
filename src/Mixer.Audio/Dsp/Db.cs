namespace Mixer.Audio.Dsp;

public static class Db
{
    /// <summary>Нижняя граница фейдера: ниже неё — тишина.</summary>
    public const float Min = -60f;
    public const float Max = 12f;

    /// <summary>дБ фейдера → линейное усиление. ≤ −60 дБ — тишина, выше +12 дБ обрезается.</summary>
    public static float ToGain(float db)
    {
        if (float.IsNaN(db) || db <= Min) return 0f;
        if (db > Max) db = Max;
        return MathF.Pow(10f, db / 20f);
    }

    /// <summary>Линейная амплитуда → дБ (−∞ для нуля).</summary>
    public static double FromGain(double gain) => gain <= 0 ? double.NegativeInfinity : 20 * Math.Log10(gain);
}
