namespace Mixer.Audio.Dsp;

public enum BiquadKind { Peak, LowShelf, HighShelf, HighPass }

/// <summary>Коэффициенты биквада, нормированные на a0 (формулы RBJ Audio EQ Cookbook).</summary>
public readonly record struct BiquadCoeffs(double B0, double B1, double B2, double A1, double A2)
{
    public static readonly BiquadCoeffs Unity = new(1, 0, 0, 0, 0);

    /// <summary>
    /// RBJ Cookbook. У пика и полок Q — добротность (у полок она задаёт крутизну перехода так же, как в Cookbook,
    /// через α = sin w0 / 2Q); у ФВЧ Q = 1/√2 даёт Баттерворта 12 дБ/окт.
    /// </summary>
    public static BiquadCoeffs Design(BiquadKind kind, double freq, double gainDb, double q, double sampleRate)
    {
        // частота у самого Найквиста делает фильтр вырожденным
        freq = Math.Clamp(freq, 1, sampleRate * 0.49);
        q = Math.Max(q, 0.05);
        double w0 = 2 * Math.PI * freq / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * q);
        double a = Math.Pow(10, gainDb / 40);
        double b0, b1, b2, a0, a1, a2;
        switch (kind)
        {
            case BiquadKind.Peak:
                b0 = 1 + alpha * a;
                b1 = -2 * cos;
                b2 = 1 - alpha * a;
                a0 = 1 + alpha / a;
                a1 = -2 * cos;
                a2 = 1 - alpha / a;
                break;
            case BiquadKind.LowShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + s);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - s);
                a0 = (a + 1) + (a - 1) * cos + s;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - s;
                break;
            }
            case BiquadKind.HighShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cos + s);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - s);
                a0 = (a + 1) - (a - 1) * cos + s;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - s;
                break;
            }
            default: // HighPass
                b0 = (1 + cos) / 2;
                b1 = -(1 + cos);
                b2 = (1 + cos) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
        }
        return new BiquadCoeffs(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>|H(e^jw)| в дБ на частоте freq.</summary>
    public double MagnitudeDb(double freq, double sampleRate)
    {
        double w = 2 * Math.PI * freq / sampleRate;
        double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
        double nr = B0 + B1 * c1 + B2 * c2, ni = -(B1 * s1 + B2 * s2);
        double dr = 1 + A1 * c1 + A2 * c2, di = -(A1 * s1 + A2 * s2);
        double num = nr * nr + ni * ni, den = dr * dr + di * di;
        return 10 * Math.Log10(Math.Max(num, 1e-300) / Math.Max(den, 1e-300));
    }
}

/// <summary>
/// Стерео-биквад в прямой форме I, состояние в double (на низких полосах float шумит). Прямая форма I выбрана
/// ради плавной смены коэффициентов на ходу: её состояние — прошлые входы и выходы, они не зависят от коэффициентов,
/// поэтому вращение ручки не даёт переходного выброса (у транспонированной формы II на полке он был −37 дБ).
/// </summary>
public struct StereoBiquad
{
    public BiquadCoeffs C;
    private double _lx1, _lx2, _ly1, _ly2, _rx1, _rx2, _ry1, _ry2;

    public void Reset() => _lx1 = _lx2 = _ly1 = _ly2 = _rx1 = _rx2 = _ry1 = _ry2 = 0;

    public void Process(Span<float> stereo, int frames)
    {
        double b0 = C.B0, b1 = C.B1, b2 = C.B2, a1 = C.A1, a2 = C.A2;
        double lx1 = _lx1, lx2 = _lx2, ly1 = _ly1, ly2 = _ly2, rx1 = _rx1, rx2 = _rx2, ry1 = _ry1, ry2 = _ry2;
        for (int i = 0, j = 0; i < frames; i++, j += 2)
        {
            double x = stereo[j];
            double y = b0 * x + b1 * lx1 + b2 * lx2 - a1 * ly1 - a2 * ly2;
            lx2 = lx1;
            lx1 = x;
            ly2 = ly1;
            ly1 = y;
            stereo[j] = (float)y;

            x = stereo[j + 1];
            y = b0 * x + b1 * rx1 + b2 * rx2 - a1 * ry1 - a2 * ry2;
            rx2 = rx1;
            rx1 = x;
            ry2 = ry1;
            ry1 = y;
            stereo[j + 1] = (float)y;
        }
        // денормалы на длинной тишине
        _lx1 = lx1;
        _lx2 = lx2;
        _ly1 = Math.Abs(ly1) < 1e-25 ? 0 : ly1;
        _ly2 = Math.Abs(ly2) < 1e-25 ? 0 : ly2;
        _rx1 = rx1;
        _rx2 = rx2;
        _ry1 = Math.Abs(ry1) < 1e-25 ? 0 : ry1;
        _ry2 = Math.Abs(ry2) < 1e-25 ? 0 : ry2;
    }
}
