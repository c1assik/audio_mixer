using System.Diagnostics;
using System.Text.Json.Nodes;
using Mixer.Audio.Dsp;
using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Config;
using Mixer.Core.Graph;
using Mixer.Core.Plugins;
using Mixer.Plugins.Host;
using Xunit.Abstractions;

namespace Mixer.Tests;

/// <summary>
/// VST, этап 3 «Движок»: раскладка закреплённых шин и их отводы, слот в аудиопотоке (кроссфейды с учётом задержки,
/// NaN, просрочки), управляющий слой (перезапуск, автоотключение, обход, частота), сборка графа, задержка путей.
/// Тесты со слотами запускают настоящий хост со встроенными плагинами; без C++-сборки они проходят пустыми.
/// </summary>
public class PluginEngineTests(ITestOutputHelper output)
{
    private const int Rate = 48000, Block = 480;

    // ── закреплённые шины: раскладка ──

    private static EngineGraph PinnedGraph(PluginSlot? slot, string? clock = null) =>
        new([new SourceSpec("mic", SourceKind.DeviceInput, "e-mic")],
            [new OutputSpec("phones", "e-hp"), new OutputSpec("cable", "e-ci") { LowLatency = true }],
            [new WireSpec("mic", "a"), new WireSpec("a", "fx"), new WireSpec("a", "phones"), new WireSpec("fx", "phones"), new WireSpec("fx", "cable")])
        {
            Buses = [new BusSpec("a"), new BusSpec("fx") { Plugins = slot is null ? [] : [slot], ClockOutput = clock }],
        };

    [Fact]
    public void PinnedBus_IsComputedOnceByItsMainOutput()
    {
        var routes = MixPlanner.Plan(PinnedGraph(new PluginSlot("p1", 16)), out var clocks).ToDictionary(r => r.OutputId);

        Assert.Equal("cable", clocks["fx"]); // «Низкая задержка» — период короче
        Assert.Equal(["a", "fx"], routes["cable"].Buses);
        Assert.Empty(routes["cable"].RemoteWires);
        // наушники считают шину «a» (она идёт в них и напрямую), но не fx: её звук приходит проводом с кольцом
        Assert.Equal(["a"], routes["phones"].Buses);
        var remote = Assert.Single(routes["phones"].RemoteWires);
        Assert.Equal(("fx", "phones"), (remote.From, remote.To));
        Assert.DoesNotContain(routes["phones"].BusWires, w => w.From == "fx");
    }

    [Fact]
    public void PinnedBus_FollowsTheChosenClock_AndBusWithoutPluginsIsNotPinned()
    {
        var chosen = MixPlanner.Plan(PinnedGraph(new PluginSlot("p1", 16), clock: "phones"), out var clocks).ToDictionary(r => r.OutputId);
        Assert.Equal("phones", clocks["fx"]);
        Assert.Equal(["a", "fx"], chosen["phones"].Buses);
        Assert.Equal(["fx"], chosen["cable"].RemoteWires.Select(w => w.From));
        Assert.DoesNotContain("a", chosen["cable"].Buses); // в кабель «a» идёт только через fx

        var plain = MixPlanner.Plan(PinnedGraph(null), out var none).ToDictionary(r => r.OutputId);
        Assert.Empty(none);
        Assert.Contains("fx", plain["phones"].Buses);
        Assert.Contains("fx", plain["cable"].Buses);
    }

    /// <summary>Шина, закреплённая за одним выходом, звучит и во втором: отвод пишет её сумму в кольцо второго.</summary>
    [Fact]
    public void PinnedBus_TapCarriesItsSumToTheOtherOutput()
    {
        var o = new EngineOptions { UseMmcss = false };
        var micClock = new SourceClock();
        var bus = new BusInstance("fx", "cable", o.MaxBlockFrames);
        bus.SetGain(-6.0206f, 0, false); // ×0,5
        var micToBus = new Wire("mic", "fx", micClock, o, "cable");
        var tap = new Wire("fx", "phones", bus.Clock, o, "phones");
        bus.PublishTaps([tap]);
        var cable = new MixPlan { Steps = [new MixStep { Bus = bus, Wires = [micToBus], Links = [] }], Wires = [], Links = [] };
        var phones = new MixPlan { Steps = [], Wires = [tap], Links = [] };

        micClock.Started(Rate);
        var input = new float[Block * 2];
        Array.Fill(input, 0.4f);
        var mixA = new float[Block * 2];
        var mixB = new float[Block * 2];
        var scratch = new float[Block * 2];
        long tick = Stopwatch.Frequency / 100, now = 0;
        for (int i = 0; i < 200; i++)
        {
            now += tick;
            micToBus.Write(input, Block, now);
            Array.Clear(mixA);
            cable.Render(mixA, scratch, Block, Rate, now + tick / 3);
            Array.Clear(mixB);
            phones.Render(mixB, scratch, Block, Rate, now + 2 * tick / 3);
        }
        output.WriteLine($"кабель {bus.Output(Block)[^1]:F4}, наушники {mixB[^1]:F4}");
        Assert.All(bus.Output(Block).ToArray(), x => Assert.InRange(x, 0.199f, 0.201f));
        Assert.All(mixB, x => Assert.InRange(x, 0.199f, 0.201f));
        Assert.Equal(WireState.Running, tap.State);
    }

    // ── слот в аудиопотоке ──

    private static float[] Constant(float v)
    {
        var b = new float[Block * 2];
        Array.Fill(b, v);
        return b;
    }

    private static long Deadline(double ms = 4) => Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);

    private async Task<(PluginHost Host, PluginInfo Info)> Load(string builtin, BuiltinOptions? options = null)
    {
        var host = TestHost.Start(output);
        var info = await host.LoadAsync(PluginLoad.Test(builtin, options));
        return (host, info);
    }

    [Fact]
    public async Task Slot_FadesInOver10ms_ThenProcesses()
    {
        if (!TestHost.Available()) return;
        var (host, _) = await Load("gain");
        using var _h = host;
        var echo = new TaskCompletionSource();
        host.ParamChanged += p => { if (p.Id == 0) echo.TrySetResult(); };
        host.SetParam(0, 0.25); // ×0,5
        await TestHost.Within(echo.Task.ContinueWith(_ => 0));

        var slot = new PluginSlot("p1", 8192) { Wet = true };
        slot.Attach(host.Audio, Rate, 0);
        var first = Constant(0.5f);
        slot.Process(first, Block, Rate, Deadline(1000));
        Assert.InRange(first[0], 0.49f, 0.5f);       // начало перехода — почти сухой
        Assert.InRange(first[^1], 0.249f, 0.251f);   // 480 кадров = 10 мс — уже обработанный
        var next = Constant(0.5f);
        slot.Process(next, Block, Rate, Deadline(1000));
        Assert.All(next, x => Assert.InRange(x, 0.249f, 0.251f));
        Assert.True(slot.TakeLoad().Frames >= 2 * Block);
        Assert.True(slot.Detach());
    }

    /// <summary>Сухой сигнал задержан на задержку плагина: выключение и включение не сдвигают звук ни на отсчёт.</summary>
    [Fact]
    public async Task Slot_BypassKeepsThePluginLatency()
    {
        if (!TestHost.Available()) return;
        var (host, info) = await Load("latency", new BuiltinOptions { Latency = 512 });
        using var _h = host;
        Assert.Equal(512, info.LatencySamples);
        var slot = new PluginSlot("p1", 8192) { Wet = true };
        slot.Attach(host.Audio, Rate, info.LatencySamples);

        var input = new List<float>();
        var outL = new List<float>();
        for (int b = 0; b < 60; b++)
        {
            if (b == 20) slot.Wet = false;
            if (b == 40) slot.Wet = true;
            var buf = new float[Block * 2];
            for (int i = 0; i < Block; i++)
            {
                float v = MathF.Sin(2 * MathF.PI * 440 * (b * Block + i) / Rate) * 0.5f;
                buf[2 * i] = buf[2 * i + 1] = v;
                input.Add(v);
            }
            slot.Process(buf, Block, Rate, Deadline(1000));
            for (int i = 0; i < Block; i++) outL.Add(buf[2 * i]);
        }
        double worst = 0;
        for (int n = 1024; n < outL.Count; n++) worst = Math.Max(worst, Math.Abs(outL[n] - input[n - 512]));
        output.WriteLine($"наибольшее отличие от входа, задержанного на 512: {worst:E2}");
        Assert.True(worst < 1e-5, $"{worst}");
        slot.Detach();
    }

    [Fact]
    public async Task Slot_NaN_IsReplacedByDryAndReported()
    {
        if (!TestHost.Available()) return;
        var (host, _) = await Load("nan", new BuiltinOptions { After = 3 });
        using var _h = host;
        var slot = new PluginSlot("p1", 8192) { Wet = true };
        slot.Attach(host.Audio, Rate, 0);
        bool bad = false;
        for (int b = 0; b < 10; b++)
        {
            var buf = Constant(0.3f);
            slot.Process(buf, Block, Rate, Deadline(1000));
            Assert.All(buf, x => Assert.True(float.IsFinite(x)));
            bad |= slot.TakeBadOutput();
            if (b == 9) Assert.All(buf, x => Assert.Equal(0.3f, x)); // дальше — только сухой
        }
        Assert.True(bad);
        slot.Detach();
    }

    [Fact]
    public async Task Slot_LateHostGoesDryAndCountsMisses()
    {
        if (!TestHost.Available()) return;
        var (host, _) = await Load("sleep", new BuiltinOptions { SleepMs = 20 });
        using var _h = host;
        var slot = new PluginSlot("p1", 8192) { Wet = true };
        slot.Attach(host.Audio, Rate, 0);
        for (int b = 0; b < 5; b++)
        {
            var buf = Constant(0.3f);
            slot.Process(buf, Block, Rate, Deadline(2));
            Assert.All(buf, x => Assert.Equal(0.3f, x));
        }
        Assert.True(slot.TakeMisses() >= 1);
        slot.Detach();
    }

    [Fact]
    public void Chain_WithOnlyPlugins_IsNotIdle_AndSumsLatency()
    {
        var chain = new ProcessingChain();
        var a = new PluginSlot("a", 16);
        var b = new PluginSlot("b", 16) { Post = true };
        a.SetLatency(100);
        b.SetLatency(28);
        chain.SetPlugins([a, b]);
        Assert.Equal(128, chain.LatencySamples);
        var buf = new float[32];
        Array.Fill(buf, 0.5f);
        chain.Process(buf, 16, Rate);
        Assert.True(chain.Meters.In.Take().L > 0.4f); // цепочка работала: только плагины, встроенные блоки выключены
        Assert.Equal(Rate, a.SeenRate);
        Assert.Equal(0f, buf[0]); // сухой задержан на 100 кадров
    }

    // ── управляющий слой ──

    private static GraphConfig Graph(params PluginConfig[] plugins) => new()
    {
        Nodes = [new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, Name = "Mic", Plugins = [.. plugins] }],
    };

    private static PluginConfig Test(string id, string kind, PluginGroup group = PluginGroup.Pre, bool enabled = true) =>
        new() { Id = id, Format = "test", Path = kind, Name = kind, Group = group, Enabled = enabled };

    private PluginRack Rack(Dictionary<string, BuiltinOptions>? options = null, PluginRackOptions? o = null) =>
        new((o ?? new PluginRackOptions()) with
        {
            HostExe = TestHost.Path,
            Resolve = p => p.Path == "none" ? null : PluginLoad.Test(p.Path!, options?.GetValueOrDefault(p.Path!)),
        }, s => { try { output.WriteLine(s); } catch (InvalidOperationException) { } });

    /// <summary>«Аудиопоток» узла: блоки по 10 мс в слоты узла с дедлайном 40% периода.</summary>
    internal sealed class Pump : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        public volatile bool AllFinite = true;

        public Pump(PluginRack rack, string node, int rate = Rate)
        {
            _thread = new Thread(() =>
            {
                var buf = new float[Block * 2];
                long n = 0;
                while (!_stop)
                {
                    for (int i = 0; i < Block; i++, n++) buf[2 * i] = buf[2 * i + 1] = MathF.Sin(n * 0.05f) * 0.3f;
                    long start = Stopwatch.GetTimestamp();
                    foreach (var s in rack.Slots(node)) s.Process(buf, Block, rate, PluginSlot.Deadline(start, 0.01));
                    if (buf.Any(x => !float.IsFinite(x))) AllFinite = false;
                    Thread.Sleep(10);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(2000);
        }
    }

    internal static async Task<bool> Until(Func<bool> condition, double seconds, PluginRack? tick = null)
    {
        var sw = Stopwatch.StartNew();
        double nextTick = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (condition()) return true;
            if (tick is not null && sw.Elapsed.TotalSeconds >= nextTick)
            {
                tick.Tick();
                nextTick = sw.Elapsed.TotalSeconds + 0.25;
            }
            await Task.Delay(20);
        }
        return condition();
    }

    private static PluginSlotStatus StatusOf(PluginRack rack, string slot) => rack.Status.Single(s => s.SlotId == slot);

    [Fact]
    public async Task Rack_LoadsSlots_AndSwitchesOffThroughThePluginBypass()
    {
        if (!TestHost.Available()) return;
        using var rack = Rack();
        rack.Sync(Graph(Test("p1", "gain"), Test("p2", "copy", PluginGroup.Post)));
        Assert.True(await Until(() => rack.Status.All(s => s.State == PluginSlotState.Running), 10));
        var slots = rack.Slots("mic");
        Assert.Equal(["p1", "p2"], slots.Select(s => s.Id));
        Assert.True(slots[1].Post);

        rack.Sync(Graph(Test("p1", "gain", enabled: false), Test("p2", "copy", PluginGroup.Post, enabled: false)));
        Assert.Equal(PluginSlotState.Off, StatusOf(rack, "p1").State);
        Assert.True(slots[0].Wet);   // у gain есть kIsBypass: звук идёт через плагин, он сам в обходе
        Assert.False(slots[1].Wet);  // у copy его нет: кроссфейд в сухой
        Assert.Same(slots[0], rack.Slots("mic")[0]); // тот же слот, хост не перезапускался
    }

    /// <summary>Критерий: снять процесс хоста — звук без разрыва уходит в сухой, через 3 с плагин снова работает с тем же состоянием.</summary>
    [Fact]
    public async Task Rack_RestartsACrashedHostWithItsLastState()
    {
        if (!TestHost.Available()) return;
        using var rack = Rack(o: new PluginRackOptions { StateDelay = TimeSpan.FromMilliseconds(200) });
        var saved = new TaskCompletionSource<byte[]>();
        rack.StateSaved += (_, _, blob) => saved.TrySetResult(blob);
        rack.Sync(Graph(Test("p1", "gain")));
        Assert.True(await Until(() => StatusOf(rack, "p1").State == PluginSlotState.Running, 10));
        using var pump = new Pump(rack, "mic");

        // правка «в окне плагина» → dirty → через 2 с тишины состояние забирается
        rack.HostOf("mic", "p1")!.SendDebug(new JsonObject { ["op"] = "edit", ["id"] = 0, ["value"] = 0.25 });
        Assert.True(await Until(() => saved.Task.IsCompleted, 5, rack));

        var victim = Process.GetProcessById(rack.HostOf("mic", "p1")!.ProcessId);
        var sw = Stopwatch.StartNew();
        victim.Kill();
        Assert.True(await Until(() => StatusOf(rack, "p1").Fault == PluginFault.Crashed, 3));
        Assert.True(await Until(() => StatusOf(rack, "p1").State == PluginSlotState.Running, 5));
        output.WriteLine($"снова работает через {sw.Elapsed.TotalSeconds:F2} с");
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.9, 3.0);
        var gain = rack.HostOf("mic", "p1")!.Info!.Params.Single(p => p.Id == 0);
        Assert.Equal(0.25, gain.Value, 6);
        Assert.True(pump.AllFinite);
    }

    [Fact]
    public async Task Rack_ThreeCrashesInFiveMinutes_WaitForRestart()
    {
        if (!TestHost.Available()) return;
        using var rack = Rack(o: new PluginRackOptions { RestartDelay = TimeSpan.FromMilliseconds(100) });
        rack.Sync(Graph(Test("p1", "copy")));
        for (int i = 0; i < 3; i++)
        {
            Assert.True(await Until(() => StatusOf(rack, "p1").State == PluginSlotState.Running, 10));
            Process.GetProcessById(rack.HostOf("mic", "p1")!.ProcessId).Kill();
            Assert.True(await Until(() => StatusOf(rack, "p1").State != PluginSlotState.Running, 3));
        }
        await Task.Delay(500);
        Assert.Equal(PluginFault.CrashedRepeatedly, StatusOf(rack, "p1").Fault);
        Assert.Null(rack.HostOf("mic", "p1"));

        Assert.True(rack.Restart("mic", "p1"));
        Assert.True(await Until(() => StatusOf(rack, "p1").State == PluginSlotState.Running, 10));
    }

    /// <summary>Критерий: плагин «спит 50 мс» выключается не позже чем через 10 с.</summary>
    [Fact]
    public async Task Rack_SlowPluginIsSwitchedOff()
    {
        if (!TestHost.Available()) return;
        using var rack = Rack(new() { ["sleep"] = new BuiltinOptions { SleepMs = 50 } });
        rack.Sync(Graph(Test("p1", "sleep")));
        Assert.True(await Until(() => StatusOf(rack, "p1").State == PluginSlotState.Running, 10));
        using var pump = new Pump(rack, "mic");
        var sw = Stopwatch.StartNew();
        Assert.True(await Until(() => StatusOf(rack, "p1").Fault == PluginFault.TooSlow, 10, rack));
        output.WriteLine($"выключен через {sw.Elapsed.TotalSeconds:F2} с: {StatusOf(rack, "p1").Detail}");
        Assert.Null(rack.HostOf("mic", "p1"));
    }

    [Fact]
    public async Task Rack_NaNFailsTheSlot()
    {
        if (!TestHost.Available()) return;
        using var rack = Rack(new() { ["nan"] = new BuiltinOptions { After = 10 } });
        rack.Sync(Graph(Test("p1", "nan")));
        Assert.True(await Until(() => StatusOf(rack, "p1").State == PluginSlotState.Running, 10));
        using var pump = new Pump(rack, "mic");
        Assert.True(await Until(() => StatusOf(rack, "p1").Fault == PluginFault.BadOutput, 5, rack));
        Assert.True(pump.AllFinite);
    }

    [Fact]
    public async Task Rack_FollowsTheNodeRate()
    {
        if (!TestHost.Available()) return;
        using var rack = Rack();
        rack.Sync(Graph(Test("p1", "gain")));
        Assert.True(await Until(() => StatusOf(rack, "p1").State == PluginSlotState.Running, 10));
        Assert.Equal(48000, StatusOf(rack, "p1").Rate);
        using var pump = new Pump(rack, "mic", 44100);
        Assert.True(await Until(() => StatusOf(rack, "p1").Rate == 44100, 5, rack));
    }

    [Fact]
    public void Rack_LimitsAndMissingPlugins_DoNotStartHosts()
    {
        using var rack = Rack();
        rack.Sync(Graph(Test("p1", "none"), Test("p2", "none"), Test("p3", "copy")));
        Assert.Equal(PluginSlotState.Missing, StatusOf(rack, "p1").State);
        Assert.Equal(PluginFault.NotFound, StatusOf(rack, "p1").Fault);
        Assert.Equal(PluginFault.Limit, StatusOf(rack, "p3").Fault); // третий «до» на узле
        Assert.Null(rack.HostOf("mic", "p3"));

        rack.Sync(Graph());
        Assert.Empty(rack.Status);
        Assert.Empty(rack.Slots("mic"));
    }

    // ── сборка графа и graph.json ──

    private static readonly EndpointInfo Headphones = new("{0.0.0.00000000}.{hp}", "Headphones", EDataFlow.Render, true);
    private static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Shure MV7", EDataFlow.Capture, true);

    [Fact]
    public void Builder_PassesSlots_AppNodeGetsAnFxBus_NoDirectRoute()
    {
        var slot = new PluginSlot("p1", 16);
        var p = new PluginConfig { Id = "p1", Name = "Denoiser" };
        var g = new GraphConfig
        {
            Nodes =
            [
                new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, EndpointId = Mic.Id, EndpointName = Mic.Name, Plugins = [p] },
                new NodeConfig { Id = "game", Kind = NodeKind.App, Exe = "game.exe", Plugins = [p], ClockOutput = "hp" },
                new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput, EndpointId = Headphones.Id, EndpointName = Headphones.Name },
            ],
            Wires = [new WireConfig { From = "mic", To = "hp" }, new WireConfig { From = "game", To = "hp" }],
        };
        var r = GraphBuilder.Build(g, [Headphones, Mic], apps: new RuntimeContext
        {
            Roots = exe => exe == "game.exe" ? [42u] : [],
            DirectRoutes = true,
            Plugins = id => id is "mic" or "game" ? [slot] : [],
        });
        Assert.Same(slot, Assert.Single(r.Graph.Sources.Single(s => s.Id == "mic").Plugins));
        var fx = Assert.Single(r.Graph.Buses);
        Assert.Equal("game" + GraphBuilder.FxSuffix, fx.Id);
        Assert.True(fx.Pinned);
        Assert.Equal("hp", fx.ClockOutput);
        Assert.Empty(r.DirectRoutes); // плагину нужен захват
        Assert.Empty(r.Graph.Outputs.Single().Plugins);
    }

    [Fact]
    public void GraphJson_KeepsSlots_AndIsVersion3()
    {
        var g = Graph(new PluginConfig
        {
            Id = "p1", Group = PluginGroup.Post, Enabled = false, ClassId = "5A3B0000000000000000000000000E1",
            Name = "Denoiser", Vendor = "Example Audio", Path = @"C:\Program Files\Common Files\VST3\Denoiser.vst3",
            State = "plugins/mic-p1.state", EvenBlocks = true,
        });
        string json = GraphSerializer.Serialize(g);
        Assert.Contains("\"group\": \"post\"", json);
        var back = GraphSerializer.Deserialize(json);
        Assert.Equal(3, back.Version);
        Assert.Equal(g.Nodes[0].Plugins![0], back.Nodes[0].Plugins![0]);
    }

    [Fact]
    public void Latency_ParallelPathsWithDifferentPluginLatency_Warn()
    {
        var g = new GraphConfig
        {
            Nodes =
            [
                new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput },
                new NodeConfig { Id = "fx", Kind = NodeKind.Bus },
                new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput },
            ],
            Wires = [new WireConfig { From = "mic", To = "fx" }, new WireConfig { From = "fx", To = "hp" }, new WireConfig { From = "mic", To = "hp" }],
        };
        // задержка 512 на 48 кГц — 10,7 мс
        var ms = PluginLatency.ByNode([new PluginSlotStatus("fx", "p1", "Reverb", PluginSlotState.Running, PluginFault.None, null, 512, 48000, 0, false)]);
        Assert.Equal(10.667, ms["fx"], 3);
        var m = Assert.Single(PluginLatency.Mismatches(g, ms));
        Assert.Equal(("hp", "mic"), (m.OutputId, m.SourceId));
        Assert.Equal(["mic", "hp"], m.Fast);
        Assert.Equal(["mic", "fx", "hp"], m.Slow);
        Assert.Equal(10.667, PluginLatency.Max(g, ms)[("mic", "hp")], 3);

        // тот же провод выключен — один путь, предупреждения нет
        var single = g with { Wires = [g.Wires[0], g.Wires[1], g.Wires[2] with { Enabled = false }] };
        Assert.Empty(PluginLatency.Mismatches(single, ms));
    }
}
