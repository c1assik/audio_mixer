using System.Collections.Concurrent;
using System.Numerics;

namespace Mixer.Audio.Dsp;

/// <summary>
/// Таблица полифазного windowed-sinc фильтра (окно Кайзера) для заданного масштаба.
/// При понижении частоты (r &gt; 1) ядро растягивается в r раз, а срез опускается до новой частоты Найквиста.
/// Таблицы кэшируются по масштабу: строится одна на пару частот, а не на провод.
/// </summary>
public sealed class SincTable
{
    /// <summary>Нулей sinc в каждую сторону при масштабе 1.</summary>
    public const int ZeroCrossings = 32;
    /// <summary>Срез относительно частоты Найквиста меньшей из частот.</summary>
    public const double Rolloff = 0.92;
    public const double KaiserBeta = 8.6;
    /// <summary>Фаз в таблице; между соседними фазами коэффициенты интерполируются линейно.</summary>
    public const int Phases = 512;

    private static readonly ConcurrentDictionary<long, SincTable> Cache = new();

    public double Scale { get; }
    public int H { get; }
    public int Taps { get; }
    public int Stride { get; }
    /// <summary>(Phases + 1) строк по Stride коэффициентов; хвост строки после Taps — нули.</summary>
    public float[] Rows { get; }

    private SincTable(double scale)
    {
        Scale = scale;
        double width = ZeroCrossings / scale;        // полуширина окна во входных кадрах
        double c = scale * Rolloff;                  // 2·fc в циклах на входной кадр
        H = (int)Math.Ceiling(width);
        Taps = 2 * H;
        int vc = 16; // кратно Vector<float>.Count и для AVX2 (8), и для AVX-512 (16)
        Stride = (Taps + vc - 1) / vc * vc;
        Rows = new float[(Phases + 1) * Stride];
        double i0Beta = BesselI0(KaiserBeta);

        var row = new double[Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            for (int j = 0; j < Taps; j++)
            {
                double d = frac + H - 1 - j;
                double v = 0;
                if (Math.Abs(d) < width)
                {
                    double x = d / width;
                    v = c * Sinc(c * d) * BesselI0(KaiserBeta * Math.Sqrt(1 - x * x)) / i0Beta;
                }
                row[j] = v;
                sum += v;
            }
            // единичное усиление на постоянном токе для каждой фазы
            for (int j = 0; j < Taps; j++) Rows[p * Stride + j] = (float)(row[j] / sum);
        }
    }

    /// <summary>Таблица для коэффициента r = fвх / fвых (номинального, без поправки на дрейф).</summary>
    public static SincTable For(double nominalRatio)
    {
        double scale = nominalRatio > 1 ? 1 / nominalRatio : 1;
        long key = (long)Math.Round(scale * 1e6);
        return Cache.GetOrAdd(key, _ => new SincTable(scale));
    }

    /// <summary>Заранее построить таблицы для пар частот (вызывать вне аудиопотока).</summary>
    public static void Prewarm(IEnumerable<int> rates)
    {
        var list = rates.Distinct().ToList();
        foreach (int a in list)
        foreach (int b in list)
            For((double)a / b);
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-12) return 1;
        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 64; k++)
        {
            term *= q / (k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }
}

/// <summary>Полифазный windowed-sinc ресемплер; скалярные произведения на System.Numerics.Vector.</summary>
public sealed class SincResampler : Resampler
{
    private readonly SincTable _t;

    public SincTable Table => _t;

    public SincResampler(SincTable table, int maxInputFrames) : base(table.H, maxInputFrames + table.Stride)
    {
        _t = table;
    }

    protected override int ReadPadding => _t.Stride - _t.Taps;

    protected override void Render(float[] hl, float[] hr, double pos, double ratio, Span<float> dst, int outFrames)
    {
        float[] rows = _t.Rows;
        int stride = _t.Stride, h = _t.H;
        int vc = Vector<float>.Count;
        const int phases = SincTable.Phases;

        for (int i = 0, o = 0; i < outFrames; i++, o += 2)
        {
            double t = pos + i * ratio;
            int idx = (int)t;
            double pf = (t - idx) * phases;
            int p = (int)pf;
            if (p >= phases) p = phases - 1;
            var w = new Vector<float>((float)(pf - p));
            int ra = p * stride, rb = ra + stride;
            int b = idx - h + 1;

            var accL = Vector<float>.Zero;
            var accR = Vector<float>.Zero;
            for (int k = 0; k < stride; k += vc)
            {
                var ca = new Vector<float>(rows, ra + k);
                var cb = new Vector<float>(rows, rb + k);
                var coef = ca + (cb - ca) * w;
                accL += coef * new Vector<float>(hl, b + k);
                accR += coef * new Vector<float>(hr, b + k);
            }
            dst[o] = Vector.Sum(accL);
            dst[o + 1] = Vector.Sum(accR);
        }
    }
}
