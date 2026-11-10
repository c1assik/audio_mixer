namespace Mixer.Tests;

/// <summary>Генерация и анализ тестовых сигналов.</summary>
internal static class Signal
{
    /// <summary>Interleaved-стерео синус: L = R.</summary>
    public static float[] Sine(double freq, int rate, int frames, double amplitude = 0.5, double phase = 0)
    {
        var buf = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(2 * Math.PI * freq * i / rate + phase));
            buf[2 * i] = v;
            buf[2 * i + 1] = v;
        }
        return buf;
    }

    /// <summary>
    /// THD+N в дБ относительно основного тона: МНК-подгонка a·sin + b·cos + c на известной частоте,
    /// остаток — всё, что не основной тон. Берётся канал L.
    /// </summary>
    public static double ThdN(ReadOnlySpan<float> stereo, double freq, int rate)
    {
        int n = stereo.Length / 2;
        double s11 = 0, s12 = 0, s13 = 0, s22 = 0, s23 = 0, s33 = n, y1 = 0, y2 = 0, y3 = 0;
        for (int i = 0; i < n; i++)
        {
            double w = 2 * Math.PI * freq * i / rate;
            double s = Math.Sin(w), c = Math.Cos(w), y = stereo[2 * i];
            s11 += s * s; s12 += s * c; s13 += s; s22 += c * c; s23 += c;
            y1 += s * y; y2 += c * y; y3 += y;
        }
        // 3×3 по Крамеру
        double det = s11 * (s22 * s33 - s23 * s23) - s12 * (s12 * s33 - s23 * s13) + s13 * (s12 * s23 - s22 * s13);
        double a = (y1 * (s22 * s33 - s23 * s23) - s12 * (y2 * s33 - s23 * y3) + s13 * (y2 * s23 - s22 * y3)) / det;
        double b = (s11 * (y2 * s33 - y3 * s23) - y1 * (s12 * s33 - s23 * s13) + s13 * (s12 * y3 - y2 * s13)) / det;
        double dc = (s11 * (s22 * y3 - s23 * y2) - s12 * (s12 * y3 - y2 * s13) + y1 * (s12 * s23 - s22 * s13)) / det;

        double res = 0;
        for (int i = 0; i < n; i++)
        {
            double w = 2 * Math.PI * freq * i / rate;
            double e = stereo[2 * i] - (a * Math.Sin(w) + b * Math.Cos(w) + dc);
            res += e * e;
        }
        double signalPower = (a * a + b * b) / 2;
        double noisePower = res / n;
        return 10 * Math.Log10(noisePower / signalPower);
    }

    /// <summary>
    /// THD+N с подгонкой частоты (4 параметра, как в IEEE 1057): несколько итераций Гаусса — Ньютона
    /// по поправке частоты вокруг freq0.
    /// </summary>
    public static double ThdN4(ReadOnlySpan<float> stereo, double freq0, int rate)
    {
        // время отсчитывается от середины окна: иначе поправка частоты смешивается с фазой и сходится медленно
        int n = stereo.Length / 2;
        double mid = (n - 1) / 2.0;
        double w = 2 * Math.PI * freq0 / rate;
        double a = 0, b = 0, c = 0;
        for (int iter = 0; iter < 10; iter++)
        {
            (a, b, c) = Fit3(stereo, w, mid);
            double num = 0, den = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i - mid;
                double s = Math.Sin(w * t), co = Math.Cos(w * t);
                double g = t * (a * co - b * s);
                num += g * (stereo[2 * i] - (a * s + b * co + c));
                den += g * g;
            }
            w += num / den;
        }
        (a, b, c) = Fit3(stereo, w, mid);
        double res = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i - mid;
            double e = stereo[2 * i] - (a * Math.Sin(w * t) + b * Math.Cos(w * t) + c);
            res += e * e;
        }
        return 10 * Math.Log10(res / n / ((a * a + b * b) / 2));
    }

    private static (double A, double B, double C) Fit3(ReadOnlySpan<float> stereo, double w, double mid)
    {
        int n = stereo.Length / 2;
        double s11 = 0, s12 = 0, s13 = 0, s22 = 0, s23 = 0, s33 = n, y1 = 0, y2 = 0, y3 = 0;
        for (int i = 0; i < n; i++)
        {
            double s = Math.Sin(w * (i - mid)), c = Math.Cos(w * (i - mid)), y = stereo[2 * i];
            s11 += s * s; s12 += s * c; s13 += s; s22 += c * c; s23 += c;
            y1 += s * y; y2 += c * y; y3 += y;
        }
        double det = s11 * (s22 * s33 - s23 * s23) - s12 * (s12 * s33 - s23 * s13) + s13 * (s12 * s23 - s22 * s13);
        double a = (y1 * (s22 * s33 - s23 * s23) - s12 * (y2 * s33 - s23 * y3) + s13 * (y2 * s23 - s22 * y3)) / det;
        double b = (s11 * (y2 * s33 - y3 * s23) - y1 * (s12 * s33 - s23 * s13) + s13 * (s12 * y3 - y2 * s13)) / det;
        double dc = (s11 * (s22 * y3 - s23 * y2) - s12 * (s12 * y3 - y2 * s13) + y1 * (s12 * s23 - s22 * s13)) / det;
        return (a, b, dc);
    }

    public static double Rms(ReadOnlySpan<float> stereo, int channel = 0)
    {
        double sum = 0;
        int n = stereo.Length / 2;
        for (int i = 0; i < n; i++) sum += stereo[2 * i + channel] * (double)stereo[2 * i + channel];
        return Math.Sqrt(sum / Math.Max(1, n));
    }

    public static float PeakAbs(ReadOnlySpan<float> x)
    {
        float p = 0;
        foreach (float v in x) p = MathF.Max(p, MathF.Abs(v));
        return p;
    }
}
