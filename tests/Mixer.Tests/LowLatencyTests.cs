using System.Diagnostics;
using Mixer.App.ViewModels;
using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;
using Mixer.Core.Host;
using Xunit.Abstractions;

namespace Mixer.Tests;

/// <summary>v2, этап 4: «Низкая задержка» — IAudioClient3 по узлам, автооткат, адаптивная цель колец.</summary>
public class LowLatencyTests(ITestOutputHelper output)
{
    private const double P128 = 128 / 48000.0; // 2,67 мс — минимум VB-Cable на этой машине (этап 0)

    // ── цель заполнения кольца ──

    [Theory]
    [InlineData(0, 0, 20)]              // обычные 10 мс с обеих сторон — как в v1
    [InlineData(P128, P128, 5.333)]     // два малых периода
    [InlineData(0.010, P128, 20)]       // самый медленный конец решает
    [InlineData(P128, 0, 20)]
    [InlineData(0.001, 0.001, 4)]       // не меньше 4 мс
    [InlineData(0.0015, 0.0005, 4)]
    public void Ring_target_is_two_periods_of_the_slowest_end_but_at_least_4_ms(double src, double outp, double expectedMs)
    {
        double ms = Wire.TargetSeconds(new EngineOptions(), src, outp) * 1000;
        Assert.Equal(expectedMs, ms, 2);
    }

    [Fact]
    public void Wire_with_small_periods_on_both_ends_holds_its_small_target_without_underruns()
    {
        // 128 кадров на 48 кГц с обеих сторон, пробуждения запаздывают на 0…1 мс, источник уходит на +100 ppm.
        // С фейдом 2 мс, как у обычной цели, здесь было бы ~100 underrun в минуту: запас малой цели съедал резерв на фейд.
        var sim = new WireSimulation
        {
            SourceBlockMs = 128 / 48.0, OutputBlockMs = 128 / 48.0, ReportPeriods = true, JitterMs = 1, DriftPpm = 100,
        };
        var wire = sim.Run(60);
        var late = sim.Samples.Where(s => s.Time > 20).ToList();
        output.WriteLine($"цель {wire.TargetMs:F2} мс, заполнение {late.Min(s => s.FillMs):F2}…{late.Max(s => s.FillMs):F2} мс, " +
                         $"задержка провода {wire.LatencyMs:F2} мс, ε {wire.EpsilonPpm:F0} ppm, сбоев {wire.Underruns}/{wire.Overruns}/{wire.Resyncs}");
        Assert.Equal(5.33, wire.TargetMs, 1);
        Assert.Equal(0, wire.Underruns);
        Assert.Equal(0, wire.Overruns);
        Assert.All(late, s => Assert.InRange(s.FillMs, 4, 7));
        Assert.True(wire.LatencyMs < 8, $"провод {wire.LatencyMs:F2} мс");
    }

    [Theory]
    [InlineData(10, 128 / 48.0)] // микрофон по 10 мс → выход с малым периодом
    [InlineData(128 / 48.0, 10)] // вход с малым периодом → обычный выход
    public void Wire_between_a_small_and_a_usual_period_keeps_the_v1_target(double srcMs, double outMs)
    {
        var sim = new WireSimulation { SourceBlockMs = srcMs, OutputBlockMs = outMs, ReportPeriods = true, JitterMs = 1, DriftPpm = -200 };
        var wire = sim.Run(40);
        output.WriteLine($"цель {wire.TargetMs:F2} мс, сбоев {wire.Underruns}/{wire.Overruns}/{wire.Resyncs}, ε {wire.EpsilonPpm:F0} ppm");
        Assert.Equal(20, wire.TargetMs, 1);
        Assert.Equal(0, wire.Underruns);
    }

    [Fact]
    public void Wire_retargets_when_its_output_falls_back_to_the_usual_period()
    {
        const int rate = 48000, block = 128;
        var clock = new SourceClock();
        clock.Started(rate, P128);
        var wire = new Wire("in", "out", clock, new EngineOptions());
        var buf = new float[480 * 2];
        var mix = new float[480 * 2];
        var scratch = new float[480 * 2];
        long tick = (long)(Stopwatch.Frequency * P128);
        long now = 0;
        for (int i = 0; i < 400; i++)
        {
            now += tick;
            wire.Write(buf, block, now);
            wire.Render(mix, scratch, block, rate, now + tick / 2, i < 200 ? P128 : 0);
            if (i == 199) Assert.Equal(5.33, wire.TargetMs, 1);
        }
        Assert.Equal(20, wire.TargetMs, 1);
    }

    [Fact]
    public void Plan_passes_the_output_period_to_wires_into_buses_and_direct_wires()
    {
        var opt = new EngineOptions();
        var clock = new SourceClock();
        clock.Started(48000, P128);
        var direct = new Wire("mic", "out", clock, opt);
        var toBus = new Wire("mic", "bus", clock, opt, "out");
        var bus = new BusInstance("bus", "out", opt.MaxBlockFrames);
        var plan = new MixPlan
        {
            Steps = [new MixStep { Bus = bus, Wires = [toBus], Links = [] }],
            Wires = [direct],
            Links = [new BusLink(bus, "out")],
        };
        var buf = new float[128 * 2];
        var mix = new float[128 * 2];
        var scratch = new float[128 * 2];
        long tick = (long)(Stopwatch.Frequency * P128), now = 0;
        for (int i = 0; i < 100; i++)
        {
            now += tick;
            direct.Write(buf, 128, now);
            toBus.Write(buf, 128, now);
            plan.Render(mix, scratch, 128, 48000, now + tick / 2, P128);
        }
        Assert.Equal(5.33, direct.TargetMs, 1);
        Assert.Equal(5.33, toBus.TargetMs, 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Underrun_on_a_lowered_target_is_reported_to_the_output_as_a_glitch(bool small)
    {
        // выход с «Низкой задержкой» считает такие underrun своими сбоями: на обычной цели 20 мс они бы не случились
        const int rate = 48000, block = 128;
        double period = small ? P128 : 0;
        var clock = new SourceClock();
        clock.Started(rate, period);
        var wire = new Wire("in", "out", clock, new EngineOptions());
        var buf = new float[block * 2];
        var mix = new float[block * 2];
        var scratch = new float[block * 2];
        long tick = (long)(Stopwatch.Frequency * P128), now = 0;
        int reported = 0;
        for (int i = 0; i < 600; i++)
        {
            now += tick;
            if (i < 300 || i > 320) wire.Write(buf, block, now); // источник замолчал на 54 мс
            if (wire.Render(mix, scratch, block, rate, now + tick / 2, period)) reported++;
        }
        Assert.Equal(1, wire.Underruns);
        Assert.Equal(small ? 1 : 0, reported);
    }

    // ── очередь выхода ──

    [Fact]
    public void Output_queue_is_15_ms_as_in_v1_and_two_periods_with_a_small_period()
    {
        var o = new EngineOptions();
        Assert.Equal(720u, OutputNode.QueueFramesFor(48000, 1056, 0, o));       // 15 мс; буфер CABLE Input — 22 мс
        Assert.Equal(256u, OutputNode.QueueFramesFor(48000, 1056, P128, o));    // 128 + 128
        Assert.Equal(144u, OutputNode.QueueFramesFor(48000, 1056, 0.001, o));   // 1 мс + запас 2 мс
        Assert.Equal(200u, OutputNode.QueueFramesFor(48000, 200, P128, o));     // не больше буфера устройства
    }

    // ── автооткат ──

    [Fact]
    public void Glitch_window_trips_on_the_fourth_glitch_within_a_minute()
    {
        const long s = 1000;
        var g = new GlitchWindow(ticksPerSecond: s);
        Assert.False(g.Add(0));
        Assert.False(g.Add(10 * s));
        Assert.False(g.Add(20 * s));
        Assert.True(g.Add(30 * s));     // 4 сбоя за 30 с: больше 3 за минуту

        g.Reset();
        // по сбою каждые 25 с: в любой минуте не больше трёх
        for (int i = 0; i < 20; i++) Assert.False(g.Add(i * 25 * s));

        g.Reset();
        Assert.False(g.Add(0));
        Assert.False(g.Add(1 * s));
        Assert.False(g.Add(2 * s));
        Assert.False(g.Add(61 * s));    // первый ушёл из окна
        Assert.False(g.Add(62 * s));    // 1, 2, 61, 62 — это 61 с, не минута
        Assert.False(g.Add(63 * s));
        Assert.True(g.Add(64 * s));     // 61, 62, 63, 64
    }

    [Fact]
    public void Node_that_fell_back_before_starts_with_the_usual_period()
    {
        var fb = new LowLatencyInfo(LowLatencyStatus.FellBack, 48000, 128, 128, 480);
        var opt = new EngineOptions();
        var again = new OutputNode("o", "{x}", opt, lowLatency: true, fallback: fb);
        Assert.True(again.LowLatencyRequested);
        Assert.Equal(LowLatencyStatus.FellBack, again.LowLatency.Status);
        Assert.Same(fb, again.FellBack);
        // режим выключили — откат забыт
        var off = new OutputNode("o", "{x}", opt, lowLatency: false, fallback: fb);
        Assert.Equal(LowLatencyStatus.Off, off.LowLatency.Status);
        Assert.Null(off.FellBack);
        // у loopback и захвата приложений режима нет: их период задаёт чужой поток
        Assert.False(new SourceNode("lb", SourceKind.Loopback, "{x}", opt, lowLatency: true).LowLatencyRequested);
        Assert.False(new SourceNode("a", SourceKind.App, "", opt, 1234, 48000, lowLatency: true).LowLatencyRequested);
        Assert.True(new SourceNode("mic", SourceKind.DeviceInput, "{x}", opt, lowLatency: true).LowLatencyRequested);
    }

    [Fact]
    public void Low_latency_info_reports_periods_in_ms()
    {
        var i = new LowLatencyInfo(LowLatencyStatus.Active, 48000, 128, 128, 480);
        Assert.Equal(2.667, i.PeriodMs, 3);
        Assert.Equal(10, i.DefaultMs, 3);
        Assert.Contains("128 frames (2.67 ms", MixerHost.DescribeLowLatency(i));
        Assert.Contains("minimum period 480 frames (10 ms)",
            MixerHost.DescribeLowLatency(new LowLatencyInfo(LowLatencyStatus.Unsupported, 48000, 480, 480, 480)));
        Assert.Contains("more than 3 glitches", MixerHost.DescribeLowLatency(i with { Status = LowLatencyStatus.FellBack }));
    }

    // ── граф и graph.json ──

    private static readonly EndpointInfo Hp = new("{0.0.0.00000000}.{hp}", "Наушники (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Микрофон (fifine)", EDataFlow.Capture, true);

    private static GraphConfig Graph(bool? micLow = null, bool? hpLow = null, bool? lbLow = null) => new()
    {
        Nodes =
        [
            new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, Name = "Микрофон", EndpointId = Mic.Id, EndpointName = Mic.Name, LowLatency = micLow },
            new NodeConfig { Id = "lb", Kind = NodeKind.Loopback, Name = "Звук наушников", EndpointId = Hp.Id, EndpointName = Hp.Name, LowLatency = lbLow },
            new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput, Name = "Наушники", EndpointId = Hp.Id, EndpointName = Hp.Name, X = GraphLayout.OutputX, LowLatency = hpLow },
        ],
        Wires = [new WireConfig { From = "mic", To = "hp" }],
    };

    [Fact]
    public void Low_latency_is_on_for_outputs_and_off_for_inputs_by_default()
    {
        var g = Graph();
        Assert.True(g.Nodes[2].LowLatencyOn);
        Assert.False(g.Nodes[0].LowLatencyOn);
        Assert.False(g.Nodes[1].LowLatencyOn);
        Assert.False(Graph(lbLow: true).Nodes[1].LowLatencyOn); // у loopback режима нет
        Assert.True(Graph(micLow: true).Nodes[0].LowLatencyOn);
        Assert.False(Graph(hpLow: false).Nodes[2].LowLatencyOn);

        var r = GraphBuilder.Build(Graph(), [Hp, Mic]);
        Assert.True(r.Graph.Outputs.Single().LowLatency);
        Assert.False(r.Graph.Sources.Single(s => s.Id == "mic").LowLatency);
        var r2 = GraphBuilder.Build(Graph(micLow: true, hpLow: false), [Hp, Mic]);
        Assert.False(r2.Graph.Outputs.Single().LowLatency);
        Assert.True(r2.Graph.Sources.Single(s => s.Id == "mic").LowLatency);
    }

    [Fact]
    public void Low_latency_is_off_by_default_on_a_virtual_cable_output()
    {
        var cable = new EndpointInfo("{0.0.0.00000000}.{ci}", "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
        var node = new NodeConfig { Id = "st", Kind = NodeKind.DeviceOutput, Name = "Стрим", EndpointId = cable.Id, EndpointName = cable.Name };
        Assert.False(node.LowLatencyOn);
        Assert.True((node with { LowLatency = true }).LowLatencyOn);
        var g = new GraphConfig { Nodes = [node] };
        Assert.False(GraphBuilder.Build(g, [cable]).Graph.Outputs.Single().LowLatency);
        Assert.True(GraphBuilder.Build(g with { Nodes = [node with { LowLatency = true }] }, [cable]).Graph.Outputs.Single().LowLatency);
    }

    [Fact]
    public void Low_latency_survives_json_and_changing_it_is_a_structural_edit()
    {
        var g = Graph(micLow: true, hpLow: false);
        string json = GraphSerializer.Serialize(g);
        Assert.Contains("\"lowLatency\": true", json);
        Assert.Contains("\"lowLatency\": false", json);
        var back = GraphSerializer.Deserialize(json);
        Assert.True(back.Nodes[0].LowLatency);
        Assert.False(back.Nodes[2].LowLatency);
        Assert.Null(back.Nodes[1].LowLatency);
        // по умолчанию поле не пишется: файл v1 и новый файл без настроек одинаковы
        Assert.DoesNotContain("lowLatency", GraphSerializer.Serialize(Graph()));

        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(Graph(), Graph(hpLow: false)));
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(Graph(), Graph(micLow: true)));
    }

    [Fact]
    public void Menu_toggle_writes_only_non_default_values_and_undoes()
    {
        WpfThread.Run(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "graph.json");
            var g = Graph();
            using var store = new GraphStore(path, g);
            var vm = new MainViewModel(g, store, host: null, offlineDevices: [Hp, Mic]);
            var hp = vm.FindNode("hp")!;
            var mic = vm.FindNode("mic")!;
            var lb = vm.FindNode("lb")!;
            Assert.True(hp.SupportsLowLatency && mic.SupportsLowLatency);
            Assert.False(lb.SupportsLowLatency);
            Assert.True(hp.LowLatency);
            Assert.False(mic.LowLatency);

            hp.LowLatency = false;
            Assert.False(vm.Graph.Nodes.Single(n => n.Id == "hp").LowLatency);
            hp.LowLatency = true;
            Assert.Null(vm.Graph.Nodes.Single(n => n.Id == "hp").LowLatency); // снова по умолчанию
            mic.LowLatency = true;
            Assert.True(vm.Graph.Nodes.Single(n => n.Id == "mic").LowLatency);

            vm.UndoCommand.Execute(null);
            Assert.Null(vm.Graph.Nodes.Single(n => n.Id == "mic").LowLatency);
            Assert.False(vm.FindNode("mic")!.LowLatency);
            vm.Dispose();
        });
    }

    [Fact]
    public void Card_tooltip_explains_the_low_latency_state()
    {
        Assert.Equal("Низкая задержка: период 2,7 мс вместо 10 мс",
            MainViewModel.LowLatencyTip(new LowLatencyInfo(LowLatencyStatus.Active, 48000, 128, 128, 480)));
        Assert.Equal("Низкая задержка недоступна: драйвер не даёт период меньше 10 мс",
            MainViewModel.LowLatencyTip(new LowLatencyInfo(LowLatencyStatus.Unsupported, 48000, 480, 480, 480)));
        Assert.Contains("AUDCLNT_E_ENGINE_PERIODICITY_LOCKED",
            MainViewModel.LowLatencyTip(new LowLatencyInfo(LowLatencyStatus.Unavailable) { Error = "InitializeSharedAudioStream: 0x88890028 AUDCLNT_E_ENGINE_PERIODICITY_LOCKED" }));
        Assert.Null(MainViewModel.LowLatencyTip(LowLatencyInfo.Off));
        Assert.Null(MainViewModel.LowLatencyTip(null));
    }
}
