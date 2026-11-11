using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;
using Mixer.Core.Sys;

namespace Mixer.Tests;

/// <summary>Этап 3: модель редактора без WPF — история, правки, проверка проводов, раскладка, хранилище, каталог.</summary>
public class EditorTests
{
    private static readonly EndpointInfo Headphones = new("{0.0.0.00000000}.{hp}", "Headphones (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo CableIn = new("{0.0.0.00000000}.{ci}", "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo CableIn16 = new("{0.0.0.00000000}.{c16}", "CABLE In 16ch (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo CableOut = new("{0.0.1.00000000}.{co}", "CABLE Output (VB-Audio Virtual Cable)", EDataFlow.Capture, false);
    private static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Shure MV7", EDataFlow.Capture, true);
    private static readonly EndpointInfo VaioIn = new("{0.0.0.00000000}.{vmi}", "Voicemeeter Input (VB-Audio Voicemeeter VAIO)", EDataFlow.Render, false);
    private static readonly List<EndpointInfo> Devices = [Headphones, CableIn, CableIn16, CableOut, Mic, VaioIn];

    private static NodeConfig Node(string id, NodeKind kind, EndpointInfo? ep = null, double y = 60) => new()
    {
        Id = id, Kind = kind, Name = id, EndpointId = ep?.Id, EndpointName = ep?.Name,
        Exe = kind == NodeKind.App ? "discord.exe" : null,
        X = kind == NodeKind.DeviceOutput ? GraphLayout.OutputX : GraphLayout.SourceX, Y = y,
    };

    private static GraphConfig Streamer() => new()
    {
        Nodes =
        [
            Node("mic", NodeKind.DeviceInput, Mic),
            Node("lb", NodeKind.Loopback, Headphones, 210),
            Node("cable", NodeKind.DeviceInput, CableOut, 360),
            Node("app", NodeKind.App, null, 510),
            Node("hp", NodeKind.DeviceOutput, Headphones),
            Node("stream", NodeKind.DeviceOutput, CableIn, 210),
            Node("c16", NodeKind.DeviceOutput, CableIn16, 360),
        ],
        Wires = [new WireConfig { From = "mic", To = "hp" }, new WireConfig { From = "app", To = "stream", GainDb = -6 }],
    };

    // ── история ──

    [Fact]
    public void Undo_and_redo_walk_snapshots()
    {
        var g0 = Streamer();
        var h = new GraphHistory(g0);
        var g1 = GraphEdits.Connect(g0, "mic", "stream");
        var g2 = GraphEdits.RemoveNode(g1, "app");
        h.Apply(g1);
        h.Apply(g2);
        Assert.True(h.Undo());
        Assert.Same(g1, h.Current);
        Assert.True(h.Undo());
        Assert.Same(g0, h.Current);
        Assert.False(h.Undo());
        Assert.True(h.Redo());
        Assert.Same(g1, h.Current);
        var g3 = GraphEdits.Disconnect(g1, "mic", "hp");
        h.Apply(g3);
        Assert.False(h.CanRedo); // новая правка отрезает ветку повтора
    }

    [Fact]
    public void Continuous_edit_merges_into_one_step_until_released()
    {
        long now = 0;
        var g = Streamer();
        var h = new GraphHistory(g, () => now);
        for (int i = 1; i <= 30; i++)
        {
            now += 30; // фейдер: правка каждые 30 мс
            h.Apply(GraphEdits.UpdateNode(h.Current, "mic", n => n with { GainDb = -i }), "gain:mic");
        }
        Assert.Equal(1, h.UndoDepth);
        h.EndMerge();
        h.Apply(GraphEdits.UpdateNode(h.Current, "mic", n => n with { GainDb = 3 }), "gain:mic");
        Assert.Equal(2, h.UndoDepth);
        h.Undo();
        Assert.Equal(-30, GraphEdits.Find(h.Current, "mic")!.GainDb);
        h.Undo();
        Assert.Equal(0, GraphEdits.Find(h.Current, "mic")!.GainDb);
    }

    [Fact]
    public void Same_key_after_pause_is_a_new_step_and_other_key_never_merges()
    {
        long now = 0;
        var h = new GraphHistory(Streamer(), () => now);
        h.Apply(GraphEdits.UpdateNode(h.Current, "mic", n => n with { GainDb = -1 }), "gain:mic");
        now += 3000;
        h.Apply(GraphEdits.UpdateNode(h.Current, "mic", n => n with { GainDb = -2 }), "gain:mic");
        now += 10;
        h.Apply(GraphEdits.UpdateNode(h.Current, "hp", n => n with { GainDb = -2 }), "gain:hp");
        Assert.Equal(3, h.UndoDepth);
    }

    [Fact]
    public void History_keeps_fifty_steps()
    {
        var h = new GraphHistory(Streamer());
        for (int i = 0; i < 80; i++) h.Apply(GraphEdits.UpdateNode(h.Current, "mic", n => n with { GainDb = i }));
        Assert.Equal(GraphHistory.Capacity, h.UndoDepth);
        while (h.Undo()) { }
        Assert.Equal(29, GraphEdits.Find(h.Current, "mic")!.GainDb); // самые старые шаги отброшены
    }

    // ── правки ──

    [Fact]
    public void Removing_node_removes_its_wires()
    {
        var g = GraphEdits.RemoveNode(Streamer(), "app");
        Assert.DoesNotContain(g.Nodes, n => n.Id == "app");
        Assert.DoesNotContain(g.Wires, w => w.From == "app");
        Assert.Single(g.Wires);
    }

    [Fact]
    public void Reroute_keeps_gain_and_refuses_duplicates()
    {
        var g = GraphEdits.Reroute(Streamer(), "app", "stream", "app", "hp");
        var w = Assert.Single(g.Wires, x => x.From == "app");
        Assert.Equal("hp", w.To);
        Assert.Equal(-6, w.GainDb);
        var dup = GraphEdits.Reroute(g, "app", "hp", "mic", "hp"); // mic → hp уже есть
        Assert.Same(g, dup);
        Assert.Same(g, GraphEdits.Connect(g, "mic", "hp"));
    }

    [Fact]
    public void New_node_ids_are_unique()
    {
        var g = Streamer();
        string id = GraphEdits.NewNodeId(g);
        Assert.Equal("n1", id);
        g = GraphEdits.AddNode(g, Node(id, NodeKind.DeviceInput, Mic));
        Assert.Equal("n2", GraphEdits.NewNodeId(g));
    }

    // ── проверка провода ──

    [Fact]
    public void Connect_check_normalizes_direction_and_rejects_duplicates_and_same_side()
    {
        var g = Streamer();
        var back = ConnectionRules.Check(g, "stream", "mic", Devices); // тянули от выхода к источнику
        Assert.True(back.Ok);
        Assert.Equal(("mic", "stream"), (back.From, back.To));
        Assert.Equal(ConnectVerdict.Exists, ConnectionRules.Check(g, "mic", "hp", Devices).Verdict);
        Assert.Equal(ConnectVerdict.SameSide, ConnectionRules.Check(g, "mic", "app", Devices).Verdict);
        Assert.Equal(ConnectVerdict.SameSide, ConnectionRules.Check(g, "hp", "stream", Devices).Verdict);
    }

    [Fact]
    public void Connect_check_forbids_loops_with_reason()
    {
        var g = Streamer();
        var loop = ConnectionRules.Check(g, "lb", "hp", Devices);
        Assert.Equal(ConnectVerdict.Feedback, loop.Verdict);
        Assert.Contains("Петля", loop.Reason);
        Assert.True(ConnectionRules.Check(g, "lb", "stream", Devices).Ok);

        Assert.Equal(ConnectVerdict.Feedback, ConnectionRules.Check(g, "cable", "stream", Devices).Verdict);
        Assert.Equal(ConnectVerdict.Feedback, ConnectionRules.Check(g, "cable", "c16", Devices).Verdict);
        Assert.Contains("кабель", ConnectionRules.Check(g, "cable", "stream", Devices).Reason);
        Assert.True(ConnectionRules.Check(g, "cable", "hp", Devices).Ok);
        Assert.True(ConnectionRules.Check(g, "app", "hp", Devices).Ok); // приложение — не устройство, петли нет
    }

    [Fact]
    public void Loopback_into_its_own_disconnected_output_is_still_a_loop()
    {
        var g = Streamer();
        var none = Devices.Where(d => d != Headphones).ToList(); // наушники отключены
        Assert.Equal(ConnectVerdict.Feedback, ConnectionRules.Check(g, "lb", "hp", none).Verdict);
    }

    // ── раскладка ──

    [Fact]
    public void Nodes_stay_in_their_column()
    {
        var (x, _) = GraphLayout.Clamp(source: true, 900, 100);
        Assert.True(x + GraphLayout.CardWidth < GraphLayout.Width / 2);
        var (ox, oy) = GraphLayout.Clamp(source: false, 0, -500);
        Assert.True(ox > GraphLayout.Width / 2);
        Assert.True(oy >= 0);
        Assert.Equal(GraphLayout.SourceX, GraphLayout.Clamp(true, GraphLayout.SourceX, 300).X);
        Assert.Equal(GraphLayout.OutputX, GraphLayout.Clamp(false, GraphLayout.OutputX, 300).X);
    }

    [Fact]
    public void New_node_goes_below_its_column_or_to_a_free_spot()
    {
        var g = Streamer();
        var (_, y) = GraphLayout.NextSlot(g, source: false);
        Assert.Equal(360 + GraphLayout.CardHeight + GraphLayout.Gap, y);
        var (_, free) = GraphLayout.NextSlot(g, source: false, atY: 560 + GraphLayout.PortY);
        Assert.Equal(560, free);
        var (_, busy) = GraphLayout.NextSlot(g, source: false, atY: 240); // занято узлом «stream»
        Assert.Equal(y, busy);
        Assert.Equal((GraphLayout.SourceX, GraphLayout.Top), GraphLayout.NextSlot(new GraphConfig(), source: true));
    }

    // ── что поменялось ──

    [Fact]
    public void Change_kind_separates_layout_parameters_and_structure()
    {
        var g = Streamer();
        Assert.Equal(GraphChangeKind.None, GraphChange.Classify(g, g with { }));
        Assert.Equal(GraphChangeKind.Layout, GraphChange.Classify(g, GraphEdits.UpdateNode(g, "mic", n => n with { X = 50, Y = 90, Color = "#fff" })));
        Assert.Equal(GraphChangeKind.Parameters, GraphChange.Classify(g, GraphEdits.UpdateNode(g, "mic", n => n with { GainDb = -3 })));
        Assert.Equal(GraphChangeKind.Parameters, GraphChange.Classify(g, GraphEdits.UpdateNode(g, "mic", n => n with { Name = "Голос" })));
        Assert.Equal(GraphChangeKind.Parameters, GraphChange.Classify(g, GraphEdits.UpdateWire(g, "mic", "hp", w => w with { Enabled = false })));
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, GraphEdits.Connect(g, "mic", "stream")));
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, GraphEdits.UpdateNode(g, "app", n => n with { TakeOver = false })));
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, GraphEdits.SetSink(g, CableIn.Id)));
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, GraphEdits.Reroute(g, "app", "stream", "app", "hp")));
        var dup = g with { Wires = [g.Wires[0], g.Wires[0]] };
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, dup));
    }

    // ── Voicemeeter ──

    [Fact]
    public void Voicemeeter_endpoint_waits_while_voicemeeter_is_not_running()
    {
        var g = new GraphConfig
        {
            Nodes = [Node("mic", NodeKind.DeviceInput, Mic), Node("vm", NodeKind.DeviceOutput, VaioIn)],
            Wires = [new WireConfig { From = "mic", To = "vm" }],
        };
        var off = GraphBuilder.Build(g, Devices, apps: new RuntimeContext { VoicemeeterRunning = false });
        Assert.Empty(off.Graph.Outputs);
        Assert.Empty(off.Graph.Wires);
        Assert.Contains(off.Issues, i => i.Kind == IssueKind.WaitingDevice && i.Subject == "vm" && i.Message.Contains("Voicemeeter"));
        var on = GraphBuilder.Build(g, Devices, apps: new RuntimeContext { VoicemeeterRunning = true });
        Assert.Single(on.Graph.Outputs);
        Assert.Single(on.Graph.Wires);
        Assert.Single(GraphBuilder.Build(g, Devices).Graph.Outputs); // неизвестно — не блокируем
    }

    // ── хранилище ──

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "graph.json");

    [Fact]
    public void Store_merges_ui_graph_with_host_journal()
    {
        string path = TempPath();
        var g = Streamer();
        using var store = new GraphStore(path, g);
        var edited = GraphEdits.UpdateNode(g, "mic", n => n with { Name = "Голос" });
        store.Changed(edited);
        Assert.True(store.IsDirty);
        // хост пишет журнал со своим (старым) графом — правка интерфейса не теряется
        store.SaveJournal(g with { BoundApps = ["discord.exe"], VolumeApps = ["firefox.exe"] });
        var disk = GraphSerializer.Load(path);
        Assert.Equal("Голос", GraphEdits.Find(disk, "mic")!.Name);
        Assert.Equal(new[] { "discord.exe" }, disk.BoundApps);
        Assert.Equal(new[] { "firefox.exe" }, disk.VolumeApps);
        Assert.False(store.IsDirty);
        // и наоборот: граф интерфейса со старым журналом журнал не откатывает
        store.Changed(GraphEdits.UpdateNode(edited, "mic", n => n with { GainDb = -5 }));
        store.Flush();
        disk = GraphSerializer.Load(path);
        Assert.Equal(-5, GraphEdits.Find(disk, "mic")!.GainDb);
        Assert.Equal(new[] { "discord.exe" }, disk.BoundApps);
        Assert.Equal(new[] { "firefox.exe" }, disk.VolumeApps);
    }

    [Fact]
    public void Store_saves_after_delay()
    {
        string path = TempPath();
        using var store = new GraphStore(path, new GraphConfig());
        using var saved = new ManualResetEventSlim();
        store.Saved += e => { if (e is null) saved.Set(); };
        store.Changed(Streamer());
        Assert.False(File.Exists(path));
        Assert.True(saved.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(7, GraphSerializer.Load(path).Nodes.Count);
    }

    [Fact]
    public void Broken_graph_is_set_aside_not_overwritten()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"nodes\": [ {");
        var g = GraphStore.LoadOrEmpty(path, out string? error);
        Assert.Empty(g.Nodes);
        Assert.NotNull(error);
        var aside = Directory.GetFiles(Path.GetDirectoryName(path)!, "graph.json.broken-*");
        Assert.Single(aside);
        Assert.Equal("{ \"nodes\": [ {", File.ReadAllText(aside[0]));
        Assert.Empty(GraphStore.LoadOrEmpty(TempPath(), out string? none).Nodes);
        Assert.Null(none);
    }

    [Fact]
    public void Positions_names_gains_and_mute_survive_save_and_load()
    {
        string path = TempPath();
        var g = GraphEdits.UpdateNode(Streamer(), "mic", n => n with { X = 77, Y = 333, Name = "Голос «эфир»", GainDb = -7.5f, Muted = true, Mono = true, Balance = -0.25f, Color = "#FF9A4D" });
        g = GraphEdits.UpdateWire(g, "app", "stream", w => w with { Enabled = false });
        GraphSerializer.Save(path, g);
        var back = GraphSerializer.Load(path);
        Assert.Equal(GraphEdits.Find(g, "mic"), GraphEdits.Find(back, "mic"));
        Assert.Equal(g.Wires, back.Wires);
        Assert.Equal(GraphChangeKind.None, GraphChange.Classify(g, back));
    }

    // ── каталог приложений ──

    [Fact]
    public void Catalog_lists_session_apps_first_and_hides_mixer_and_services()
    {
        const uint self = 100;
        var procs = new ProcessSnapshot(
        [
            new ProcInfo(self, 1, "mixer.exe"),
            new ProcInfo(101, self, "helper.exe"), // дочерний процесс микшера
            new ProcInfo(10, 1, "chrome.exe"),
            new ProcInfo(11, 10, "chrome.exe"),
            new ProcInfo(12, 10, "chrome.exe"),
            new ProcInfo(20, 1, "obs64.exe"),
            new ProcInfo(30, 1, "Telegram.exe"),
            new ProcInfo(40, 1, "svchost.exe"),
        ], self);
        var sessions = new List<AudioSessionInfo>
        {
            new(11, Headphones.Id, AudioSessionState.Active),
            new(30, Headphones.Id, AudioSessionState.Inactive),
            new(101, Headphones.Id, AudioSessionState.Active),
            new(40, Headphones.Id, AudioSessionState.Active),
        };
        var windows = new HashSet<uint> { 10, 20, self };
        var apps = AppCatalog.Build(procs, sessions, windows, pid => pid == 10 ? @"C:\chrome.exe" : null, (exe, _) => exe);
        Assert.Equal(new[] { "chrome.exe", "telegram.exe", "obs64.exe" }, apps.Select(a => a.Exe));
        Assert.True(apps[0].Playing);
        Assert.Equal(3, apps[0].Processes);
        Assert.Equal(@"C:\chrome.exe", apps[0].Path);
        Assert.True(apps[1].HasSession);
        Assert.False(apps[1].Playing);
        Assert.False(apps[2].HasSession);
    }
}
