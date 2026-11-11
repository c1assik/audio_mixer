using System.Diagnostics;
using Mixer.App.ViewModels;
using Mixer.Audio.Dsp;
using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;
using Xunit.Abstractions;

namespace Mixer.Tests;

/// <summary>
/// v2, этап 5: перенос graph.json с v1 и критерии приёмки 2.0, которые проверяются без звуковой карты
/// (остальные — на устройствах, см. docs/DEVELOPMENT.md, «v2 stage 5»).
/// </summary>
public class MigrationV2Tests
{
    private static readonly EndpointInfo Hp = new("{0.0.0.00000000}.{hp}", "Наушники (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo Cable = new("{0.0.0.00000000}.{ci}", "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Микрофон (fifine)", EDataFlow.Capture, true);
    private static readonly List<EndpointInfo> Devices = [Hp, Cable, Mic];

    /// <summary>graph.json в том виде, в каком его пишет v1 (1.0.0): без шин, обработки и полей v2.</summary>
    private const string V1Json = """
        {
          "version": 1,
          "sinkEndpoint": "{0.0.0.00000000}.{ci}",
          "boundApps": [],
          "nodes": [
            { "id": "n1", "kind": "deviceInput", "name": "Микрофон", "endpointId": "{0.0.1.00000000}.{mic}", "endpointName": "Микрофон (fifine)",
              "gainDb": -3, "muted": false, "mono": true, "x": 40, "y": 60, "color": "#4FA3FF" },
            { "id": "n2", "kind": "loopback", "name": "Звук наушников", "endpointId": "{0.0.0.00000000}.{hp}", "endpointName": "Наушники (Realtek)",
              "gainDb": 0, "muted": false, "balance": -0.25, "x": 40, "y": 210, "color": "#FF9A4D" },
            { "id": "n3", "kind": "deviceOutput", "name": "Наушники", "endpointId": "{0.0.0.00000000}.{hp}", "endpointName": "Наушники (Realtek)",
              "gainDb": -2, "muted": false, "x": 760, "y": 60 },
            { "id": "n4", "kind": "deviceOutput", "name": "Стрим", "endpointId": "{0.0.0.00000000}.{ci}", "endpointName": "CABLE Input (VB-Audio Virtual Cable)",
              "gainDb": 0, "muted": true, "x": 760, "y": 210 }
          ],
          "wires": [
            { "from": "n1", "to": "n3", "gainDb": -6, "enabled": true },
            { "from": "n1", "to": "n4", "gainDb": 0, "enabled": true },
            { "from": "n2", "to": "n4", "gainDb": -9.5, "enabled": false }
          ]
        }
        """;

    private static string TempGraph(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "graph.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void V1_graph_sounds_the_same_after_migration_and_the_original_is_kept()
    {
        // критерий 2.0: «Схема v1 после переноса звучит так же. graph.v1.json сохранён»
        string path = TempGraph(V1Json);
        var g = GraphStore.LoadOrEmpty(path, out string? error, out bool upgraded);
        Assert.Null(error);
        Assert.True(upgraded);
        Assert.Equal(GraphConfig.CurrentVersion, g.Version);
        string kept = Path.Combine(Path.GetDirectoryName(path)!, "graph.v1.json");
        Assert.Equal(V1Json, File.ReadAllText(kept));

        // поля v2 пустые: обработки нет, шин нет, «Низкая задержка» — по умолчанию
        Assert.All(g.Nodes, n => Assert.Null(n.Chain));
        Assert.All(g.Nodes, n => Assert.Null(n.LowLatency));
        Assert.DoesNotContain(g.Nodes, n => n.IsBus);

        // снимок движка — тот же, что строит v1: те же источники, выходы, провода и уровни, без обработки и шин
        var e = GraphBuilder.Build(g, Devices).Graph;
        Assert.Empty(e.Buses);
        Assert.Equal(
            [
                new SourceSpec("n1", SourceKind.DeviceInput, Mic.Id, -3, false, Mono: true, Balance: 0),
                new SourceSpec("n2", SourceKind.Loopback, Hp.Id, 0, false, Mono: false, Balance: -0.25f),
            ],
            e.Sources.OrderBy(s => s.Id));
        Assert.Equal(
            [new OutputSpec("n3", Hp.Id, -2, false) { LowLatency = true }, new OutputSpec("n4", Cable.Id, 0, true)],
            e.Outputs.OrderBy(o => o.Id));
        Assert.Equal(
            [new WireSpec("n1", "n3", -6), new WireSpec("n1", "n4", 0), new WireSpec("n2", "n4", -9.5f, Enabled: false)],
            e.Wires.OrderBy(w => w.From).ThenBy(w => w.To));
        Assert.All(e.Sources, s => Assert.Null(s.Chain));
        Assert.All(e.Outputs, o => Assert.Null(o.Chain));

        // после записи файл уже версии 2, копия v1 не перезаписывается
        using (var store = new GraphStore(path, g))
        {
            store.Changed(g with { Nodes = [g.Nodes[0] with { GainDb = -4 }, .. g.Nodes.Skip(1)] });
            store.Flush();
        }
        var again = GraphStore.LoadOrEmpty(path, out _, out bool upgradedAgain);
        Assert.False(upgradedAgain);
        Assert.Equal(-4, again.Nodes[0].GainDb);
        Assert.Equal(V1Json, File.ReadAllText(kept));
    }

    private static GraphConfig Streamer() => new()
    {
        Nodes =
        [
            new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, Name = "Микрофон", EndpointId = Mic.Id, EndpointName = Mic.Name, Y = 60 },
            new NodeConfig { Id = "game", Kind = NodeKind.App, Name = "Игра", Exe = "game.exe", Y = 210 },
            new NodeConfig { Id = "discord", Kind = NodeKind.App, Name = "Discord", Exe = "discord.exe", Y = 360 },
            new NodeConfig { Id = "music", Kind = NodeKind.App, Name = "Музыка", Exe = "spotify.exe", Y = 510 },
            new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput, Name = "Наушники", EndpointId = Hp.Id, EndpointName = Hp.Name, X = GraphLayout.OutputX, Y = 60 },
            new NodeConfig { Id = "st", Kind = NodeKind.DeviceOutput, Name = "Стрим", EndpointId = Cable.Id, EndpointName = Cable.Name, X = GraphLayout.OutputX, Y = 210 },
        ],
        Wires =
        [
            // микрофон только в стрим; игра и Discord — в оба выхода (это и есть кандидаты в шину), музыка — в оба
            new WireConfig { From = "mic", To = "st" },
            new WireConfig { From = "game", To = "hp", GainDb = -3 },
            new WireConfig { From = "game", To = "st", GainDb = -9 },
            new WireConfig { From = "discord", To = "hp" },
            new WireConfig { From = "discord", To = "st", GainDb = -6 },
            new WireConfig { From = "music", To = "hp", GainDb = -12, Enabled = false },
        ],
    };

    [Fact]
    public void After_migration_sources_with_the_same_outputs_are_offered_as_a_bus()
    {
        var offers = GraphEdits.GroupSuggestions(Streamer());
        // микрофон идёт в один выход, у музыки провод выключен: они не кандидаты
        Assert.Equal([["game", "discord"]], offers);

        // уже сгруппированные и источники с одним выходом не предлагаются
        var grouped = GraphEdits.GroupIntoBus(Streamer(), ["game", "discord"], new NodeConfig { Id = "b1", Kind = NodeKind.Bus, Name = "Шина 1" }).Graph;
        Assert.Empty(GraphEdits.GroupSuggestions(grouped));
    }

    [Fact]
    public void Accepting_the_offer_groups_in_one_undo_step_and_keeps_every_level()
    {
        WpfThread.Run(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "graph.json");
            var g = Streamer();
            using var store = new GraphStore(path, g);
            var vm = new MainViewModel(g, store, host: null, offlineDevices: Devices);
            Assert.Null(vm.GroupOfferText);
            vm.OfferGrouping();
            Assert.Equal("Схема перенесена с версии 1. «Игра» и «Discord» идут в одни и те же выходы («Наушники» и «Стрим»). " +
                         "Сгруппировать в шину? Один фейдер на группу, проводов меньше, уровни не изменятся.", vm.GroupOfferText);

            vm.AcceptGroupOfferCommand.Execute(null);
            Assert.Null(vm.GroupOfferText);
            var bus = Assert.Single(vm.Graph.Nodes, n => n.IsBus);
            // уровни: источник → шина + шина → выход = прежний провод
            float Level(string src, string dst) =>
                vm.Graph.Wires.Single(w => w.From == src && w.To == bus.Id).GainDb + vm.Graph.Wires.Single(w => w.From == bus.Id && w.To == dst).GainDb;
            Assert.Equal(-3, Level("game", "hp"), 3);
            Assert.Equal(-9, Level("game", "st"), 3);
            Assert.Equal(0, Level("discord", "hp"), 3);
            Assert.Equal(-6, Level("discord", "st"), 3);
            Assert.DoesNotContain(vm.Graph.Wires, w => w.From is "game" or "discord" && w.To is "hp" or "st");

            vm.UndoCommand.Execute(null);
            Assert.DoesNotContain(vm.Graph.Nodes, n => n.IsBus);
            Assert.Equal(Streamer().Wires.Count, vm.Graph.Wires.Count);

            vm.OfferGrouping();
            vm.DismissGroupOfferCommand.Execute(null);
            Assert.Null(vm.GroupOfferText);
            Assert.DoesNotContain(vm.Graph.Nodes, n => n.IsBus);
            vm.Dispose();
        });
    }

    [Fact]
    public void Offer_skips_a_suggestion_whose_nodes_were_deleted_meanwhile()
    {
        WpfThread.Run(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "graph.json");
            var g = Streamer();
            using var store = new GraphStore(path, g);
            var vm = new MainViewModel(g, store, host: null, offlineDevices: Devices);
            vm.OfferGrouping();
            vm.DeleteNode(vm.FindNode("discord")!);
            vm.AcceptGroupOfferCommand.Execute(null); // предложение устарело: группировать некого
            Assert.DoesNotContain(vm.Graph.Nodes, n => n.IsBus);
            vm.Dispose();
        });
    }
}

/// <summary>
/// Критерий 2.0: «схема стримера из макета v2 (4 источника, 2 шины, 2 выхода) … CPU не выше 5% на 4-ядерном
/// процессоре, обработка включена на всех узлах». Здесь — вычислительная часть движка без устройств: обработка
/// источников, кольца с ресемплерами, шины (по экземпляру на выход), выходы с обработкой и ограничителем.
/// </summary>
[Collection(nameof(SerialTiming))]
public class StreamerBudgetTests(ITestOutputHelper output)
{
    [Fact]
    public void Streamer_layout_with_processing_on_every_node_fits_the_cpu_budget()
    {
        const int outRate = 48000, block = 480;
        var opt = new EngineOptions();
        var chain = ProcessingTests.Everything;

        // источники: микрофон 44,1 кГц (как fifine), игра, Discord, музыка по 48 кГц — у каждого полная цепочка
        var src = new (string Id, int Rate, SourceClock Clock, ProcessingChain Chain)[]
        {
            ("mic", 44100, new SourceClock(), new ProcessingChain()),
            ("game", 48000, new SourceClock(), new ProcessingChain()),
            ("discord", 48000, new SourceClock(), new ProcessingChain()),
            ("music", 48000, new SourceClock(), new ProcessingChain()),
        };
        foreach (var s in src)
        {
            s.Chain.Set(chain);
            s.Clock.Started(s.Rate);
        }

        // шины «Игра + чат» (игра, Discord → наушники и стрим) и «Эфир» (микрофон, музыка → стрим)
        var graph = new EngineGraph(
            src.Select(s => new SourceSpec(s.Id, SourceKind.DeviceInput, s.Id)).ToList(),
            [new OutputSpec("hp", "hp"), new OutputSpec("st", "st")],
            [
                new WireSpec("game", "chat"), new WireSpec("discord", "chat"),
                new WireSpec("mic", "air"), new WireSpec("music", "air"),
                new WireSpec("chat", "hp"), new WireSpec("chat", "st", -6), new WireSpec("air", "st"),
            ])
        { Buses = [new BusSpec("chat") { Chain = chain }, new BusSpec("air") { Chain = chain }] };

        var wiresBySource = src.ToDictionary(s => s.Id, _ => new List<Wire>());
        var outputs = new List<(MixPlan Plan, ProcessingChain Chain, Limiter Limiter)>();
        foreach (var route in MixPlanner.Plan(graph))
        {
            var buses = route.Buses.ToDictionary(id => id, id => new BusInstance(id, route.OutputId, opt.MaxBlockFrames));
            foreach (var b in buses.Values) b.Chain.Set(chain);
            Wire WireFor(WireSpec w)
            {
                var c = src.Single(s => s.Id == w.From).Clock;
                var wire = new Wire(w.From, w.To, c, opt, route.OutputId) { GainDb = w.GainDb };
                wiresBySource[w.From].Add(wire);
                return wire;
            }
            var plan = new MixPlan
            {
                Steps = route.Buses.Select(id => new MixStep
                {
                    Bus = buses[id],
                    Wires = route.SourceWires.Where(w => w.To == id).Select(WireFor).ToArray(),
                    Links = route.BusWires.Where(w => w.To == id).Select(w => new BusLink(buses[w.From], w.To)).ToArray(),
                }).ToArray(),
                Wires = route.SourceWires.Where(w => w.To == route.OutputId).Select(WireFor).ToArray(),
                Links = route.BusWires.Where(w => w.To == route.OutputId).Select(w => new BusLink(buses[w.From], w.To) { GainDb = w.GainDb }).ToArray(),
            };
            var oc = new ProcessingChain();
            oc.Set(chain);
            var lim = new Limiter();
            lim.Init(outRate);
            outputs.Add((plan, oc, lim));
        }
        // «Игра + чат» идёт в два выхода — два экземпляра; провода игры и Discord — по кольцу на каждый выход
        Assert.Equal(2, outputs.Count);
        Assert.Equal(2, wiresBySource["game"].Count);
        Assert.Single(wiresBySource["mic"]);

        var rnd = new Random(1);
        var packets = src.ToDictionary(s => s.Id, s =>
        {
            int n = s.Rate / 100;
            var p = new float[n * 2];
            for (int i = 0; i < p.Length; i++) p[i] = (float)(rnd.NextDouble() - 0.5) * 0.4f;
            return p;
        });
        var work = new float[4096 * 2];
        var mix = new float[block * 2];
        var scratch = new float[block * 2];
        long tick = Stopwatch.Frequency / 100;

        double Run(int blocks, ref long now)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blocks; i++)
            {
                now += tick;
                foreach (var s in src)
                {
                    var pkt = packets[s.Id];
                    int n = pkt.Length / 2;
                    var span = work.AsSpan(0, pkt.Length);
                    pkt.CopyTo(span);
                    s.Chain.Process(span, n, s.Rate);
                    foreach (var w in wiresBySource[s.Id]) w.Write(span, n, now);
                }
                foreach (var (plan, oc, lim) in outputs)
                {
                    Array.Clear(mix);
                    plan.Render(mix, scratch, block, outRate, now + tick / 2);
                    oc.Process(mix, block, outRate);
                    lim.Process(mix, block);
                }
            }
            return sw.Elapsed.TotalSeconds;
        }

        long t = 0;
        Run(300, ref t); // прогрев: кольца заполнены, регуляторы сошлись, JIT
        Assert.All(wiresBySource.Values.SelectMany(w => w), w => Assert.Equal(WireState.Running, w.State));
        double best = double.MaxValue;
        const int blocks = 1000; // 10 с звука
        for (int run = 0; run < 5; run++) best = Math.Min(best, Run(blocks, ref t));
        double core = best / (blocks / 100.0) * 100;
        double quad = core / 4;
        output.WriteLine($"схема стримера, обработка на всех 8 узлах: {core:F2}% одного ядра = {quad:F2}% 4-ядерного процессора (бюджет 5%)");
        Assert.All(wiresBySource.Values.SelectMany(w => w), w => Assert.Equal(0, w.Underruns));

        bool optimized = typeof(ProcessingChain).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .Cast<DebuggableAttribute>().All(d => !d.IsJITOptimizerDisabled);
        // вычисления — малая часть бюджета: остальное — пробуждения потоков, WASAPI и интерфейс (проверяется на устройствах)
        if (optimized) Assert.True(quad < 1.5, $"{quad:F2}% 4-ядерного процессора");
    }
}
