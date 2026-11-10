using System.Diagnostics;
using Mixer.Audio.Dsp;
using Mixer.Core.Editing;
using Xunit.Abstractions;

namespace Mixer.Tests;

/// <summary>v2, этап 2: обработка — АЧХ EQ, плавная смена параметров без щелчков, гейт, компрессор, CPU и аллокации.</summary>
public class ProcessingTests(ITestOutputHelper output)
{
    private const int Rate = 48000;
    private const int Block = 480;

    /// <summary>Прогнать стерео-сигнал через цепочку блоками по 10 мс; perBlock — что поменять перед блоком.</summary>
    private static float[] Process(ProcessingChain chain, float[] input, Action<int>? perBlock = null)
    {
        var buf = (float[])input.Clone();
        int frames = buf.Length / 2;
        for (int off = 0, b = 0; off < frames; off += Block, b++)
        {
            perBlock?.Invoke(b);
            int n = Math.Min(Block, frames - off);
            chain.Process(buf.AsSpan(off * 2, n * 2), n, Rate);
        }
        return buf;
    }

    /// <summary>Амплитуда синуса частоты f в отрезке (МНК по sin и cos), канал 0.</summary>
    private static double Amplitude(float[] stereo, double f, int from, int to)
    {
        double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0;
        double w = 2 * Math.PI * f / Rate;
        for (int i = from; i < to; i++)
        {
            double s = Math.Sin(w * i), c = Math.Cos(w * i), y = stereo[2 * i];
            ss += s * s;
            sc += s * c;
            cc += c * c;
            ys += y * s;
            yc += y * c;
        }
        double det = ss * cc - sc * sc;
        double a = (ys * cc - yc * sc) / det, b = (yc * ss - ys * sc) / det;
        return Math.Sqrt(a * a + b * b);
    }

    private static ChainSettings EqOnly(EqSettings eq) => new ChainSettings { Eq = eq with { Enabled = true } }.Normalized();

    internal static ChainSettings FullEq => EqOnly(new EqSettings
    {
        HighPass = true,
        HighPassHz = 80,
        Bands =
        [
            new EqBand { Type = EqBandType.LowShelf, FreqHz = 150, GainDb = 4, Q = 0.7f },
            new EqBand { Type = EqBandType.Peak, FreqHz = 400, GainDb = -6, Q = 2 },
            new EqBand { Type = EqBandType.Peak, FreqHz = 3000, GainDb = 9, Q = 0.8f },
            new EqBand { Type = EqBandType.HighShelf, FreqHz = 10000, GainDb = -5, Q = 0.7f },
        ],
    });

    /// <summary>Расчётная АЧХ цепочки EQ: сумма |H| биквадов из настроек, дБ.</summary>
    private static double Expected(ChainSettings c, double f)
    {
        double db = 0;
        if (c.Eq.HighPass) db += BiquadCoeffs.Design(BiquadKind.HighPass, c.Eq.HighPassHz, 0, 0.70710678, Rate).MagnitudeDb(f, Rate);
        foreach (var b in c.Eq.Bands)
        {
            if (b.Type == EqBandType.Off) continue;
            var kind = b.Type switch { EqBandType.LowShelf => BiquadKind.LowShelf, EqBandType.HighShelf => BiquadKind.HighShelf, _ => BiquadKind.Peak };
            db += BiquadCoeffs.Design(kind, b.FreqHz, b.GainDb, b.Q, Rate).MagnitudeDb(f, Rate);
        }
        return db;
    }

    // ── EQ ──

    [Fact]
    public void Rbj_design_points_hold()
    {
        // пик: ровно своё усиление на центральной частоте; ФВЧ Баттерворта: −3,01 дБ на срезе; полки: усиление вдали
        Assert.Equal(9, BiquadCoeffs.Design(BiquadKind.Peak, 3000, 9, 0.8, Rate).MagnitudeDb(3000, Rate), 3);
        Assert.Equal(-3.01, BiquadCoeffs.Design(BiquadKind.HighPass, 80, 0, 0.70710678, Rate).MagnitudeDb(80, Rate), 2);
        Assert.Equal(-12.30, BiquadCoeffs.Design(BiquadKind.HighPass, 80, 0, 0.70710678, Rate).MagnitudeDb(40, Rate), 1); // 1/√(1 + 2⁴)
        Assert.Equal(-36.1, BiquadCoeffs.Design(BiquadKind.HighPass, 80, 0, 0.70710678, Rate).MagnitudeDb(10, Rate), 1);  // 12 дБ/окт вдали
        Assert.Equal(4, BiquadCoeffs.Design(BiquadKind.LowShelf, 150, 4, 0.7, Rate).MagnitudeDb(20, Rate), 1);
        Assert.Equal(-5, BiquadCoeffs.Design(BiquadKind.HighShelf, 10000, -5, 0.7, Rate).MagnitudeDb(22000, Rate), 1);
        Assert.Equal(2, BiquadCoeffs.Design(BiquadKind.HighShelf, 10000, 4, 0.7, Rate).MagnitudeDb(10000, Rate), 3); // полка: половина на своей частоте
        Assert.Equal(0, BiquadCoeffs.Design(BiquadKind.Peak, 1000, 0, 1, Rate).MagnitudeDb(250, Rate), 9); // 0 дБ — точная единица
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(1000)]
    [InlineData(3000)]
    [InlineData(6000)]
    [InlineData(10000)]
    [InlineData(15000)]
    [InlineData(19000)]
    public void Eq_response_matches_the_design_within_a_tenth_of_a_db(double f)
    {
        var chain = new ProcessingChain();
        var s = FullEq;
        chain.Set(s);
        var input = Signal.Sine(f, Rate, Rate, amplitude: 0.1);
        var y = Process(chain, input);
        double measured = 20 * Math.Log10(Amplitude(y, f, Rate / 2, Rate) / 0.1);
        double expected = Expected(s, f);
        output.WriteLine($"{f} Гц: измерено {measured:F3} дБ, расчёт {expected:F3} дБ");
        Assert.InRange(measured - expected, -0.1, 0.1);
    }

    [Fact]
    public void Disabled_chain_and_disabled_blocks_pass_audio_untouched()
    {
        var input = Signal.Sine(997, Rate, Rate / 2, amplitude: 0.5);
        var chain = new ProcessingChain();
        Assert.Equal(input, Process(chain, input));
        chain.Set(new ChainSettings().Normalized()); // все блоки выключены
        Assert.Equal(input, Process(chain, input));

        // включили и выключили: после кроссфейда — снова точная копия
        var on = new ProcessingChain();
        on.Set(FullEq);
        Process(on, input);
        on.Set(FullEq with { Eq = FullEq.Eq with { Enabled = false } });
        Process(on, input); // переход
        Assert.Equal(input, Process(on, input));
    }

    // ── щелчки ──

    /// <summary>
    /// Детектор щелчков: остаток выше 6 кГц (четыре ФВЧ Баттерворта подряд, синус 1 кГц гасится на ~120 дБ).
    /// Скачок сигнала даёт широкий спектр и виден здесь сразу; медленная модуляция уровня или частоты — нет.
    /// </summary>
    private static double ClickDb(float[] stereo, int from)
    {
        var hp = new StereoBiquad[4];
        for (int i = 0; i < hp.Length; i++) hp[i].C = BiquadCoeffs.Design(BiquadKind.HighPass, 6000, 0, 0.70710678, Rate);
        var buf = (float[])stereo.Clone();
        for (int i = 0; i < hp.Length; i++) hp[i].Process(buf, buf.Length / 2);
        float peak = 0;
        for (int i = from * 2; i < buf.Length; i++) peak = MathF.Max(peak, MathF.Abs(buf[i]));
        return 20 * Math.Log10(Math.Max(peak, 1e-12));
    }

    /// <summary>Синус 1 кГц −6 dBFS, 3 с; параметр меняется каждые 10 мс (как правки от ручки), от a до b за 2 с и обратно.</summary>
    private double Rotate(string what, ChainSettings start, Func<ChainSettings, double, ChainSettings> set, double a, double b)
    {
        var chain = new ProcessingChain();
        chain.Set(start);
        var input = Signal.Sine(1000, Rate, Rate * 3, amplitude: 0.5);
        var y = Process(chain, input, block =>
        {
            if (block < 50) return; // 0,5 с на установление
            double t = (block - 50) / 100.0; // 0…2,5 с
            double k = t < 1 ? t : t < 2 ? 2 - t : 0;
            chain.Set(set(start, a + (b - a) * k));
        });
        // относительно самого сигнала: при +18 дБ на пике выход сам +12 dBFS
        float peak = 0;
        for (int i = Rate; i < y.Length; i++) peak = MathF.Max(peak, MathF.Abs(y[i]));
        double db = ClickDb(y, Rate / 2) - 20 * Math.Log10(peak);
        output.WriteLine($"{what}: остаток выше 6 кГц {db:F1} дБ от сигнала");
        return db;
    }

    private const double ClickLimitDb = -60;

    internal static ChainSettings Comp(float thr = -20, float ratio = 4, float makeup = 0) =>
        new ChainSettings { Comp = new CompSettings { Enabled = true, ThresholdDb = thr, Ratio = ratio, AttackMs = 10, ReleaseMs = 150, KneeDb = 6, MakeupDb = makeup } }.Normalized();

    private static ChainSettings Gate(float thr = -60) =>
        new ChainSettings { Gate = new GateSettings { Enabled = true, ThresholdDb = thr, AttackMs = 1, HoldMs = 50, ReleaseMs = 150, DepthDb = -40 } }.Normalized();

    private static ChainSettings Peak(float f = 1000, float g = 6, float q = 1) =>
        EqOnly(new EqSettings { Bands = [new EqBand { Type = EqBandType.Peak, FreqHz = f, GainDb = g, Q = q }] });

    [Fact]
    public void Turning_eq_knobs_on_a_1khz_sine_does_not_click()
    {
        Assert.InRange(Rotate("частота пика", Peak(), (s, v) => s with { Eq = s.Eq.WithBand(0, b => b with { FreqHz = (float)Math.Exp(v) }) }, Math.Log(200), Math.Log(5000)), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("усиление пика", Peak(), (s, v) => s with { Eq = s.Eq.WithBand(0, b => b with { GainDb = (float)v }) }, -18, 18), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("Q пика", Peak(g: 12), (s, v) => s with { Eq = s.Eq.WithBand(0, b => b with { Q = (float)Math.Exp(v) }) }, Math.Log(0.3), Math.Log(10)), double.MinValue, ClickLimitDb);
        var shelf = EqOnly(new EqSettings { Bands = [new EqBand { Type = EqBandType.HighShelf, FreqHz = 2000, GainDb = 0, Q = 0.7f }] });
        Assert.InRange(Rotate("высокая полка", shelf, (s, v) => s with { Eq = s.Eq.WithBand(0, b => b with { GainDb = (float)v }) }, -18, 18), double.MinValue, ClickLimitDb);
        var hp = EqOnly(new EqSettings { HighPass = true, HighPassHz = 40 });
        Assert.InRange(Rotate("срез ФВЧ", hp, (s, v) => s with { Eq = s.Eq with { HighPassHz = (float)v } }, 40, 300), double.MinValue, ClickLimitDb);
    }

    [Fact]
    public void Turning_compressor_and_gate_knobs_does_not_click()
    {
        Assert.InRange(Rotate("порог компрессора", Comp(), (s, v) => s with { Comp = s.Comp with { ThresholdDb = (float)v } }, -40, 0), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("степень", Comp(), (s, v) => s with { Comp = s.Comp with { Ratio = (float)v } }, 1, 20), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("компенсация", Comp(), (s, v) => s with { Comp = s.Comp with { MakeupDb = (float)v } }, 0, 24), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("колено", Comp(thr: -8), (s, v) => s with { Comp = s.Comp with { KneeDb = (float)v } }, 0, 12), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("атака", Comp(), (s, v) => s with { Comp = s.Comp with { AttackMs = (float)v } }, 0.1, 100), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("восстановление", Comp(), (s, v) => s with { Comp = s.Comp with { ReleaseMs = (float)v } }, 10, 1000), double.MinValue, ClickLimitDb);
        // порог гейта ниже сигнала, глубина — по всему диапазону, пока гейт закрыт порогом выше сигнала
        Assert.InRange(Rotate("порог гейта", Gate(), (s, v) => s with { Gate = s.Gate with { ThresholdDb = (float)v } }, -80, -10), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("глубина гейта", Gate(thr: 0), (s, v) => s with { Gate = s.Gate with { DepthDb = (float)v } }, -80, 0), double.MinValue, ClickLimitDb);
    }

    [Fact]
    public void Switching_blocks_and_band_types_does_not_click()
    {
        var full = new ChainSettings
        {
            Gate = new GateSettings { Enabled = true, ThresholdDb = -60 },
            Eq = FullEq.Eq,
            Comp = Comp().Comp,
        }.Normalized();
        // каждые 10 мс: туда-сюда, пока v растёт до 1 и обратно
        ChainSettings Flip(ChainSettings s, double v, Func<ChainSettings, ChainSettings> change) => (int)(v * 20) % 2 == 1 ? change(s) : s;
        Assert.InRange(Rotate("EQ выкл/вкл", full, (s, v) => Flip(s, v, x => x with { Eq = x.Eq with { Enabled = false } }), 0, 1), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("компрессор выкл/вкл", full, (s, v) => Flip(s, v, x => x with { Comp = x.Comp with { Enabled = false } }), 0, 1), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("гейт выкл/вкл", full, (s, v) => Flip(s, v, x => x with { Gate = x.Gate with { Enabled = false } }), 0, 1), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("ФВЧ выкл/вкл", full, (s, v) => Flip(s, v, x => x with { Eq = x.Eq with { HighPass = false } }), 0, 1), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("тип полосы пик ↔ полка", full, (s, v) => Flip(s, v, x => x with { Eq = x.Eq.WithBand(2, b => b with { Type = EqBandType.HighShelf }) }), 0, 1), double.MinValue, ClickLimitDb);
        Assert.InRange(Rotate("полоса выкл/вкл", full, (s, v) => Flip(s, v, x => x with { Eq = x.Eq.WithBand(1, b => b with { Type = EqBandType.Off }) }), 0, 1), double.MinValue, ClickLimitDb);

    }

    [Fact]
    public void Click_detector_sees_an_abrupt_gain_step()
    {
        // проверка самого детектора: ступенька 1 дБ без рампы — это щелчок
        var x = Signal.Sine(1000, Rate, Rate, amplitude: 0.5);
        for (int i = Rate / 2 * 2 + 37; i < x.Length; i++) x[i] *= 1.122f;
        Assert.True(ClickDb(x, Rate / 4) > ClickLimitDb + 10);
        Assert.True(ClickDb(Signal.Sine(1000, Rate, Rate, amplitude: 0.5), Rate / 4) < -100);
    }

    // ── гейт и компрессор ──

    [Fact]
    public void Compressor_static_curve_and_reduction_meter()
    {
        var chain = new ProcessingChain();
        chain.Set(new ChainSettings { Comp = new CompSettings { Enabled = true, ThresholdDb = -20, Ratio = 4, KneeDb = 0, AttackMs = 5, ReleaseMs = 200, MakeupDb = 3 } }.Normalized());
        var y = Process(chain, Signal.Sine(1000, Rate, Rate * 2, amplitude: 0.5));
        double outDb = 20 * Math.Log10(Amplitude(y, 1000, Rate, Rate * 2));
        // пик −6,02 dBFS: подавление (20 − 6,02)·(1 − 1/4) = 10,49 дБ, плюс компенсация 3
        double expected = -6.02 - 10.49 + 3;
        output.WriteLine($"выход {outDb:F2} dBFS, ждали {expected:F2}; подавление по индикатору {chain.Meters.TakeReduction():F2} дБ");
        Assert.InRange(outDb, expected - 0.5, expected + 0.5);
        Process(chain, Signal.Sine(1000, Rate, Block, amplitude: 0.5));
        Assert.InRange(chain.Meters.TakeReduction(), 10, 11.2);
    }

    [Fact]
    public void Compressor_detector_is_shared_so_the_stereo_image_stays()
    {
        var chain = new ProcessingChain();
        chain.Set(Comp(thr: -30, ratio: 8));
        var x = Signal.Sine(1000, Rate, Rate, amplitude: 0.5);
        for (int i = 1; i < x.Length; i += 2) x[i] *= 0.1f; // правый канал на 20 дБ тише
        var y = Process(chain, x);
        double l = 0, r = 0;
        for (int i = Rate; i < y.Length; i += 2)
        {
            l = Math.Max(l, Math.Abs(y[i]));
            r = Math.Max(r, Math.Abs(y[i + 1]));
        }
        Assert.Equal(0.1, r / l, 3);
    }

    [Fact]
    public void Gate_mutes_below_threshold_holds_and_opens_above()
    {
        var chain = new ProcessingChain();
        chain.Set(new ChainSettings { Gate = new GateSettings { Enabled = true, ThresholdDb = -40, AttackMs = 1, HoldMs = 100, ReleaseMs = 20, DepthDb = -60 } }.Normalized());
        // 0,5 с тона −20 dBFS, затем 1 с фона −50 dBFS
        var loud = Signal.Sine(1000, Rate, Rate / 2, amplitude: 0.1);
        var quiet = Signal.Sine(1000, Rate, Rate, amplitude: 0.00316);
        var y = Process(chain, [.. loud, .. quiet]);
        Assert.Equal(0.1, Amplitude(y, 1000, Rate / 4, Rate / 2), 3);                // открыт: без изменений
        double held = Amplitude(y, 1000, Rate / 2 + 480, Rate / 2 + 2400);            // 10…50 мс после: удержание
        Assert.InRange(held, 0.0030, 0.0033);
        double closed = Amplitude(y, 1000, Rate / 2 + Rate / 2, Rate / 2 + Rate);      // через 0,5 с: −60 дБ
        output.WriteLine($"удержание {20 * Math.Log10(held / 0.00316):F1} дБ, закрыт {20 * Math.Log10(closed / 0.00316):F1} дБ");
        Assert.InRange(20 * Math.Log10(closed / 0.00316), -61, -58);
        Assert.False(chain.Meters.GateOpen);
    }

    // ── бюджет ──

    internal static ChainSettings Everything => new ChainSettings
    {
        Gate = new GateSettings { Enabled = true, ThresholdDb = -70 },
        Eq = FullEq.Eq,
        Comp = Comp().Comp,
    }.Normalized();

    [Fact]
    public void Processing_does_not_allocate()
    {
        var chain = new ProcessingChain();
        var all = Everything;
        chain.Set(all);
        var buf = Signal.Sine(440, Rate, Block, amplitude: 0.3);
        var other = all with { Comp = all.Comp with { ThresholdDb = -30 }, Eq = all.Eq.WithBand(1, b => b with { Type = EqBandType.HighShelf }) };
        long Run()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                if (i % 10 == 0) chain.Set(i % 20 == 0 ? other : all); // смена параметров — тоже без аллокаций в аудиопотоке
                chain.Process(buf, Block, Rate);
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        // Прогрев: многоуровневая компиляция подменяет код по ходу, и в этот момент рантайм может что-то выделить
        // в вызывающем потоке. Аудиопоток работает часами, важен установившийся режим.
        for (int i = 0; i < 3; i++) Run();
        Thread.Sleep(300);
        Assert.Equal(0, Enumerable.Range(0, 3).Min(_ => Run()));
    }

    // ── пресеты и настройки ──

    [Fact]
    public void Built_in_presets_are_within_the_specification_ranges()
    {
        foreach (var p in ChainPresets.BuiltIn)
        {
            Assert.Equal(p.Chain.Normalized(), p.Chain with { Eq = p.Chain.Eq.Normalized() });
            Assert.True(p.Chain.IsActive);
            var chain = new ProcessingChain();
            chain.Set(p.Chain.Normalized());
            var y = Process(chain, Signal.Sine(1000, Rate, Rate / 2, amplitude: 0.3));
            Assert.All(y, v => Assert.True(float.IsFinite(v) && Math.Abs(v) < 2));
        }
        Assert.Equal(3, ChainPresets.BuiltIn.Select(p => p.Name).Distinct().Count());
    }

    [Fact]
    public void Settings_are_clamped_to_the_specification()
    {
        var c = new ChainSettings
        {
            Gate = new GateSettings { ThresholdDb = -200, AttackMs = 0, HoldMs = 9999, ReleaseMs = 1, DepthDb = 5 },
            Eq = new EqSettings { HighPassHz = 10, Bands = [new EqBand { FreqHz = 5, GainDb = 40, Q = 99 }] },
            Comp = new CompSettings { ThresholdDb = 3, Ratio = 0.5f, AttackMs = 500, ReleaseMs = 1, KneeDb = 30, MakeupDb = 40 },
        }.Normalized();
        Assert.Equal((-80f, 0.1f, 500f, 5f, 0f), (c.Gate.ThresholdDb, c.Gate.AttackMs, c.Gate.HoldMs, c.Gate.ReleaseMs, c.Gate.DepthDb));
        Assert.Equal(40, c.Eq.HighPassHz);
        Assert.Equal((20f, 18f, 10f), (c.Eq.Bands[0].FreqHz, c.Eq.Bands[0].GainDb, c.Eq.Bands[0].Q));
        Assert.Equal(EqSettings.BandCount, c.Eq.Bands.Count);
        Assert.Equal((0f, 1f, 100f, 10f, 12f, 24f), (c.Comp.ThresholdDb, c.Comp.Ratio, c.Comp.AttackMs, c.Comp.ReleaseMs, c.Comp.KneeDb, c.Comp.MakeupDb));
    }
}

/// <summary>Замеры времени — без параллельных тестов, иначе чужие потоки съедают процессор и цифра «плавает».</summary>
[CollectionDefinition(nameof(SerialTiming), DisableParallelization = true)]
public sealed class SerialTiming;

[Collection(nameof(SerialTiming))]
public class ProcessingBudgetTests(ITestOutputHelper output)
{
    private const int Rate = 48000;
    private const int Block = 480;

    private static ChainSettings Everything => ProcessingTests.Everything;

    [Fact]
    public void Full_chain_on_a_stereo_node_costs_under_three_tenths_of_a_percent_of_a_core()
    {
        var chain = new ProcessingChain();
        chain.Set(Everything);
        var rnd = new Random(1);
        var buf = new float[Block * 2];
        for (int i = 0; i < buf.Length; i++) buf[i] = (float)(rnd.NextDouble() - 0.5) * 0.4f;
        var work = new float[buf.Length];
        const int blocks = 1000; // 10 с звука
        double best = double.MaxValue;
        for (int run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blocks; i++)
            {
                buf.CopyTo(work, 0);
                chain.Process(work, Block, Rate);
            }
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
        }
        double pct = best / (blocks * Block / (double)Rate) * 100;
        output.WriteLine($"полная цепочка, стерео 48 кГц: {pct:F3}% одного ядра");
        // бюджет — для выпускной сборки: в Debug JIT не оптимизирует
        bool optimized = typeof(ProcessingChain).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .Cast<DebuggableAttribute>().All(d => !d.IsJITOptimizerDisabled);
        if (optimized) Assert.True(pct < 0.3, $"{pct:F3}%");
    }
}
