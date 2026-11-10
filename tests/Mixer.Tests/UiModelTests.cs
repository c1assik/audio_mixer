using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Editing;

namespace Mixer.Tests;

/// <summary>Один STA-поток с диспетчером и Application на все тесты моделей интерфейса.</summary>
internal static class WpfThread
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        Dispatcher? d = null;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            d = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "WPF tests" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        return d!;
    });

    public static void Run(Action body)
    {
        ExceptionDispatchInfo? error = null;
        Ui.Value.Invoke(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        error?.Throw();
    }
}

/// <summary>Этап 3: модели интерфейса без окна — карточки, правки, провода, подсветка, палитра, форматирование.</summary>
public class UiModelTests
{
    private static readonly EndpointInfo Headphones = new("{0.0.0.00000000}.{hp}", "Наушники (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo CableIn = new("{0.0.0.00000000}.{ci}", "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo VaioIn = new("{0.0.0.00000000}.{vmi}", "Voicemeeter Input (VB-Audio Voicemeeter VAIO)", EDataFlow.Render, false);
    private static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Микрофон (fifine)", EDataFlow.Capture, true);
    private static readonly List<EndpointInfo> Devices = [Headphones, CableIn, VaioIn, Mic];

    private static GraphConfig Graph() => new()
    {
        Nodes =
        [
            new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, Name = "Микрофон", EndpointId = Mic.Id, EndpointName = Mic.Name, X = 40, Y = 60, Color = "#4FA3FF" },
            new NodeConfig { Id = "lb", Kind = NodeKind.Loopback, Name = "Звук наушников", EndpointId = Headphones.Id, EndpointName = Headphones.Name, X = 40, Y = 210, Color = "#FF9A4D" },
            new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput, Name = "Наушники", EndpointId = Headphones.Id, EndpointName = Headphones.Name, X = GraphLayout.OutputX, Y = 60 },
            new NodeConfig { Id = "stream", Kind = NodeKind.DeviceOutput, Name = "Стрим", EndpointId = CableIn.Id, EndpointName = CableIn.Name, X = GraphLayout.OutputX, Y = 210 },
        ],
        Wires = [new WireConfig { From = "mic", To = "hp" }],
    };

    private static (MainViewModel Vm, GraphStore Store) Make(GraphConfig? g = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "graph.json");
        g ??= Graph();
        var store = new GraphStore(path, g);
        return (new MainViewModel(g, store, host: null, offlineDevices: Devices), store);
    }

    [Fact]
    public void Cards_mirror_the_graph_and_edits_go_through_history()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            Assert.Equal(4, vm.Nodes.Count);
            var w = Assert.Single(vm.Wires);
            Assert.Same(vm.FindNode("mic"), w.Source);

            var mic = vm.FindNode("mic")!;
            for (int i = 1; i <= 10; i++) mic.GainDb = -i * 0.7; // фейдер тянут
            vm.EndMerge();
            Assert.Equal(-7f, GraphEdits.Find(vm.Graph, "mic")!.GainDb);
            Assert.True(store.IsDirty);
            Assert.Equal("−7,0 дБ", mic.GainText);

            vm.UndoCommand.Execute(null); // вся протяжка — один шаг
            Assert.Equal(0, mic.GainDb);
            Assert.False(vm.UndoCommand.CanExecute(null));
            vm.RedoCommand.Execute(null);
            Assert.Equal(-7, mic.GainDb, 3);

            mic.Muted = true;
            Assert.True(GraphEdits.Find(vm.Graph, "mic")!.Muted);
            vm.Dispose();
        });
    }

    [Fact]
    public void Gain_snaps_to_tenths_and_to_zero_near_zero()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            var mic = vm.FindNode("mic")!;
            mic.GainDb = -3.04;
            Assert.Equal(-3f, GraphEdits.Find(vm.Graph, "mic")!.GainDb);
            mic.GainDb = 0.2;
            Assert.Equal(0f, GraphEdits.Find(vm.Graph, "mic")!.GainDb);
            mic.GainDb = 40;
            Assert.Equal(12f, GraphEdits.Find(vm.Graph, "mic")!.GainDb);
            vm.Dispose();
        });
    }

    [Fact]
    public void Forbidden_wire_is_not_created_and_the_reason_is_shown()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            Assert.False(vm.Connect("lb", "hp"));
            Assert.Single(vm.Wires);
            Assert.True(vm.ToastIsError);
            Assert.Contains("Петля", vm.Toast);

            Assert.True(vm.Connect("stream", "lb")); // тянули от выхода к источнику
            var w = vm.FindWire("lb", "stream");
            Assert.NotNull(w);
            Assert.Same(w, vm.Selected);
            Assert.True(w!.IsSelected);
            vm.Dispose();
        });
    }

    [Fact]
    public void Dragging_from_a_port_marks_valid_occupied_and_forbidden_targets()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            var lb = vm.FindNode("lb")!;
            vm.BeginConnectPreview(lb);
            Assert.Equal(PortLook.Origin, lb.Port);
            Assert.Equal(PortLook.Forbidden, vm.FindNode("hp")!.Port);
            Assert.Contains("Петля", vm.FindNode("hp")!.PortReason);
            Assert.Equal(PortLook.Valid, vm.FindNode("stream")!.Port);
            Assert.True(vm.FindNode("mic")!.IsDimmed); // своя колонка гаснет
            vm.EndConnectPreview();
            Assert.All(vm.Nodes, n => Assert.Equal(PortLook.None, n.Port));

            vm.BeginConnectPreview(vm.FindNode("hp")!);
            Assert.Equal(PortLook.Occupied, vm.FindNode("mic")!.Port);
            Assert.Equal(PortLook.Forbidden, vm.FindNode("lb")!.Port);
            vm.EndConnectPreview();
            vm.Dispose();
        });
    }

    [Fact]
    public void Hover_highlights_routes_of_the_node()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            vm.SetHover(vm.FindNode("mic"));
            Assert.False(vm.FindNode("mic")!.IsDimmed);
            Assert.False(vm.FindNode("hp")!.IsDimmed);
            Assert.True(vm.FindNode("lb")!.IsDimmed);
            Assert.True(vm.FindNode("stream")!.IsDimmed);
            Assert.False(vm.Wires[0].IsDimmed);
            vm.SetHover(null);
            Assert.All(vm.Nodes, n => Assert.False(n.IsDimmed));
            vm.Dispose();
        });
    }

    [Fact]
    public void Reroute_delete_and_undo_keep_cards_in_sync()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            var w = vm.FindWire("mic", "hp")!;
            vm.SetWireGain(w, -6);
            vm.EndMerge();
            vm.Reroute(w, "mic", "stream");
            var moved = Assert.Single(vm.Wires);
            Assert.Equal("stream", moved.To);
            Assert.Equal(-6, moved.GainDb);

            vm.DeleteNode(vm.FindNode("stream")!);
            Assert.Empty(vm.Wires);
            Assert.Equal(3, vm.Nodes.Count);
            vm.UndoCommand.Execute(null);
            Assert.Equal(4, vm.Nodes.Count);
            Assert.Equal("stream", Assert.Single(vm.Wires).To);

            var pill = vm.Wires[0];
            vm.ToggleWire(pill);
            Assert.False(pill.Enabled);
            Assert.Equal("выкл", pill.PillText);
            vm.ResetWire(pill); // двойной клик: 0 дБ и включён, один шаг с первым кликом
            Assert.True(pill.Enabled);
            Assert.Equal("0 дБ", pill.PillText);
            vm.UndoCommand.Execute(null);
            Assert.Equal("−6 дБ", vm.Wires[0].PillText);
            vm.Dispose();
        });
    }

    [Fact]
    public void Added_node_lands_in_its_column_with_a_free_color_and_is_selected()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            var n = vm.AddNode(new NodeConfig { Id = "", Kind = NodeKind.App, Name = "Discord", Exe = "discord.exe", TakeOver = true });
            Assert.Equal(GraphLayout.SourceX, n.X);
            Assert.Equal(210 + GraphLayout.CardHeight + GraphLayout.Gap, n.Y);
            Assert.Same(n, vm.Selected);
            Assert.DoesNotContain(n.Config.Color, new[] { "#4FA3FF", "#FF9A4D" });
            Assert.Contains("заглушку", vm.Toast); // заглушки нет — подсказка, где её назначить

            var o = vm.AddNode(new NodeConfig { Id = "", Kind = NodeKind.DeviceOutput, Name = "X", EndpointId = VaioIn.Id, EndpointName = VaioIn.Name }, atY: 600);
            Assert.Equal(GraphLayout.OutputX, o.X);
            Assert.Equal(600 - GraphLayout.PortY, o.Y);

            vm.MoveNode(n, 2000, -50); // в чужую колонку не уходит
            var (min, max) = GraphLayout.ColumnX(source: true);
            Assert.InRange(n.X, min, max);
            vm.Dispose();
        });
    }

    [Fact]
    public void Rename_and_device_change()
    {
        WpfThread.Run(() =>
        {
            var g = Graph();
            g = GraphEdits.UpdateNode(g, "hp", c => c with { Name = "Наушники" }); // имя по умолчанию — короткое имя устройства
            var (vm, store) = Make(g);
            using var _ = store;
            var mic = vm.FindNode("mic")!;
            mic.CommitRename("  Голос  ");
            Assert.Equal("Голос", GraphEdits.Find(vm.Graph, "mic")!.Name);
            mic.CommitRename("   ");
            Assert.Equal("Голос", mic.Name);

            vm.ChangeDevice(vm.FindNode("hp")!, CableIn);
            var hp = GraphEdits.Find(vm.Graph, "hp")!;
            Assert.Equal(CableIn.Id, hp.EndpointId);
            Assert.Equal("CABLE Input", hp.Name);
            Assert.Equal("Голос", GraphEdits.Find(vm.Graph, "mic")!.Name);
            vm.Dispose();
        });
    }

    [Fact]
    public void Palette_lists_sections_filters_and_adds()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make();
            using var _ = store;
            var catalog = new SourceCatalog(Devices,
            [
                new AppEntry("chrome.exe", "Google Chrome", null, 6, true, true),
                new AppEntry("obs64.exe", "OBS Studio", null, 1, false, false),
            ], null, VoicemeeterRunning: false);
            var p = new PaletteViewModel(vm, PaletteMode.Sources, null, Task.FromResult(catalog));
            Assert.False(p.Loading);
            Assert.Equal(new[] { "ПРИЛОЖЕНИЯ СО ЗВУКОМ", "ДРУГИЕ ЗАПУЩЕННЫЕ ПРИЛОЖЕНИЯ", "ВХОДЫ", "ЗВУК ВЫХОДОВ (LOOPBACK)", "ОСОБЫЕ" },
                p.Rows.Where(r => r.IsHeader).Select(r => r.Header));
            Assert.Equal("Google Chrome", p.Rows[p.SelectedIndex].Title);
            Assert.Contains("6 процессов", p.Rows[p.SelectedIndex].Subtitle);
            Assert.False(p.Rows.Single(r => r.Title == "Voicemeeter Input").Enabled); // Voicemeeter не запущен

            p.Query = "fifine"; // «Микрофон (fifine)» — ищется по полному имени устройства
            Assert.Equal(2, p.Rows.Count);
            Assert.Equal("ВХОДЫ", p.Rows[0].Header);
            p.Query = "";
            p.Move(+1);
            Assert.Equal("OBS Studio", p.Rows[p.SelectedIndex].Title); // заголовки пропускаются

            p.Commit();
            var added = vm.Nodes.Last();
            Assert.Equal(NodeKind.App, added.Kind);
            Assert.Equal("obs64.exe", added.Config.Exe);
            Assert.True(added.TakeOver);

            var outs = new PaletteViewModel(vm, PaletteMode.Outputs, null, Task.FromResult(catalog));
            Assert.Equal("ВЫХОДЫ", outs.Rows[0].Header);
            Assert.All(outs.Rows.Skip(1), r => Assert.Equal(NodeKind.DeviceOutput, r.Template!.Kind));
            vm.Dispose();
        });
    }

    [Fact]
    public void Numbers_use_russian_comma_and_true_minus()
    {
        Assert.Equal("0,0 дБ", Fmt.Gain(0));
        Assert.Equal("−3,0 дБ", Fmt.Gain(-3));
        Assert.Equal("+6,0 дБ", Fmt.Gain(6));
        Assert.Equal("−∞ дБ", Fmt.Gain(-60));
        Assert.Equal("0 дБ", Fmt.Pill(0));
        Assert.Equal("−4,5 дБ", Fmt.Pill(-4.5));
        Assert.Equal("0,0 дБ", Fmt.Gain(-0.01));
        Assert.Equal("44,1", Fmt.Khz(44100));
        Assert.Equal("1 источник", Fmt.Plural(1, "источник|источника|источников"));
        Assert.Equal("3 источника", Fmt.Plural(3, "источник|источника|источников"));
        Assert.Equal("11 источников", Fmt.Plural(11, "источник|источника|источников"));
        Assert.Equal("22 источника", Fmt.Plural(22, "источник|источника|источников"));
        Assert.Equal("Динамики", Fmt.ShortDevice("Динамики (fifine Headset)"));
    }

    [Fact]
    public void Meter_rises_instantly_falls_20_db_per_second_and_holds_peak()
    {
        var m = new MeterState();
        m.Push(0.5f, 0.1f, now: 0, dt: 1 / 30.0);
        Assert.Equal(-6.02f, m.L, 1);
        Assert.Equal(-20f, m.R, 1);
        for (int i = 1; i <= 30; i++) m.Push(0, 0, i / 30.0, 1 / 30.0);
        Assert.Equal(-26.02f, m.L, 1); // за секунду −20 дБ
        Assert.Equal(-6.02f, m.HoldL, 1); // пик держится 1,5 с
        for (int i = 31; i <= 60; i++) m.Push(0, 0, i / 30.0, 1 / 30.0);
        Assert.True(m.HoldL < -6.5f);
        m.PushReduction(0.5f, 3);
        Assert.Equal(6.02f, m.ReductionDb, 1);
        m.PushReduction(1f, 3.5);
        Assert.Equal(6.02f, m.ReductionDb, 1); // удержание
        m.PushReduction(1f, 5);
        Assert.Equal(0f, m.ReductionDb);
    }
}
