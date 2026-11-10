using System.Diagnostics;
using Mixer.Audio.Dsp;
using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;
using Xunit.Abstractions;

namespace Mixer.Tests;

/// <summary>v2, этап 1: шины — раскладка по выходам, сведение в периоде выхода, проверки цикла, глубины и петель, группировка.</summary>
public class BusTests(ITestOutputHelper output)
{
    private static readonly EndpointInfo Headphones = new("{0.0.0.00000000}.{hp}", "Headphones (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo CableIn = new("{0.0.0.00000000}.{ci}", "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo CableOut = new("{0.0.1.00000000}.{co}", "CABLE Output (VB-Audio Virtual Cable)", EDataFlow.Capture, false);
    private static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Shure MV7", EDataFlow.Capture, true);
    private static readonly List<EndpointInfo> Devices = [Headphones, CableIn, CableOut, Mic];

    private static NodeConfig Node(string id, NodeKind kind, EndpointInfo? ep = null, double y = 60, string? exe = null) => new()
    {
        Id = id, Kind = kind, Name = id, EndpointId = ep?.Id, EndpointName = ep?.Name, Exe = exe,
        X = GraphLayout.ColumnLeft(kind == NodeKind.Bus ? NodeColumn.Buses : kind == NodeKind.DeviceOutput ? NodeColumn.Outputs : NodeColumn.Sources), Y = y,
    };

    private static WireConfig W(string from, string to, float gain = 0, bool enabled = true) => new() { From = from, To = to, GainDb = gain, Enabled = enabled };

    /// <summary>Схема стримера из макета v2: 4 источника, 2 шины, 2 выхода.</summary>
    private static GraphConfig Streamer() => new()
    {
        Nodes =
        [
            Node("mic", NodeKind.DeviceInput, Mic),
            Node("lb", NodeKind.Loopback, Headphones, 210),
            Node("cable", NodeKind.DeviceInput, CableOut, 360),
            Node("game", NodeKind.App, null, 510, "game.exe"),
            Node("gc", NodeKind.Bus, null, 60),
            Node("sm", NodeKind.Bus, null, 360),
            Node("hp", NodeKind.DeviceOutput, Headphones),
            Node("stream", NodeKind.DeviceOutput, CableIn, 360),
        ],
        Wires = [W("game", "gc", -3), W("mic", "sm"), W("gc", "hp"), W("gc", "sm", -6), W("sm", "stream", -1)],
    };

    // ── раскладка по выходам ──

    [Fact]
    public void Planner_gives_each_output_its_own_bus_instances_in_topological_order()
    {
        var g = new EngineGraph(
            [new SourceSpec("a", SourceKind.DeviceInput, "x"), new SourceSpec("b", SourceKind.DeviceInput, "y")],
            [new OutputSpec("o1", "e1"), new OutputSpec("o2", "e2")],
            [new WireSpec("a", "b1"), new WireSpec("b1", "b2"), new WireSpec("b", "b2"), new WireSpec("b2", "o1"), new WireSpec("b1", "o2"), new WireSpec("a", "o2")])
        {
            Buses = [new BusSpec("b2"), new BusSpec("b1"), new BusSpec("dead")],
        };
        var plan = MixPlanner.Plan(g).ToDictionary(r => r.OutputId);

        // b2 зависит от b1: b1 считается раньше, хотя в списке шин он второй
        Assert.Equal(["b1", "b2"], plan["o1"].Buses);
        Assert.Equal(["a>b1", "b>b2"], plan["o1"].SourceWires.Select(w => $"{w.From}>{w.To}").Order());
        Assert.Equal(["b1>b2", "b2>o1"], plan["o1"].BusWires.Select(w => $"{w.From}>{w.To}").Order());
        // в o2 b2 не ведёт: его экземпляра там нет, провод b → b2 там не нужен
        Assert.Equal(["b1"], plan["o2"].Buses);
        Assert.Equal(["a>b1", "a>o2"], plan["o2"].SourceWires.Select(w => $"{w.From}>{w.To}").Order());
        Assert.Equal(["b1>o2"], plan["o2"].BusWires.Select(w => $"{w.From}>{w.To}").Order());
        // шина, которая никуда не ведёт, не считается нигде
        Assert.DoesNotContain(plan.Values, r => r.Buses.Contains("dead"));
    }

    [Fact]
    public void Planner_skips_buses_caught_in_a_cycle()
    {
        var g = new EngineGraph([new SourceSpec("a", SourceKind.DeviceInput, "x")], [new OutputSpec("o", "e")],
            [new WireSpec("a", "b1"), new WireSpec("b1", "b2"), new WireSpec("b2", "b1"), new WireSpec("b2", "o"), new WireSpec("a", "o")])
        {
            Buses = [new BusSpec("b1"), new BusSpec("b2")],
        };
        var r = Assert.Single(MixPlanner.Plan(g));
        Assert.Empty(r.Buses);
        Assert.Equal(["a>o"], r.SourceWires.Select(w => $"{w.From}>{w.To}"));
        Assert.Empty(r.BusWires);
    }

    // ── сведение в периоде выхода ──

    /// <summary>Источник с постоянным уровнем: пишет в свои провода по 10 мс, как поток захвата.</summary>
    private sealed class FakeSource(float level)
    {
        public readonly SourceClock Clock = new();
        public readonly List<Wire> Wires = [];

        public Wire WireTo(string from, string to, string output, EngineOptions o)
        {
            var w = new Wire(from, to, Clock, o, output);
            Wires.Add(w);
            return w;
        }

        public void Write(int frames, long now)
        {
            var buf = new float[frames * 2];
            Array.Fill(buf, level);
            foreach (var w in Wires) w.Write(buf, frames, now);
        }
    }

    /// <summary>Гоняет план выхода 2 с по 10 мс и возвращает последний блок.</summary>
    private static float[] Run(MixPlan plan, params FakeSource[] sources)
    {
        const int rate = 48000, block = 480;
        foreach (var s in sources) s.Clock.Started(rate);
        var mix = new float[block * 2];
        var scratch = new float[block * 2];
        long tick = Stopwatch.Frequency / 100;
        long now = 0;
        for (int i = 0; i < 200; i++)
        {
            now += tick;
            foreach (var s in sources) s.Write(block, now);
            Array.Clear(mix);
            plan.Render(mix, scratch, block, rate, now + tick / 2);
        }
        return mix;
    }

    [Fact]
    public void Bus_sums_its_inputs_and_applies_its_fader_and_wire_gains()
    {
        var o = new EngineOptions { UseMmcss = false };
        var a = new FakeSource(0.2f);
        var c = new FakeSource(0.1f);
        var bus = new BusInstance("b", "out", o.MaxBlockFrames);
        bus.SetGain(-6.0206f, 0, false);                     // ×0,5
        var link = new BusLink(bus, "out") { GainDb = 6.0206f }; // ×2
        var wa = a.WireTo("a", "b", "out", o);
        var wc = c.WireTo("c", "b", "out", o);
        wc.GainDb = -6.0206f;                                // ×0,5
        var plan = new MixPlan
        {
            Steps = [new MixStep { Bus = bus, Wires = [wa, wc], Links = [] }],
            Wires = [],
            Links = [link],
        };
        var mix = Run(plan, a, c);
        // (0,2 + 0,1·0,5) · 0,5 · 2 = 0,25
        output.WriteLine($"выход: {mix[^2]:F4}");
        Assert.All(mix, x => Assert.InRange(x, 0.248f, 0.252f));
        Assert.True(bus.Peak.Take().L > 0.12f);
        Assert.True(link.Peak.Take().L > 0.24f);
    }

    [Fact]
    public void Bus_feeding_another_bus_is_computed_first_and_direct_wires_add_up()
    {
        var o = new EngineOptions { UseMmcss = false };
        var a = new FakeSource(0.1f);
        var d = new FakeSource(0.05f);
        var b1 = new BusInstance("b1", "out", o.MaxBlockFrames);
        var b2 = new BusInstance("b2", "out", o.MaxBlockFrames);
        var plan = new MixPlan
        {
            Steps =
            [
                new MixStep { Bus = b1, Wires = [a.WireTo("a", "b1", "out", o)], Links = [] },
                new MixStep { Bus = b2, Wires = [], Links = [new BusLink(b1, "b2") { GainDb = 6.0206f }] },
            ],
            Wires = [d.WireTo("d", "out", "out", o)],
            Links = [new BusLink(b2, "out")],
        };
        var mix = Run(plan, a, d);
        // a·2 через две шины + d напрямую = 0,25
        Assert.All(mix, x => Assert.InRange(x, 0.248f, 0.252f));
    }

    [Fact]
    public void Muted_bus_and_disabled_link_go_silent_without_a_click()
    {
        var o = new EngineOptions { UseMmcss = false };
        var a = new FakeSource(0.3f);
        var bus = new BusInstance("b", "out", o.MaxBlockFrames);
        bus.SetGain(0, 0, muted: true);
        var plan = new MixPlan { Steps = [new MixStep { Bus = bus, Wires = [a.WireTo("a", "b", "out", o)], Links = [] }], Wires = [], Links = [new BusLink(bus, "out")] };
        Assert.All(Run(plan, a), x => Assert.Equal(0f, x, 6));

        var a2 = new FakeSource(0.3f);
        var bus2 = new BusInstance("b", "out", o.MaxBlockFrames);
        var plan2 = new MixPlan { Steps = [new MixStep { Bus = bus2, Wires = [a2.WireTo("a", "b", "out", o)], Links = [] }], Wires = [], Links = [new BusLink(bus2, "out") { Enabled = false }] };
        Assert.All(Run(plan2, a2), x => Assert.Equal(0f, x, 6));
    }

    // ── проверки проводов ──

    [Fact]
    public void Wires_through_buses_are_allowed_in_both_drag_directions()
    {
        var g = Streamer();
        Assert.True(ConnectionRules.Check(g, "mic", "gc", Devices).Ok);
        // тянут от входного порта шины к источнику: направление нормализуется
        var back = ConnectionRules.Check(g, "gc", "mic", Devices);
        Assert.True(back.Ok);
        Assert.Equal(("mic", "gc"), (back.From, back.To));
        // от выхода к шине — тоже
        var o = ConnectionRules.Check(g, "stream", "gc", Devices);
        Assert.True(o.Ok);
        Assert.Equal(("gc", "stream"), (o.From, o.To));
        Assert.Equal(ConnectVerdict.Exists, ConnectionRules.Check(g, "stream", "sm", Devices).Verdict);
    }

    [Fact]
    public void Bus_cycle_is_forbidden_and_the_reason_shows_the_loop_path()
    {
        var g = Streamer();
        var c = ConnectionRules.Check(g, "sm", "gc", Devices);
        Assert.Equal(ConnectVerdict.Cycle, c.Verdict);
        output.WriteLine(c.Reason);
        Assert.Contains("«sm» → «gc» → «sm»", c.Reason);
        Assert.Equal(ConnectVerdict.SameSide, ConnectionRules.Check(g, "gc", "gc", Devices).Verdict);
    }

    [Fact]
    public void Three_buses_in_a_row_are_forbidden()
    {
        var g = GraphEdits.AddNode(Streamer(), Node("b3", NodeKind.Bus, null, 600));
        // gc → sm уже два уровня; sm → b3 сделал бы три
        var c = ConnectionRules.Check(g, "sm", "b3", Devices);
        Assert.Equal(ConnectVerdict.TooDeep, c.Verdict);
        Assert.Contains("2", c.Reason);
        // b3 → gc тоже: b3 → gc → sm
        Assert.Equal(ConnectVerdict.TooDeep, ConnectionRules.Check(g, "b3", "gc", Devices).Verdict);
        // а b3 → hp можно: путь из одной шины
        Assert.True(ConnectionRules.Check(g, "b3", "hp", Devices).Ok);
    }

    [Fact]
    public void Loopback_into_a_bus_that_reaches_its_own_output_is_a_loop_with_the_path()
    {
        var g = Streamer();
        // gc ведёт в наушники: звук наушников в gc вернётся в наушники
        var c = ConnectionRules.Check(g, "lb", "gc", Devices);
        Assert.Equal(ConnectVerdict.Feedback, c.Verdict);
        output.WriteLine(c.Reason);
        Assert.Contains("«lb» → «gc» → «hp»", c.Reason);
        // в sm можно: sm идёт только в стрим
        Assert.True(ConnectionRules.Check(g, "lb", "sm", Devices).Ok);

        // и наоборот: шина, в которую уже идёт loopback, не может вести в его выход
        var g2 = GraphEdits.Connect(g, "lb", "sm");
        var back = ConnectionRules.Check(g2, "sm", "hp", Devices);
        Assert.Equal(ConnectVerdict.Feedback, back.Verdict);
        Assert.Contains("«lb» → «sm» → «hp»", back.Reason);

        // CABLE Output через шину в CABLE Input — петля через кабель
        Assert.Equal(ConnectVerdict.Feedback, ConnectionRules.Check(g, "cable", "sm", Devices).Verdict);
    }

    // ── сборка снимка движка ──

    [Fact]
    public void Builder_passes_buses_and_their_wires_to_the_engine()
    {
        var g = Streamer();
        var r = GraphBuilder.Build(g, Devices, apps: new RuntimeContext { Roots = exe => exe == "game.exe" ? [42u] : [] });
        Assert.Equal(["gc", "sm"], r.Graph.Buses.Select(b => b.Id).Order());
        var wires = r.Graph.Wires.Select(w => $"{w.From}>{w.To}").ToHashSet();
        Assert.Contains("game@42>gc", wires);
        Assert.Contains("mic>sm", wires);
        Assert.Contains("gc>hp", wires);
        Assert.Contains("gc>sm", wires);
        Assert.Contains("sm>stream", wires);
        Assert.Equal("gc", r.EngineBuses["gc"]);
        Assert.DoesNotContain(r.Issues, i => i.Kind is IssueKind.Invalid or IssueKind.Feedback);
    }

    [Fact]
    public void Capture_rate_follows_outputs_reached_through_buses()
    {
        var g = Streamer();
        var rates = new Dictionary<string, int?> { [Headphones.Id] = 44100, [CableIn.Id] = 44100 };
        var r = GraphBuilder.Build(g, Devices, apps: new RuntimeContext { Roots = _ => [42u], OutputRate = id => rates.GetValueOrDefault(id) });
        Assert.Equal(44100, r.Graph.Sources.Single(s => s.Id == "game@42").SampleRate);
    }

    [Fact]
    public void Loop_through_a_bus_after_a_device_change_drops_the_source_wire_only()
    {
        // в файле: loopback наушников → gc, а gc → наушники (схему собрали, пока loopback был другого устройства)
        var g = GraphEdits.Connect(Streamer(), "lb", "gc");
        var r = GraphBuilder.Build(g, Devices);
        var issue = Assert.Single(r.Issues, i => i.Kind == IssueKind.Feedback);
        Assert.Equal("lb → gc", issue.Subject);
        var wires = r.Graph.Wires.Select(w => $"{w.From}>{w.To}").ToHashSet();
        Assert.DoesNotContain("lb>gc", wires);
        Assert.Contains("gc>hp", wires); // остальной звук шины идёт как шёл
        Assert.NotNull(ConnectionRules.WhyWireForbidden(g, g.Wires.Single(w => w.From == "lb"), Devices));
        Assert.Null(ConnectionRules.WhyWireForbidden(g, g.Wires.Single(w => w.From == "gc" && w.To == "hp"), Devices));
    }

    [Fact]
    public void Cycle_from_a_hand_edited_file_is_dropped_with_an_issue()
    {
        var g = Streamer() with { Wires = [.. Streamer().Wires, W("sm", "gc")] };
        var r = GraphBuilder.Build(g, Devices);
        Assert.Contains(r.Issues, i => i.Kind == IssueKind.Invalid && i.Subject == "sm → gc");
        Assert.DoesNotContain(r.Graph.Wires, w => w.From == "sm" && w.To == "gc");
    }

    [Fact]
    public void App_with_processing_mixes_its_captures_into_one_bus_with_its_fader()
    {
        var chain = new ChainSettings { Comp = new CompSettings { Enabled = true } };
        var g = new GraphConfig
        {
            Nodes = [Node("app", NodeKind.App, null, 60, "chrome.exe") with { GainDb = -4, Chain = chain }, Node("hp", NodeKind.DeviceOutput, Headphones)],
            Wires = [W("app", "hp", -2)],
        };
        var r = GraphBuilder.Build(g, Devices, apps: new RuntimeContext { Roots = _ => [1u, 2u] });
        var bus = Assert.Single(r.Graph.Buses);
        Assert.Equal("app#fx", bus.Id);
        Assert.Equal(-4, bus.GainDb);
        Assert.Equal(chain, bus.Chain);
        Assert.All(r.Graph.Sources, s => Assert.Equal(0, s.GainDb)); // фейдер — на шине
        var wires = r.Graph.Wires.Select(w => $"{w.From}>{w.To}:{w.GainDb}").ToHashSet();
        Assert.Contains("app@1>app#fx:0", wires);
        Assert.Contains("app@2>app#fx:0", wires);
        Assert.Contains("app#fx>hp:-2", wires);
        Assert.Equal("app#fx", r.EngineBuses["app"]);
        Assert.Equal("app", GraphBuilder.NodeIdOf("app#fx"));

        // без обработки — как в v1: захваты прямо в выход со своим фейдером
        var plain = GraphBuilder.Build(g with { Nodes = [g.Nodes[0] with { Chain = null }, g.Nodes[1]] }, Devices, apps: new RuntimeContext { Roots = _ => [1u, 2u] });
        Assert.Empty(plain.Graph.Buses);
        Assert.All(plain.Graph.Sources, s => Assert.Equal(-4, s.GainDb));
    }

    [Fact]
    public void Device_nodes_carry_their_chain_only_when_a_block_is_on()
    {
        var on = new ChainSettings { Gate = new GateSettings { Enabled = true } };
        var off = new ChainSettings();
        var g = new GraphConfig
        {
            Nodes = [Node("mic", NodeKind.DeviceInput, Mic) with { Chain = on }, Node("hp", NodeKind.DeviceOutput, Headphones) with { Chain = off }],
            Wires = [W("mic", "hp")],
        };
        var r = GraphBuilder.Build(g, Devices);
        Assert.Equal(on, r.Graph.Sources.Single().Chain);
        Assert.Null(r.Graph.Outputs.Single().Chain);
    }

    // ── сгруппировать и разгруппировать ──

    /// <summary>Итоговое усиление каждого пути источник → выход, дБ (сумма по путям в линейной мере).</summary>
    private static Dictionary<(string, string), double> Levels(GraphConfig g)
    {
        var result = new Dictionary<(string, string), double>();
        var nodes = g.Nodes.ToDictionary(n => n.Id);
        void Walk(string src, string at, double db)
        {
            foreach (var w in g.Wires.Where(w => w.From == at && w.Enabled))
            {
                var to = nodes[w.To];
                double d = db + w.GainDb + (to.IsBus ? to.GainDb : 0);
                if (to.IsBus && to.Muted) continue;
                if (to.IsOutput)
                {
                    double lin = result.TryGetValue((src, to.Id), out var prev) ? Math.Pow(10, prev / 20) : 0;
                    result[(src, to.Id)] = 20 * Math.Log10(lin + Math.Pow(10, d / 20));
                }
                else
                {
                    Walk(src, to.Id, d);
                }
            }
        }
        foreach (var s in g.Nodes.Where(n => n.IsSource)) Walk(s.Id, s.Id, 0);
        return result;
    }

    private static GraphConfig Flat() => new()
    {
        Nodes =
        [
            Node("mic", NodeKind.DeviceInput, Mic), Node("game", NodeKind.App, null, 210, "game.exe"), Node("chat", NodeKind.App, null, 360, "discord.exe"),
            Node("hp", NodeKind.DeviceOutput, Headphones), Node("stream", NodeKind.DeviceOutput, CableIn, 210), Node("rec", NodeKind.DeviceOutput, CableIn, 360),
        ],
        Wires =
        [
            W("game", "hp", -3), W("game", "stream", -9), W("game", "rec", -3),
            W("chat", "hp", 0), W("chat", "stream", -6), W("chat", "rec", -12),
            W("mic", "stream"),
        ],
    };

    [Fact]
    public void Grouping_moves_common_outputs_onto_the_bus_and_keeps_every_level()
    {
        var g = Flat();
        var before = Levels(g);
        var bus = Node("bus", NodeKind.Bus, null, 200);
        var r = GraphEdits.GroupIntoBus(g, ["game", "chat"], bus);
        Assert.Null(r.Error);
        // hp и stream раскладываются (game − chat = −3 в обоих), rec — нет (+9): остаётся на прямых проводах
        Assert.Equal(2, r.Moved);
        Assert.Equal(1, r.Skipped);
        var after = Levels(r.Graph);
        foreach (var (k, v) in before) Assert.Equal(v, after[k], 2);
        Assert.Equal(before.Count, after.Count);
        Assert.DoesNotContain(r.Graph.Wires, w => w.From is "game" or "chat" && w.To is "hp" or "stream");
        Assert.Contains(r.Graph.Wires, w => w.From == "game" && w.To == "rec");
        // провода из шины не громче 0 дБ
        Assert.All(r.Graph.Wires.Where(w => w.From == "bus"), w => Assert.True(w.GainDb <= 0));
        output.WriteLine(string.Join(", ", r.Graph.Wires.Select(w => $"{w.From}>{w.To} {w.GainDb}")));
    }

    [Fact]
    public void Grouping_needs_two_nodes_and_works_without_common_outputs()
    {
        var g = Flat();
        Assert.NotNull(GraphEdits.GroupIntoBus(g, ["game"], Node("bus", NodeKind.Bus)).Error);
        Assert.NotNull(GraphEdits.GroupIntoBus(g, ["game", "hp"], Node("bus", NodeKind.Bus)).Error);
        var r = GraphEdits.GroupIntoBus(g, ["mic", "game"], Node("bus", NodeKind.Bus)); // общий выход — только stream
        Assert.Equal(1, r.Moved);
        foreach (var (k, v) in Levels(g)) Assert.Equal(v, Levels(r.Graph)[k], 2);
    }

    [Fact]
    public void Ungrouping_restores_direct_wires_with_the_same_levels()
    {
        var g = Flat();
        var grouped = GraphEdits.GroupIntoBus(g, ["game", "chat"], Node("bus", NodeKind.Bus, null, 200)).Graph;
        grouped = GraphEdits.UpdateNode(grouped, "bus", n => n with { GainDb = -2 });
        var levels = Levels(grouped);
        var r = GraphEdits.Ungroup(grouped, "bus");
        Assert.Null(r.Error);
        Assert.DoesNotContain(r.Graph.Nodes, n => n.IsBus);
        foreach (var (k, v) in levels) Assert.Equal(v, Levels(r.Graph)[k], 2);

        // заглушённая шина даёт выключенные провода
        var muted = GraphEdits.Ungroup(GraphEdits.UpdateNode(grouped, "bus", n => n with { Muted = true }), "bus").Graph;
        Assert.All(muted.Wires.Where(w => w.From is "game" or "chat" && w.To is "hp" or "stream"), w => Assert.False(w.Enabled));
    }

    [Fact]
    public void Grouping_refuses_to_build_three_buses_in_a_row()
    {
        var g = Streamer();
        g = GraphEdits.AddNode(g, Node("b0", NodeKind.Bus, null, 600));
        g = GraphEdits.Connect(g, "b0", "gc");
        // gc уже ведёт в sm: b0 → gc → sm — две шины ниже b0; группа над b0 сделала бы три
        g = GraphEdits.AddNode(g, Node("x", NodeKind.DeviceInput, Mic, 700));
        g = GraphEdits.Connect(g, "x", "b0");
        g = GraphEdits.Connect(g, "mic", "b0");
        var r = GraphEdits.GroupIntoBus(g, ["x", "mic"], Node("nb", NodeKind.Bus));
        Assert.NotNull(r.Error);
        Assert.Same(g, r.Graph);
    }

    // ── разное ──

    [Fact]
    public void Buses_live_in_the_middle_column()
    {
        var (x, _) = GraphLayout.Clamp(NodeColumn.Buses, 0, 100);
        Assert.Equal(GraphLayout.ColumnX(NodeColumn.Buses).Min, x);
        (x, _) = GraphLayout.Clamp(NodeColumn.Buses, 2000, 100);
        Assert.Equal(GraphLayout.ColumnX(NodeColumn.Buses).Max, x);
        Assert.Equal(NodeColumn.Buses, GraphLayout.ColumnAt(GraphLayout.BusX + GraphLayout.CardWidth / 2));
        Assert.Equal(NodeColumn.Sources, GraphLayout.ColumnAt(GraphLayout.SourceX));
        Assert.Equal(NodeColumn.Outputs, GraphLayout.ColumnAt(GraphLayout.OutputX + 10));
        var (bx, by) = GraphLayout.NextSlot(Streamer(), NodeColumn.Buses);
        Assert.Equal(GraphLayout.BusX, bx);
        Assert.Equal(360 + GraphLayout.CardHeight + GraphLayout.Gap, by);
        // колонки не перекрываются
        Assert.True(GraphLayout.ColumnX(NodeColumn.Sources).Max + GraphLayout.CardWidth < GraphLayout.ColumnX(NodeColumn.Buses).Min);
        Assert.True(GraphLayout.ColumnX(NodeColumn.Buses).Max + GraphLayout.CardWidth < GraphLayout.ColumnX(NodeColumn.Outputs).Min);
    }

    [Fact]
    public void Bus_and_chain_survive_json_and_v1_files_are_kept_aside()
    {
        var g = Streamer();
        g = GraphEdits.UpdateNode(g, "mic", n => n with
        {
            Chain = new ChainSettings
            {
                Eq = new EqSettings { Enabled = true, Bands = [new EqBand { Type = EqBandType.HighShelf, FreqHz = 8000, GainDb = 3, Q = 0.7f }] },
            }.Normalized(),
        });
        string json = GraphSerializer.Serialize(g);
        Assert.Contains("\"kind\": \"bus\"", json);
        Assert.Contains("\"highShelf\"", json);
        var back = GraphSerializer.Deserialize(json);
        Assert.Equal(g.Nodes, back.Nodes);
        Assert.Equal(GraphConfig.CurrentVersion, back.Version);
        Assert.Equal(GraphChangeKind.None, GraphChange.Classify(g, back));

        // значения из файла, поправленного руками, приводятся к допустимым
        var wild = GraphSerializer.Deserialize("""{ "nodes": [ { "id": "m", "kind": "deviceInput", "chain": { "comp": { "enabled": true, "ratio": 500, "makeupDb": -5 } } } ] }""");
        Assert.Equal(20, wild.Nodes[0].Chain!.Comp.Ratio);
        Assert.Equal(0, wild.Nodes[0].Chain!.Comp.MakeupDb);
        Assert.Equal(EqSettings.BandCount, wild.Nodes[0].Chain!.Eq.Bands.Count);

        string dir = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "graph.json");
        File.WriteAllText(path, """{ "version": 1, "nodes": [ { "id": "n1", "kind": "deviceInput", "name": "Mic" } ], "wires": [] }""");
        var loaded = GraphStore.LoadOrEmpty(path, out string? error);
        Assert.Null(error);
        Assert.Equal(GraphConfig.CurrentVersion, loaded.Version);
        Assert.True(File.Exists(Path.Combine(dir, "graph.v1.json")));
        Assert.Contains("\"version\": 1", File.ReadAllText(Path.Combine(dir, "graph.v1.json")));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Chain_edits_are_parameter_changes_not_structure()
    {
        var g = Streamer();
        var next = GraphEdits.UpdateNode(g, "mic", n => n with { Chain = new ChainSettings { Gate = new GateSettings { Enabled = true } } });
        Assert.Equal(GraphChangeKind.Parameters, GraphChange.Classify(g, next));
        var bus = GraphEdits.AddNode(g, Node("b9", NodeKind.Bus));
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, bus));
    }
}
