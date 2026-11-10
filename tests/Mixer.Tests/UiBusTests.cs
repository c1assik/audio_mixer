using Mixer.App.ViewModels;
using Mixer.Audio.Dsp;
using Mixer.Audio.Interop;
using Mixer.Core.Config;
using Mixer.Core.Editing;

namespace Mixer.Tests;

/// <summary>v2, этапы 1–2: модели интерфейса — шины на холсте, группировка, чипы и панель обработки.</summary>
public class UiBusTests
{
    private static readonly EndpointInfo Headphones = new("{0.0.0.00000000}.{hp}", "Наушники (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo CableIn = new("{0.0.0.00000000}.{ci}", "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Микрофон (fifine)", EDataFlow.Capture, true);
    private static readonly List<EndpointInfo> Devices = [Headphones, CableIn, Mic];

    private static GraphConfig Graph() => new()
    {
        Nodes =
        [
            new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, Name = "Микрофон", EndpointId = Mic.Id, EndpointName = Mic.Name, X = 40, Y = 60, Color = "#4FA3FF" },
            new NodeConfig { Id = "lb", Kind = NodeKind.Loopback, Name = "Звук наушников", EndpointId = Headphones.Id, EndpointName = Headphones.Name, X = 40, Y = 210, Color = "#FF9A4D" },
            new NodeConfig { Id = "game", Kind = NodeKind.App, Name = "Игра", Exe = "game.exe", X = 40, Y = 360, Color = "#3DCC91" },
            new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput, Name = "Наушники", EndpointId = Headphones.Id, EndpointName = Headphones.Name, X = GraphLayout.OutputX, Y = 60 },
            new NodeConfig { Id = "stream", Kind = NodeKind.DeviceOutput, Name = "Стрим", EndpointId = CableIn.Id, EndpointName = CableIn.Name, X = GraphLayout.OutputX, Y = 210 },
        ],
        Wires =
        [
            new WireConfig { From = "mic", To = "stream" },
            new WireConfig { From = "game", To = "hp", GainDb = -3 },
            new WireConfig { From = "game", To = "stream", GainDb = -9 },
            new WireConfig { From = "mic", To = "hp", GainDb = -6 },
        ],
    };

    private static (MainViewModel Vm, GraphStore Store) Make(GraphConfig? g = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "graph.json");
        g ??= Graph();
        var store = new GraphStore(path, g);
        return (new MainViewModel(g, store, host: null, offlineDevices: Devices), store);
    }

    [Fact]
    public void New_bus_lands_in_the_middle_column_with_a_numbered_name()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            var b1 = vm.AddBus();
            var b2 = vm.AddBus(400);
            Assert.True(b1.IsBus && b1.HasInPort && b1.HasOutPort);
            Assert.Equal(NodeColumn.Buses, b1.Column);
            Assert.Equal(GraphLayout.BusX, b1.X);
            Assert.Equal("Шина 1", b1.Name);
            Assert.Equal("Шина 2", b2.Name);
            Assert.True(b1.IsSilent); // ни входов, ни выходов
            Assert.Contains("2 шины", vm.CountsText);

            vm.Connect("game", b1.Id);
            Assert.True(b1.IsSilent); // входы есть, выходов нет
            vm.Connect(b1.Id, "hp");
            Assert.False(b1.IsSilent);
            Assert.Contains(vm.Wires, w => w.Source == b1 && w.Target.Id == "hp");
            // провод из шины начинается у её правого порта
            var wire = vm.FindWire(b1.Id, "hp")!;
            Assert.Equal(b1.OutPoint, wire.A);
            vm.Dispose();
        });
    }

    [Fact]
    public void Ctrl_picked_sources_group_into_a_bus_in_one_undo_step()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            Assert.False(vm.GroupSelectedCommand.CanExecute(null));
            vm.Select(vm.FindNode("mic"));
            vm.TogglePick(vm.FindNode("game")!);
            Assert.Equal(2, vm.Picked.Count);
            Assert.True(vm.GroupSelectedCommand.CanExecute(null));

            int nodes = vm.Nodes.Count;
            vm.GroupSelectedCommand.Execute(null);
            var bus = Assert.Single(vm.Nodes, n => n.IsBus);
            Assert.Equal(nodes + 1, vm.Nodes.Count);
            // общие выходы mic и game — hp и stream. Разница уровней hp − stream у mic −6, у game +6: одной шиной не
            // разложить, поэтому на шину переезжают только наушники, стрим остаётся на прямых проводах
            Assert.Contains("Создана", vm.Toast);
            Assert.Contains(vm.Wires, w => w.Source == bus && w.Target.Id == "hp");
            Assert.Contains(vm.Wires, w => w.Source.Id == "game" && w.Target.Id == "stream");
            Assert.DoesNotContain(vm.Wires, w => w.Source.Id == "game" && w.Target.Id == "hp");
            Assert.Equal(bus, vm.Selected);

            vm.UndoCommand.Execute(null);
            Assert.DoesNotContain(vm.Nodes, n => n.IsBus);
            Assert.Equal(4, vm.Graph.Wires.Count);
            vm.Dispose();
        });
    }

    [Fact]
    public void Ungroup_from_the_menu_puts_wires_back()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            vm.Select(vm.FindNode("mic"));
            vm.TogglePick(vm.FindNode("lb")!);
            vm.GroupSelectedCommand.Execute(null); // общих выходов нет: шина только с входами
            var bus = Assert.Single(vm.Nodes, n => n.IsBus);
            Assert.Contains("никуда не ведёт", vm.Toast);
            vm.Ungroup(bus);
            Assert.DoesNotContain(vm.Nodes, n => n.IsBus);
            Assert.Equal(Graph().Wires.Count, vm.Graph.Wires.Count);
            vm.Dispose();
        });
    }

    [Fact]
    public void Dragging_from_a_bus_output_port_targets_buses_and_outputs()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            var bus = vm.AddBus();
            var other = vm.AddBus();
            vm.Connect("lb", bus.Id);
            vm.BeginConnectPreview(bus, fromInput: false);
            Assert.Equal(PortLook.Origin, bus.OutPort);
            Assert.True(vm.FindNode("mic")!.IsDimmed);                     // источники не цель
            Assert.Equal(PortLook.Valid, other.InPort);
            Assert.Equal(PortLook.Valid, vm.FindNode("stream")!.InPort);
            Assert.Equal(PortLook.Forbidden, vm.FindNode("hp")!.InPort);    // петля: loopback наушников через шину
            Assert.Contains("Путь петли", vm.FindNode("hp")!.PortReason);
            vm.EndConnectPreview();

            vm.BeginConnectPreview(bus, fromInput: true);                   // от входа шины — к источникам и шинам
            Assert.Equal(PortLook.Occupied, vm.FindNode("lb")!.OutPort);
            Assert.Equal(PortLook.Valid, vm.FindNode("mic")!.OutPort);
            Assert.Equal(PortLook.Valid, other.OutPort);
            Assert.True(vm.FindNode("hp")!.IsDimmed);
            vm.EndConnectPreview();
            vm.Dispose();
        });
    }

    [Fact]
    public void Chips_follow_the_chain_and_the_panel_edits_it_in_one_step_per_drag()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            var mic = vm.FindNode("mic")!;
            Assert.True(mic.ShowFx);

            vm.OpenProcessing(mic, "gate");
            var p = vm.Processing!;
            Assert.Equal(mic, p.Node);
            Assert.Equal("gate", p.FocusBlock);
            Assert.False(p.GateOn);

            p.GateOn = true;
            Assert.True(mic.HasGate);
            Assert.False(mic.ShowFx);
            Assert.True(vm.Graph.Nodes.Single(n => n.Id == "mic").Chain!.Gate.Enabled);

            // протяжка ручки порога — один шаг истории
            var threshold = p.GateParams[0];
            int depth = UndoDepth(vm);
            for (double v = -50; v <= -30; v += 2) threshold.Value = v;
            p.EndEdit();
            Assert.Equal(depth + 1, UndoDepth(vm));
            Assert.Equal(-30, vm.Graph.Nodes.Single(n => n.Id == "mic").Chain!.Gate.ThresholdDb);

            vm.UndoCommand.Execute(null);
            Assert.Equal(-50, p.GateParams[0].Value); // панель перечитала граф
            vm.Dispose();
        });
    }

    private static int UndoDepth(MainViewModel vm)
    {
        int n = 0;
        // глубину видно только через Undo; считаем по графу: снимаем шаги и возвращаем
        var g = vm.Graph;
        while (vm.UndoCommand.CanExecute(null))
        {
            vm.UndoCommand.Execute(null);
            n++;
        }
        for (int i = 0; i < n; i++) vm.RedoCommand.Execute(null);
        Assert.Same(g, vm.Graph);
        return n;
    }

    [Fact]
    public void Presets_apply_and_are_recognised_and_band_types_change()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            var mic = vm.FindNode("mic")!;
            vm.OpenProcessing(mic);
            var p = vm.Processing!;
            Assert.Equal("Без обработки", p.PresetName);
            var broadcast = p.Presets.Single(x => x.Id == ChainPresets.VoiceBroadcast);
            p.ApplyPreset(broadcast);
            Assert.Equal(broadcast.Name, p.PresetName);
            Assert.True(mic.HasGate && mic.HasEq && mic.HasComp);
            Assert.Equal(EqBandType.HighShelf, p.Bands[2].Type);
            Assert.Equal(-22, p.CompParams[0].Value);

            p.Bands[3].SetType(EqBandType.Peak);
            Assert.Equal(EqBandType.Peak, vm.Graph.Nodes.Single(n => n.Id == "mic").Chain!.Eq.Bands[3].Type);
            Assert.Equal("Свои настройки", p.PresetName);

            p.DragBand(0, 300, -4.5);
            var b0 = vm.Graph.Nodes.Single(n => n.Id == "mic").Chain!.Eq.Bands[0];
            Assert.Equal((300f, -4.5f), (b0.FreqHz, b0.GainDb));
            vm.Dispose();
        });
    }

    [Fact]
    public void Deleting_the_node_closes_its_panel_and_buses_offer_processing_too()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            var bus = vm.AddBus();
            vm.OpenProcessing(bus, "comp");
            vm.Processing!.CompOn = true;
            Assert.True(bus.HasComp);
            Assert.True(vm.Graph.Nodes.Single(n => n.Id == bus.Id).HasChain);
            vm.DeleteNode(bus);
            Assert.Null(vm.Processing);
            vm.Dispose();
        });
    }

    [Fact]
    public void Parameter_sliders_map_log_ranges_and_snap()
    {
        double committed = double.NaN;
        var f = new ParamViewModel("f", "f", 20, 20000, 1000, v => v.ToString(), v => committed = v, log: true, step: 1);
        Assert.Equal(Math.Log(50) / Math.Log(1000), f.Position, 6); // шкала по логарифму
        f.Position = 0;
        Assert.Equal(20, f.Value);
        f.Position = 1;
        Assert.Equal(20000, f.Value);
        Assert.Equal(20000, committed);
        f.Position = 0.6;
        Assert.Equal(0, f.Value % 10); // на килогерцах шаг 10 Гц
        var g = new ParamViewModel("g", "g", -18, 18, 0, v => v.ToString(), _ => { }, step: 0.1, wheelStep: 0.5);
        g.Nudge(3);
        Assert.Equal(1.5, g.Value, 6);
        g.Reset();
        Assert.Equal(0, g.Value);
    }
}
