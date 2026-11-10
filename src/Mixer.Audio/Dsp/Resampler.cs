using Mixer.Audio.Engine;

namespace Mixer.Audio.Dsp;

public enum ResamplerKind { Sinc, Cubic }

/// <summary>
/// Ресемплер с переменным коэффициентом r = входных кадров на выходной кадр.
/// Держит раздельную (planar) историю входа; вход берёт прямо из кольца провода.
/// Выход в момент t (в координатах истории) использует входы floor(t)−H+1 … floor(t)+H.
/// Без аллокаций в Process, пока хватает заранее выделенной истории.
/// </summary>
public abstract class Resampler
{
    private float[] _hl;
    private float[] _hr;
    private int _len;    // кадров в истории
    private double _pos; // позиция следующего выхода в координатах истории
    private int _skip;   // столько кадров кольца выбросить перед следующим чтением (при r > H)

    /// <summary>Полуширина ядра в входных кадрах.</summary>
    public int H { get; }

    /// <summary>Задержка ресемплера во входных кадрах (групповая, для бюджета задержки).</summary>
    public double DelayFrames => H - 1;

    /// <summary>Кадров сверх ядра, которые Render может читать (выравнивание под SIMD).</summary>
    protected virtual int ReadPadding => 0;

    protected Resampler(int halfTaps, int maxInputFrames)
    {
        H = halfTaps;
        _hl = new float[maxInputFrames + 2 * halfTaps + 64];
        _hr = new float[_hl.Length];
        Reset();
    }

    public static Resampler Create(ResamplerKind kind, double nominalRatio, int maxOutputFrames)
    {
        int maxIn = (int)Math.Ceiling(maxOutputFrames * nominalRatio * 1.01) + 8;
        return kind == ResamplerKind.Cubic
            ? new CubicResampler(maxIn)
            : new SincResampler(SincTable.For(nominalRatio), maxIn);
    }

    /// <summary>История заполняется нулями: первый выход соответствует первому кадру кольца.</summary>
    public void Reset()
    {
        _len = H - 1;
        Array.Clear(_hl, 0, _hl.Length);
        Array.Clear(_hr, 0, _hr.Length);
        _pos = H - 1;
        _skip = 0;
    }

    /// <summary>Сколько кадров кольца нужно для outFrames выходов.</summary>
    public int InputNeeded(int outFrames, double ratio)
    {
        if (outFrames <= 0) return 0;
        double last = _pos + (outFrames - 1) * ratio;
        int need = (int)Math.Floor(last) + H + 1 - _len;
        return _skip + Math.Max(0, need);
    }

    /// <summary>Наибольшее m ≤ outFrames, для которого хватает available кадров кольца.</summary>
    public int MaxOutput(int available, double ratio, int outFrames)
    {
        int k = available - _skip + _len - H - 1; // floor(pos + (m−1)·r) ≤ k
        if (k < Math.Floor(_pos)) return 0;
        int m = (int)Math.Ceiling((k + 1 - _pos) / ratio);
        m = Math.Clamp(m, 0, outFrames);
        while (m > 0 && InputNeeded(m, ratio) > available) m--;
        while (m < outFrames && InputNeeded(m + 1, ratio) <= available) m++;
        return m;
    }

    /// <summary>Выдаёт outFrames кадров interleaved-стерео в dst, забирая нужный вход из кольца.</summary>
    public void Process(SpscRing ring, Span<float> dst, int outFrames, double ratio)
    {
        if (outFrames <= 0) return;
        int fetch = InputNeeded(outFrames, ratio) - _skip;
        if (_skip > 0)
        {
            ring.Skip(_skip);
            _skip = 0;
        }
        EnsureCapacity(_len + fetch + ReadPadding + 1);
        if (fetch > 0)
        {
            ring.ReadPlanar(_hl.AsSpan(_len, fetch), _hr.AsSpan(_len, fetch), fetch);
            _len += fetch;
        }
        if (ReadPadding > 0)
        {
            // за ядром коэффициенты нулевые, но 0·NaN = NaN: хвост истории держим чистым
            _hl.AsSpan(_len, ReadPadding).Clear();
            _hr.AsSpan(_len, ReadPadding).Clear();
        }

        Render(_hl, _hr, _pos, ratio, dst, outFrames);

        _pos += outFrames * ratio;
        int drop = (int)Math.Floor(_pos) - H + 1;
        if (drop > 0)
        {
            if (drop >= _len)
            {
                _skip = drop - _len;
                _len = 0;
            }
            else
            {
                int keep = _len - drop;
                Array.Copy(_hl, drop, _hl, 0, keep);
                Array.Copy(_hr, drop, _hr, 0, keep);
                _len = keep;
            }
            _pos -= drop;
        }
    }

    private void EnsureCapacity(int frames)
    {
        if (frames <= _hl.Length) return;
        // Только при перенастройке (неожиданно большой блок); в установившемся режиме не срабатывает.
        int size = frames * 2;
        Array.Resize(ref _hl, size);
        Array.Resize(ref _hr, size);
    }

    /// <summary>
    /// Выход i берётся в точке t = pos + i·ratio. Входы с индексом floor(t)−H+1 … floor(t)+H гарантированно в истории.
    /// </summary>
    protected abstract void Render(float[] hl, float[] hr, double pos, double ratio, Span<float> dst, int outFrames);
}

/// <summary>Кубическая интерполяция Catmull-Rom (4 точки). Без фильтра против алиасинга.</summary>
public sealed class CubicResampler : Resampler
{
    public CubicResampler(int maxInputFrames) : base(2, maxInputFrames)
    {
    }

    protected override void Render(float[] hl, float[] hr, double pos, double ratio, Span<float> dst, int outFrames)
    {
        for (int i = 0, o = 0; i < outFrames; i++, o += 2)
        {
            double t = pos + i * ratio;
            int idx = (int)t;
            float f = (float)(t - idx);
            dst[o] = Cr(hl[idx - 1], hl[idx], hl[idx + 1], hl[idx + 2], f);
            dst[o + 1] = Cr(hr[idx - 1], hr[idx], hr[idx + 1], hr[idx + 2], f);
        }
    }

    private static float Cr(float y0, float y1, float y2, float y3, float f)
    {
        float a = -y0 + 3f * y1 - 3f * y2 + y3;
        float b = 2f * y0 - 5f * y1 + 4f * y2 - y3;
        float c = -y0 + y2;
        return 0.5f * (((a * f + b) * f + c) * f + 2f * y1);
    }
}
