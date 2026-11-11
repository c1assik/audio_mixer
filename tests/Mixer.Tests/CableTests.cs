using Mixer.App.ViewModels;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Cables;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;
using Mixer.Core.Sys;

namespace Mixer.Tests;

/// <summary>Этап 6 (v2): таблица сопоставления кабелей, роли, назначение микрофона, проверки, перенос с v1.</summary>
public class CableTests
{
    internal static readonly EndpointInfo Hp = new("{0.0.0.00000000}.{hp}", "Наушники (Realtek)", EDataFlow.Render, true);
    internal static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Микрофон (fifine)", EDataFlow.Capture, true);
    internal static readonly EndpointInfo CableIn = new("{0.0.0.00000000}.{ci}", "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    internal static readonly EndpointInfo Cable16 = new("{0.0.0.00000000}.{c16}", "CABLE In 16ch (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    internal static readonly EndpointInfo CableOut = new("{0.0.1.00000000}.{co}", "CABLE Output (VB-Audio Virtual Cable)", EDataFlow.Capture, false);
    internal static readonly EndpointInfo AIn = new("{0.0.0.00000000}.{ai}", "CABLE-A Input (VB-Audio Cable A)", EDataFlow.Render, false);
    internal static readonly EndpointInfo AOut = new("{0.0.1.00000000}.{ao}", "CABLE-A Output (VB-Audio Cable A)", EDataFlow.Capture, false);
    private static readonly EndpointInfo Line1In = new("{0.0.0.00000000}.{l1i}", "Line 1 (Virtual Audio Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo Line1Out = new("{0.0.1.00000000}.{l1o}", "Line 1 (Virtual Audio Cable)", EDataFlow.Capture, false);
    private static readonly EndpointInfo Line2In = new("{0.0.0.00000000}.{l2i}", "Line 2 (Virtual Audio Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo Line2Out = new("{0.0.1.00000000}.{l2o}", "Line 2 (Virtual Audio Cable)", EDataFlow.Capture, false);
    private static readonly EndpointInfo OddIn = new("{0.0.0.00000000}.{xi}", "Odd Pipe In (Acme Virtual Audio)", EDataFlow.Render, false);
    private static readonly EndpointInfo OddOut = new("{0.0.1.00000000}.{xo}", "Odd Pipe Out (Acme Virtual Audio)", EDataFlow.Capture, false);

    private static readonly List<EndpointInfo> All = [Hp, Mic, CableIn, Cable16, CableOut, AIn, AOut, Line1In, Line1Out, Line2In, Line2Out, OddIn, OddOut];

    private static NodeConfig Out(string id, EndpointInfo ep, string? name = null) =>
        new() { Id = id, Kind = NodeKind.DeviceOutput, Name = name ?? id, EndpointId = ep.Id, EndpointName = ep.Name };

    private static NodeConfig In(string id, EndpointInfo ep, string? name = null) =>
        new() { Id = id, Kind = NodeKind.DeviceInput, Name = name ?? id, EndpointId = ep.Id, EndpointName = ep.Name };

    [Fact]
    public void Table_pairs_cable_ends_by_driver_and_description()
    {
        var pairs = CableTable.Builtin.Pairs(All);
        Assert.Equal(["vb-cable", "vb-cable-a", "vac:line 1", "vac:line 2"], pairs.Select(p => p.Id));

        var vb = pairs[0];
        Assert.Equal("CABLE", vb.Name);
        Assert.Equal("CABLE (VB-Audio)", vb.Label);
        Assert.Equal([CableIn, Cable16], vb.Inputs); // основной вход первым
        Assert.Equal(CableOut, vb.Output);
        Assert.True(vb.Complete);

        // Virtual Audio Cable: концы называются одинаково, линии не путаются
        Assert.Equal(Line1In, pairs[2].Input);
        Assert.Equal(Line1Out, pairs[2].Output);
        Assert.Equal("Line 1", pairs[2].Name);
        Assert.Equal(Line2Out, pairs[3].Output);

        // неизвестный кабель и обычные устройства не распознаются
        Assert.Null(CableTable.Builtin.Match(OddIn));
        Assert.Null(CableTable.Builtin.Match(Hp));
        Assert.Null(CableTable.Builtin.Match("Voicemeeter Input (VB-Audio Voicemeeter VAIO)", EDataFlow.Render));

        // кабель с одним концом тоже виден, но не годен
        var half = CableTable.Builtin.Pairs([AIn]);
        Assert.Single(half);
        Assert.False(half[0].Complete);
    }

    [Fact]
    public void Own_table_next_to_the_program_goes_first_and_a_broken_one_is_ignored()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cables.json");
        File.WriteAllText(path, """
            { "cables": [ { "id": "acme", "name": "Odd Pipe", "vendor": "Acme", "driver": "Acme Virtual Audio", "inputs": ["Odd Pipe In"], "outputs": ["Odd Pipe Out"] } ] }
            """);
        var t = CableTable.LoadWithExtra(path, out string? error);
        Assert.Null(error);
        Assert.Equal("acme", t.Models[0].Id);
        var odd = t.Pairs(All).Single(p => p.Id == "acme");
        Assert.Equal(OddIn, odd.Input);
        Assert.Equal(OddOut, odd.Output);
        Assert.Contains(t.Pairs(All), p => p.Id == "vb-cable"); // встроенные на месте

        File.WriteAllText(path, "{ broken");
        var fallback = CableTable.LoadWithExtra(path, out error);
        Assert.NotNull(error);
        Assert.Equal(CableTable.Builtin.Models.Count, fallback.Models.Count);
    }

    [Fact]
    public void Role_follows_how_the_mixer_uses_the_cable()
    {
        var t = CableTable.Builtin;
        Assert.Equal(new CableLink { PairId = "vb-cable", Role = CableRole.VirtualMic }, t.Detect(Out("o", CableIn)));
        Assert.Equal(new CableLink { PairId = "vb-cable", Role = CableRole.VirtualMic }, t.Detect(Out("o", Cable16)));
        Assert.Equal(new CableLink { PairId = "vb-cable", Role = CableRole.VirtualInput }, t.Detect(In("i", CableOut)));
        Assert.Null(t.Detect(Out("o", Hp)));
        Assert.Null(t.Detect(new NodeConfig { Id = "lb", Kind = NodeKind.Loopback, EndpointId = CableIn.Id, EndpointName = CableIn.Name }));
        Assert.Null(t.Detect(new NodeConfig { Id = "d", Kind = NodeKind.DeviceOutput, DefaultRole = DefaultRole.Console }));

        // пара узла: по таблице или связанная вручную
        Assert.Equal(CableOut, t.PairOf(Out("o", CableIn), All)!.Output);
        var manual = GraphEdits.LinkCable(new GraphConfig { Nodes = [Out("o", OddIn)] }, "o", OddOut).Nodes[0];
        Assert.Equal(CableRole.VirtualMic, manual.Cable!.Role);
        Assert.Equal(OddOut.Id, manual.Cable.PartnerId);
        Assert.Equal(OddOut, t.PairOf(manual, All)!.Output);
        Assert.Equal(OddIn, t.PairOf(manual, All)!.Input);
        Assert.Null(GraphEdits.UnlinkCable(new GraphConfig { Nodes = [manual with { MicFor = ["obs64.exe"] }] }, "o").Nodes[0].Cable);
    }

    [Fact]
    public void Loops_through_a_cable_use_the_table()
    {
        var devices = All;
        // Virtual Audio Cable: Line 1 слышит только свою Line 1
        var src = In("l1", Line1Out);
        Assert.NotNull(FeedbackRules.WhyForbidden(src, Line1Out, Line1In, devices));
        Assert.Null(FeedbackRules.WhyForbidden(src, Line1Out, Line2In, devices));
        // VB-Cable: оба входа ведут в один выход
        Assert.NotNull(FeedbackRules.WhyForbidden(In("c", CableOut), CableOut, Cable16, devices));
        Assert.True(FeedbackRules.SameCable(CableIn, Cable16));
        Assert.False(FeedbackRules.SameCable(CableIn, AIn));
        Assert.False(FeedbackRules.SameCable(Line1In, Line2In));
        // кабель, связанный вручную: петля через второй конец тоже запрещена
        var manual = GraphEdits.LinkCable(new GraphConfig { Nodes = [In("x", OddOut)] }, "x", OddIn).Nodes[0];
        Assert.Null(FeedbackRules.WhyForbidden(In("x", OddOut), OddOut, OddIn, devices));
        Assert.NotNull(FeedbackRules.WhyForbidden(manual, OddOut, OddIn, devices));
    }

    [Fact]
    public void Migration_from_v1_links_cables()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "graph.json");
        File.WriteAllText(path, $$"""
            { "version": 1, "nodes": [
              { "id": "a", "kind": "deviceOutput", "name": "Стрим", "endpointId": "{{CableIn.Id}}", "endpointName": "{{CableIn.Name}}" },
              { "id": "b", "kind": "deviceInput", "name": "Игра", "endpointId": "{{CableOut.Id}}", "endpointName": "{{CableOut.Name}}" },
              { "id": "c", "kind": "deviceOutput", "name": "Наушники", "endpointId": "{{Hp.Id}}", "endpointName": "{{Hp.Name}}" } ],
              "wires": [] }
            """);
        var g = GraphStore.LoadOrEmpty(path, out string? error, out bool upgraded);
        Assert.Null(error);
        Assert.True(upgraded);
        Assert.Equal(CableRole.VirtualMic, g.Nodes[0].Cable!.Role);
        Assert.Equal("vb-cable", g.Nodes[0].Cable!.PairId);
        Assert.Equal(CableRole.VirtualInput, g.Nodes[1].Cable!.Role);
        Assert.Null(g.Nodes[2].Cable);
    }

    [Fact]
    public void Cable_fields_survive_json_and_are_structural_edits()
    {
        var g = new GraphConfig
        {
            Nodes = [Out("o", CableIn) with { Cable = new CableLink { PairId = "vb-cable", Role = CableRole.VirtualMic }, MicFor = ["obs64.exe"] }],
            MicApps = ["obs64.exe"],
        };
        var back = GraphSerializer.Deserialize(GraphSerializer.Serialize(g));
        Assert.Equal(g.Nodes[0].Cable, back.Nodes[0].Cable);
        Assert.Equal(["obs64.exe"], back.Nodes[0].MicFor!);
        Assert.Equal(["obs64.exe"], back.MicApps);
        Assert.Contains("\"virtualMic\"", GraphSerializer.Serialize(g));

        var edited = GraphEdits.SetMicFor(g, "o", "  Discord ", true);
        Assert.Equal(["obs64.exe", "discord.exe"], edited.Nodes[0].MicFor!);
        Assert.Same(edited, GraphEdits.SetMicFor(edited, "o", "", true));
        Assert.Equal(["obs64.exe", "discord.exe"], GraphEdits.SetMicFor(edited, "o", "DISCORD.exe", true).Nodes[0].MicFor!); // без дублей
        Assert.Null(GraphEdits.SetMicFor(GraphEdits.SetMicFor(edited, "o", "obs64", false), "o", "discord", false).Nodes[0].MicFor);
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, edited));
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, GraphEdits.UnlinkCable(g, "o")));
    }

    [Fact]
    public void Store_keeps_the_microphone_journal_of_the_host()
    {
        string path = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "graph.json");
        var g = new GraphConfig { Nodes = [Out("o", CableIn)] };
        using (var store = new GraphStore(path, g))
        {
            store.SaveJournal(g with { MicApps = ["obs64.exe"] });
            store.Changed(g with { Nodes = [Out("o", CableIn, "Стрим")] }); // граф интерфейса без журнала
            store.Flush();
        }
        var back = GraphSerializer.Load(path);
        Assert.Equal(["obs64.exe"], back.MicApps);
        Assert.Equal("Стрим", back.Nodes[0].Name);
    }

    // ── назначение микрофона ──

    private static readonly ProcInfo[] Procs =
    [
        new(1, 0, "explorer.exe"), new(500, 1, "obs64.exe"), new(600, 1, "Discord.exe"), new(601, 600, "Discord.exe"),
        new(World.Self, 1, "Mixer.exe"),
    ];

    [Fact]
    public void Microphone_assignment_points_the_app_at_the_cable_output_and_is_released_when_removed()
    {
        var world = World.Make(Procs, World.Session(601, Hp.Id));
        var policy = new FakePolicy(() => world.Processes);
        var journal = new FakeJournal();
        var order = new List<string>();
        policy.BeforeSet = _ => order.Add("set:" + string.Join(",", journal.List));
        var mic = new TakeOverManager(policy, journal, checkPlayback: false);

        mic.Reconcile(new Dictionary<string, string?> { ["obs64.exe"] = CableOut.Id, ["discord.exe"] = CableOut.Id }, world);
        Assert.Equal(CableOut.Id, policy.Bindings[("obs64.exe", ERole.Console)]);
        Assert.True(policy.IsBound("discord.exe"));
        Assert.All(order, o => Assert.Contains("obs64.exe", o)); // журнал до первого Set
        Assert.Equal(BindingState.Bound, mic.Status["discord.exe"].State);
        Assert.False(mic.Status["discord.exe"].PlaysDirect); // играет в наушники — это не про запись

        // выход кабеля пропал: назначение недоступно и снимается
        mic.Reconcile(new Dictionary<string, string?> { ["obs64.exe"] = null, ["discord.exe"] = CableOut.Id }, world, "нет выхода кабеля");
        Assert.Equal(BindingState.Unavailable, mic.Status["obs64.exe"].State);
        Assert.False(policy.IsBound("obs64.exe"));

        // узел удалён: назначение снято, журнал пуст
        mic.Reconcile(new Dictionary<string, string?>(), world);
        Assert.False(policy.IsBound("discord.exe"));
        Assert.Empty(journal.List);
    }

    [Fact]
    public void Microphone_assignment_waits_for_an_app_that_is_not_running()
    {
        var world = World.Make(Procs);
        var policy = new FakePolicy(() => world.Processes);
        var mic = new TakeOverManager(policy, new FakeJournal(), checkPlayback: false);
        mic.Reconcile(new Dictionary<string, string?> { ["zoom.exe"] = CableOut.Id }, world);
        Assert.Equal(BindingState.NotRunning, mic.Status["zoom.exe"].State);
        Assert.Empty(policy.Sets);
    }

    // ── проверки ──

    [Fact]
    public void Checks_warn_about_other_writers_rate_mismatch_and_an_app_reading_another_mic()
    {
        var g = new GraphConfig
        {
            Nodes =
            [
                Out("stream", CableIn, "Стрим") with { MicFor = ["obs64.exe"] },
                In("game", AOut, "Игра"),
            ],
        };
        var world = World.Make(Procs,
            World.Session(601, CableIn.Id), // Discord играет прямо в кабель
            World.Session(World.Self, CableIn.Id), // сам микшер — не в счёт
            World.Session(500, Cable16.Id, AudioSessionState.Inactive)); // неактивная — не в счёт
        var capture = new List<AudioSessionInfo> { new(500, Mic.Id, AudioSessionState.Active) }; // OBS пишет с fifine
        var rates = new Dictionary<string, int> { [CableIn.Id] = 48000, [CableOut.Id] = 48000, [AIn.Id] = 48000, [AOut.Id] = 44100 };

        var w = CableChecks.Check(g, All, CableTable.Builtin, world, capture, id => rates.TryGetValue(id, out int r) ? r : null);
        var stream = w["stream"];
        Assert.Equal(2, stream.Count);
        Assert.Contains(stream, s => s.Contains("discord.exe") && s.Contains("CABLE Input"));
        Assert.Contains(stream, s => s.Contains("OBS Studio") && s.Contains("fifine") && s.Contains("По умолчанию"));
        Assert.Contains("44,1", Assert.Single(w["game"]));

        // OBS читает кабель — тихо; частоты равны — тихо
        capture = [new(500, CableOut.Id, AudioSessionState.Active)];
        rates[AOut.Id] = 48000;
        world = World.Make(Procs);
        Assert.Empty(CableChecks.Check(g, All, CableTable.Builtin, world, capture, id => rates.TryGetValue(id, out int r) ? r : null));
    }

    // ── помощник ──

    private static readonly IReadOnlyList<string> Palette = MainViewModel.SourceColors;

    [Fact]
    public void Setup_builds_a_stream_to_obs_graph_with_one_cable()
    {
        var cables = SetupAssistant.UsableCables([Hp, Mic, CableIn, CableOut], CableTable.Builtin);
        var c = new SetupChoices { Headphones = Hp, Microphone = Mic, HeadphonesOnly = false, Stream = true };
        Assert.Equal(1, SetupAssistant.CablesNeeded(c));
        var r = SetupAssistant.Build(new GraphConfig { SinkEndpoint = "sink", BoundApps = ["x.exe"] }, c, cables, Palette);
        Assert.Equal(0, r.MissingCables);
        var g = r.Graph;
        Assert.Equal("sink", g.SinkEndpoint); // заглушка и журналы остаются
        Assert.Equal(["x.exe"], g.BoundApps);

        var mic = g.Nodes.Single(n => n.Kind == NodeKind.DeviceInput);
        Assert.Equal(Mic.Id, mic.EndpointId);
        Assert.True(mic.HasChain); // «Голос — чистый»
        Assert.Equal(mic.Id, g.MainMic);
        var sys = g.Nodes.Single(n => n.Kind == NodeKind.System);
        var stream = g.Nodes.Single(n => n.IsOutput);
        Assert.Equal(CableIn.Id, stream.EndpointId);
        Assert.Equal(CableRole.VirtualMic, stream.Cable!.Role);
        Assert.Equal(["obs64.exe"], stream.MicFor!);
        Assert.Equal(new[] { (mic.Id, stream.Id), (sys.Id, stream.Id) }, g.Wires.Select(w => (w.From, w.To)));
        // в наушники «Весь звук системы» не ведётся: приложения там и так звучат сами
        Assert.DoesNotContain(g.Nodes, n => n.EndpointId == Hp.Id);

        // схема проходит проверку проводов и строится без ошибок
        var build = GraphBuilder.Build(g, [Hp, Mic, CableIn, CableOut]);
        Assert.DoesNotContain(build.Issues, i => i.Kind is IssueKind.Invalid or IssueKind.Feedback);
        Assert.All(g.Nodes.Where(n => n.IsSource), n => Assert.NotNull(n.Color));
    }

    [Fact]
    public void Setup_with_stream_and_call_needs_two_cables_and_keeps_separate_apps_off_stream()
    {
        var c = new SetupChoices
        {
            Headphones = Hp, Microphone = Mic, Stream = true, Call = true,
            SeparateApps = [new SetupApp("Spotify.exe", "Spotify"), new SetupApp("spotify", "Spotify")],
            CallApps = ["discord.exe", "zoom.exe"],
        };
        Assert.Equal(2, SetupAssistant.CablesNeeded(c));

        // один кабель: звонок пропущен
        var one = SetupAssistant.Build(new GraphConfig(), c, SetupAssistant.UsableCables([Hp, Mic, CableIn, CableOut], CableTable.Builtin), Palette);
        Assert.Equal(1, one.MissingCables);
        Assert.Single(one.Graph.Nodes, n => n.Cable is not null);

        var two = SetupAssistant.Build(new GraphConfig(), c, SetupAssistant.UsableCables(All, CableTable.Builtin), Palette).Graph;
        var app = Assert.Single(two.Nodes, n => n.Kind == NodeKind.App);
        Assert.Equal("spotify.exe", app.Exe);
        var hp = two.Nodes.Single(n => n.EndpointId == Hp.Id);
        Assert.Contains(two.Wires, w => w.From == app.Id && w.To == hp.Id);
        var call = two.Nodes.Single(n => n.MicFor?.Contains("zoom.exe") == true);
        Assert.Equal(AIn.Id, call.EndpointId); // второй кабель
        Assert.Contains(two.Wires, w => w.To == call.Id && w.From == two.MainMic);

        // приложение со своим узлом — вне «Весь звук системы» (а значит, и вне стрима)
        var sys = two.Nodes.Single(n => n.Kind == NodeKind.System);
        Assert.Contains("spotify.exe", GraphBuilder.SystemExclusions(sys, ["spotify.exe"]));
        // и идёт напрямую в наушники: одна громкость, без захвата и без заглушки
        var direct = GraphBuilder.Build(two, All, apps: new RuntimeContext { DirectRoutes = true }).DirectRoutes;
        Assert.True(direct.ContainsKey(app.Id));
    }

    [Fact]
    public void Setup_for_headphones_only_needs_no_cables()
    {
        var c = new SetupChoices { Headphones = Hp, Microphone = Mic, HeadphonesOnly = true, SeparateApps = [new SetupApp("game.exe", "Game")] };
        Assert.Equal(0, SetupAssistant.CablesNeeded(c));
        var g = SetupAssistant.Build(new GraphConfig(), c, [], Palette).Graph;
        Assert.Equal([NodeKind.App, NodeKind.DeviceOutput], g.Nodes.Select(n => n.Kind));
        Assert.Null(g.MainMic); // микрофон не нужен
        Assert.Single(g.Wires);
    }
}
