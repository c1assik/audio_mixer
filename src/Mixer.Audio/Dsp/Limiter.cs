namespace Mixer.Audio.Dsp;

/// <summary>
/// Пиковый ограничитель на выходе, связанный по каналам: порог −1 dBFS, атака 1 мс, восстановление 100 мс.
/// Огибающая ловит пик мгновенно и спадает со временем восстановления; усиление тянется к порог/огибающая
/// с постоянной атаки. Lookahead нет (задержка не растёт), поэтому первую ~1 мс резкого фронта может пройти
/// выше порога; за ней стоит жёсткий потолок ±1,0, так что в устройство полная шкала не превышается.
/// </summary>
public struct Limiter
{
    public const float ThresholdDb = -1f;
    public const double AttackSeconds = 0.001;
    public const double ReleaseSeconds = 0.100;

    private static readonly float Threshold = MathF.Pow(10f, ThresholdDb / 20f);

    private float _attack;
    private float _release;
    private float _env;
    private float _gain;

    public void Init(int sampleRate)
    {
        _attack = (float)Math.Exp(-1.0 / (AttackSeconds * sampleRate));
        _release = (float)Math.Exp(-1.0 / (ReleaseSeconds * sampleRate));
        _env = 0;
        _gain = 1;
    }

    /// <summary>Обрабатывает блок на месте. Возвращает минимальное усиление ограничителя за блок (1 — не сработал).</summary>
    public float Process(Span<float> stereo, int frames)
    {
        float env = _env, g = _gain, minGain = 1f;
        float a = _attack, rel = _release, thr = Threshold;
        for (int i = 0, j = 0; i < frames; i++, j += 2)
        {
            float l = stereo[j], r = stereo[j + 1];
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            env = MathF.Max(peak, env * rel);
            float target = env > thr ? thr / env : 1f;
            // вниз — с атакой 1 мс, вверх — сразу (спад огибающей уже даёт восстановление 100 мс)
            g = target < g ? a * g + (1 - a) * target : target;
            if (g < 1f)
            {
                if (g < minGain) minGain = g;
                l *= g;
                r *= g;
            }
            stereo[j] = Math.Clamp(l, -1f, 1f);
            stereo[j + 1] = Math.Clamp(r, -1f, 1f);
        }
        // денормалы на длинной тишине
        _env = env < 1e-20f ? 0f : env;
        _gain = g;
        return minGain;
    }
}
