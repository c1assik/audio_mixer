namespace Mixer.Audio.Dsp;

/// <summary>
/// Линейная рампа одного множителя. Цель меняется в начале блока; рампа идёт ровно rampFrames кадров.
/// </summary>
public struct Ramp
{
    private float _current;
    private float _target;
    private float _step;
    private int _remaining;
    private int _rampFrames;

    public readonly float Current => _current;
    public readonly bool IsSettled => _remaining == 0;

    public void Init(int rampFrames, float value)
    {
        _rampFrames = Math.Max(1, rampFrames);
        _current = _target = value;
        _step = 0;
        _remaining = 0;
    }

    public void SetTarget(float target)
    {
        if (target == _target) return;
        _target = target;
        _remaining = _rampFrames;
        _step = (target - _current) / _rampFrames;
    }

    /// <summary>Следующее значение рампы (на кадр).</summary>
    public float Next()
    {
        if (_remaining == 0) return _current;
        if (--_remaining == 0) _current = _target;
        else _current += _step;
        return _current;
    }
}

/// <summary>
/// Усиление стерео-сигнала: рампа усиления за 10 мс (отдельно L и R, чтобы баланс тоже шёл рампой)
/// и рампа mute за 5 мс. Работает в аудиопотоке, без аллокаций.
/// </summary>
public struct StereoGain
{
    public const double GainRampSeconds = 0.010;
    public const double MuteRampSeconds = 0.005;

    private Ramp _l;
    private Ramp _r;
    private Ramp _mute;

    public void Init(int sampleRate, float gainL, float gainR, bool muted)
    {
        _l.Init((int)(sampleRate * GainRampSeconds), gainL);
        _r.Init((int)(sampleRate * GainRampSeconds), gainR);
        _mute.Init((int)(sampleRate * MuteRampSeconds), muted ? 0f : 1f);
    }

    public void SetTargets(float gainL, float gainR, bool muted)
    {
        _l.SetTarget(gainL);
        _r.SetTarget(gainR);
        _mute.SetTarget(muted ? 0f : 1f);
    }

    /// <summary>Итоговое усиление после рамп равно нулю: сигнал можно не считать.</summary>
    public readonly bool IsSilent => _l.IsSettled && _r.IsSettled && _mute.IsSettled
                                     && (_mute.Current == 0f || (_l.Current == 0f && _r.Current == 0f));

    /// <summary>Применяет усиление на месте к interleaved-стерео.</summary>
    public void Apply(Span<float> stereo, int frames)
    {
        if (_l.IsSettled && _r.IsSettled && _mute.IsSettled)
        {
            float gl = _l.Current * _mute.Current, gr = _r.Current * _mute.Current;
            if (gl == 1f && gr == 1f) return;
            for (int i = 0, j = 0; i < frames; i++, j += 2)
            {
                stereo[j] *= gl;
                stereo[j + 1] *= gr;
            }
            return;
        }
        for (int i = 0, j = 0; i < frames; i++, j += 2)
        {
            float m = _mute.Next();
            stereo[j] *= _l.Next() * m;
            stereo[j + 1] *= _r.Next() * m;
        }
    }

    /// <summary>Добавляет src × усиление в dst (для сведения проводов в выход).</summary>
    public void MixInto(ReadOnlySpan<float> src, Span<float> dst, int frames)
    {
        if (_l.IsSettled && _r.IsSettled && _mute.IsSettled)
        {
            float gl = _l.Current * _mute.Current, gr = _r.Current * _mute.Current;
            if (gl == 0f && gr == 0f) return;
            for (int i = 0, j = 0; i < frames; i++, j += 2)
            {
                dst[j] += src[j] * gl;
                dst[j + 1] += src[j + 1] * gr;
            }
            return;
        }
        for (int i = 0, j = 0; i < frames; i++, j += 2)
        {
            float m = _mute.Next();
            dst[j] += src[j] * _l.Next() * m;
            dst[j + 1] += src[j + 1] * _r.Next() * m;
        }
    }

    /// <summary>Линейные коэффициенты L/R для усиления в дБ и баланса −1…+1.</summary>
    public static (float L, float R) Targets(float gainDb, float balance)
    {
        float g = Db.ToGain(gainDb);
        balance = Math.Clamp(balance, -1f, 1f);
        float l = balance > 0 ? 1f - balance : 1f;
        float r = balance < 0 ? 1f + balance : 1f;
        return (g * l, g * r);
    }
}
