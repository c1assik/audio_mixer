using Mixer.Audio.Dsp;
using Mixer.Audio.Engine;
using Xunit.Abstractions;

namespace Mixer.Tests;

public class ResamplerTests(ITestOutputHelper output)
{
    /// <summary>Прогнать сигнал через ресемплер с постоянным коэффициентом блоками по block кадров.</summary>
    private static float[] Run(ResamplerKind kind, float[] input, int fin, int fout, int block = 480)
    {
        int inFrames = input.Length / 2;
        var ring = new SpscRing(inFrames + 16);
        Assert.Equal(inFrames, ring.Write(input, inFrames));
        double r = (double)fin / fout;
        var rs = Resampler.Create(kind, r, 4096);
        var result = new List<float>();
        var buf = new float[block * 2];
        while (rs.InputNeeded(block, r) <= ring.Available)
        {
            rs.Process(ring, buf, block, r);
            result.AddRange(buf);
        }
        return result.ToArray();
    }

    private double Measure(ResamplerKind kind, double freq, int fin, int fout)
    {
        var input = Signal.Sine(freq, fin, fin * 2);
        var outp = Run(kind, input, fin, fout);
        // начало (история из нулей) и конец отбрасываем
        int skip = fout / 10;
        var steady = outp.AsSpan(skip * 2, outp.Length - 2 * skip * 2);
        double thd = Signal.ThdN(steady, freq, fout);
        output.WriteLine($"{kind,-5} {fin / 1000.0:0.#}→{fout / 1000.0:0.#} кГц, {freq / 1000:0.#} кГц: THD+N {thd:F1} дБ");
        return thd;
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    public void Sinc_ThdN_below_minus80dB_at_1kHz(int fin, int fout)
    {
        Assert.True(Measure(ResamplerKind.Sinc, 1000, fin, fout) < -80);
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    public void Sinc_stays_clean_across_the_band(int fin, int fout)
    {
        // не требование спецификации, а защита от регрессий: высокие частоты тоже без грязи
        foreach (double f in new[] { 100.0, 5000, 10000, 15000 })
            Assert.True(Measure(ResamplerKind.Sinc, f, fin, fout) < -80, $"{f} Гц");
    }

    /// <summary>
    /// Catmull-Rom из спецификации: замер для сравнения. На 1 кГц он проходит −80 дБ (≈ −90), но на 10 кГц
    /// даёт около −27 дБ из-за зеркальных компонент — поэтому в движке по умолчанию sinc.
    /// </summary>
    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    public void Cubic_reference_measurement(int fin, int fout)
    {
        double at1k = Measure(ResamplerKind.Cubic, 1000, fin, fout);
        Measure(ResamplerKind.Cubic, 10000, fin, fout);
        Assert.True(at1k < -40);
    }

    [Fact]
    public void Equal_rates_pass_signal_unchanged_in_level()
    {
        var input = Signal.Sine(1000, 48000, 48000);
        var outp = Run(ResamplerKind.Sinc, input, 48000, 48000);
        var steady = outp.AsSpan(4800 * 2, 38000 * 2);
        Assert.Equal(Signal.Rms(input), Signal.Rms(steady), 3);
    }

    [Fact]
    public void Downsampling_suppresses_content_above_new_nyquist()
    {
        // 23 кГц при 48 кГц выше Найквиста 44,1 кГц (22,05 кГц): должен быть подавлен, а не отражён
        var input = Signal.Sine(23000, 48000, 48000);
        var outp = Run(ResamplerKind.Sinc, input, 48000, 44100);
        var steady = outp.AsSpan(4410 * 2, 30000 * 2);
        double db = 20 * Math.Log10(Signal.Rms(steady) / Signal.Rms(input));
        output.WriteLine($"23 кГц после 48→44,1: {db:F1} дБ");
        Assert.True(db < -60);
    }

    [Fact]
    public void Large_ratio_with_skip_works()
    {
        // 192 → 44,1 кГц: r > H у кубического, проверка ветки пропуска входа
        foreach (var kind in new[] { ResamplerKind.Cubic, ResamplerKind.Sinc })
        {
            var input = Signal.Sine(1000, 192000, 192000);
            var outp = Run(kind, input, 192000, 44100);
            int expected = 44100;
            Assert.InRange(outp.Length / 2, expected - 2000, expected);
            Assert.All(outp, v => Assert.True(float.IsFinite(v)));
            var steady = outp.AsSpan(4410 * 2, 30000 * 2);
            Assert.True(Signal.ThdN(steady, 1000, 44100) < -40);
        }
    }

    [Fact]
    public void MaxOutput_matches_InputNeeded()
    {
        var rs = Resampler.Create(ResamplerKind.Sinc, 44100.0 / 48000, 4096);
        double r = 44100.0 / 48000 * (1 + 500e-6);
        for (int avail = 0; avail < 600; avail += 7)
        {
            int m = rs.MaxOutput(avail, r, 480);
            Assert.True(rs.InputNeeded(m, r) <= avail);
            if (m < 480) Assert.True(rs.InputNeeded(m + 1, r) > avail);
        }
    }
}
