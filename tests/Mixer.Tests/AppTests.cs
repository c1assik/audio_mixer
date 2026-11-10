using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Graph;
using Mixer.Core.Sys;

namespace Mixer.Tests;

/// <summary>Процессы и сессии для тестов: Discord (два корня), Chrome (корень и дети), микшер — PID 999.</summary>
internal static class World
{
    public const uint Self = 999;
    public const string Headphones = "{0.0.0.00000000}.{hp}";
    public const string Sink = "{0.0.0.00000000}.{ci}";

    public static readonly ProcInfo[] BaseProcs =
    [
        new(1, 0, "explorer.exe"),
        new(100, 1, "Discord.exe"), new(101, 100, "Discord.exe"), new(102, 100, "Discord.exe"),
        new(200, 1, "chrome.exe"), new(201, 200, "chrome.exe"), new(202, 200, "chrome.exe"),
        new(300, 1, "discord.exe"), // второй экземпляр, свой корень
        new(Self, 1, "Mixer.Run.exe"), new(1000, Self, "conhost.exe"),
    ];

    public static ProcessSnapshot Procs(params ProcInfo[] procs) => new(procs.Length == 0 ? BaseProcs : procs, Self);

    public static AppSnapshot Make(IEnumerable<ProcInfo>? procs = null, params AudioSessionInfo[] sessions) =>
        new(new ProcessSnapshot(procs ?? BaseProcs, Self), sessions);

    public static AudioSessionInfo Session(uint pid, string ep = Headphones, AudioSessionState state = AudioSessionState.Active) => new(pid, ep, state);
}

public class ProcessTests
{
    [Fact]
    public void Roots_and_tree_of_multiprocess_app()
    {
        var p = World.Procs();
        Assert.Equal([100u, 300u], p.Roots("discord").Select(r => r.Pid));
        Assert.Equal([200u], p.Roots("Chrome.EXE").Select(r => r.Pid));
        Assert.Equal(new HashSet<uint> { 200, 201, 202 }, p.Tree([200]));
        Assert.Equal("discord.exe", ProcessSnapshot.NormalizeExe("  Discord "));
        Assert.Equal("discord.exe", ProcessSnapshot.NormalizeExe(@"C:\x\Discord.exe"));
    }

    [Fact]
    public void Mixer_itself_is_never_a_target()
    {
        var p = World.Procs();
        Assert.Empty(p.All("mixer.run.exe"));
        Assert.Empty(p.Roots("Mixer.Run"));
    }

    [Fact]
    public void Binding_targets_are_session_pids_plus_roots()
    {
        var w = World.Make(null, World.Session(102), World.Session(201), World.Session(1000));
        Assert.Equal([100u, 102u, 300u], w.BindingTargets("discord.exe"));
        Assert.Equal([200u, 201u], w.BindingTargets("chrome.exe"));
        Assert.True(w.TreeHasActiveSession(200));
        Assert.False(w.TreeHasActiveSession(300));
    }

    [Fact]
    public void System_ignores_mixer_own_sessions()
    {
        Assert.False(World.Make(null, World.Session(1000)).OthersHaveActiveSession());
        Assert.False(World.Make(null, World.Session(201, state: AudioSessionState.Inactive)).OthersHaveActiveSession());
        Assert.True(World.Make(null, World.Session(201)).OthersHaveActiveSession());
    }
}

public class AppGraphTests
{
    private static readonly EndpointInfo Hp = new(World.Headphones, "Headphones (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo Spk = new("{0.0.0.00000000}.{spk}", "Speakers (USB)", EDataFlow.Render, false);
    private static readonly EndpointInfo CableIn = new(World.Sink, "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo CableOut = new("{0.0.1.00000000}.{co}", "CABLE Output (VB-Audio Virtual Cable)", EDataFlow.Capture, false);
    private static readonly List<EndpointInfo> Devices = [Hp, Spk, CableIn, CableOut];

    private static readonly Dictionary<string, int> Rates = new() { [Hp.Id] = 48000, [Spk.Id] = 44100, [CableIn.Id] = 48000 };

    private static RuntimeContext Ctx(string? sink = null, string? unavailable = null) => new()
    {
        SelfPid = World.Self,
        Roots = exe => World.Procs().Roots(exe).Select(p => p.Pid).ToList(),
        OutputRate = id => Rates.TryGetValue(id, out int r) ? r : null,
        ActiveSinkEndpointId = sink,
        ProcessLoopbackUnavailable = unavailable,
    };

    private static NodeConfig App(string id, string exe, bool? takeOver = null) => new() { Id = id, Kind = NodeKind.App, Name = id, Exe = exe, TakeOver = takeOver };
    private static NodeConfig Sys(string id = "sys") => new() { Id = id, Kind = NodeKind.System, Name = "Весь звук" };
    private static NodeConfig Out(string id, EndpointInfo ep) => new() { Id = id, Kind = NodeKind.DeviceOutput, Name = id, EndpointId = ep.Id, EndpointName = ep.Name };

    [Fact]
    public void App_with_two_roots_gets_two_captures_summed_on_outputs()
    {
        var g = new GraphConfig
        {
            Nodes = [App("d", "discord.exe"), Out("hp", Hp)],
            Wires = [new() { From = "d", To = "hp", GainDb = -6 }],
        };
        var r = GraphBuilder.Build(g, Devices, apps: Ctx());
        Assert.Equal(["d@100", "d@300"], r.Graph.Sources.Select(s => s.Id));
        Assert.All(r.Graph.Sources, s => Assert.Equal(SourceKind.App, s.Kind));
        Assert.Equal([100u, 300u], r.Graph.Sources.Select(s => s.ProcessId));
        Assert.Equal([new WireSpec("d@100", "hp", -6), new WireSpec("d@300", "hp", -6)], r.Graph.Wires);
        Assert.Equal(["d@100", "d@300"], r.EngineSources["d"]);
        Assert.Equal("d", GraphBuilder.NodeIdOf("d@300"));
        Assert.DoesNotContain(r.Issues, i => i.Kind == IssueKind.WaitingProcess);
    }

    [Fact]
    public void System_excludes_mixer_and_only_one_is_allowed()
    {
        var g = new GraphConfig
        {
            Nodes = [Sys(), Sys("sys2"), Out("hp", Hp)],
            Wires = [new() { From = "sys", To = "hp" }, new() { From = "sys2", To = "hp" }],
        };
        var r = GraphBuilder.Build(g, Devices, apps: Ctx());
        var s = Assert.Single(r.Graph.Sources);
        Assert.Equal((SourceKind.System, World.Self, "sys"), (s.Kind, s.ProcessId, s.Id));
        Assert.Single(r.Graph.Wires);
        Assert.Contains(r.Issues, i => i.Kind == IssueKind.Invalid && i.Subject == "sys2");
    }

    [Theory]
    [InlineData(new[] { "hp" }, 48000)]
    [InlineData(new[] { "spk" }, 44100)]          // все выходы на 44,1 — захват сразу на 44,1, без ресемплинга
    [InlineData(new[] { "hp", "spk" }, 48000)]    // разные частоты — 48 кГц, провод в 44,1 ресемплирует 48 → 44,1
    [InlineData(new string[0], 48000)]
    public void Capture_rate_follows_outputs(string[] outs, int expected)
    {
        var g = new GraphConfig
        {
            Nodes = [App("c", "chrome"), Out("hp", Hp), Out("spk", Spk)],
            Wires = outs.Select(o => new WireConfig { From = "c", To = o }).ToList(),
        };
        var r = GraphBuilder.Build(g, Devices, apps: Ctx());
        Assert.Equal(expected, r.Graph.Sources.Single().SampleRate);
    }

    [Fact]
    public void Unknown_output_rates_do_not_count()
    {
        Assert.Equal(44100, GraphBuilder.CaptureRate([44100, null, 44100]));
        Assert.Equal(48000, GraphBuilder.CaptureRate([null]));
    }

    [Fact]
    public void Process_loopback_unavailable_hides_app_nodes()
    {
        var g = new GraphConfig { Nodes = [App("d", "discord"), Sys(), Out("hp", Hp)], Wires = [new() { From = "d", To = "hp" }] };
        var r = GraphBuilder.Build(g, Devices, apps: Ctx(unavailable: "нужна сборка 20348+"));
        Assert.Empty(r.Graph.Sources);
        Assert.Equal(2, r.Issues.Count(i => i.Kind == IssueKind.Unsupported));
    }

    [Fact]
    public void App_feedback_rule_has_no_taps()
    {
        // захват процесса, а не устройства: петли через устройство нет, провод в любой выход допустим
        var g = new GraphConfig { Nodes = [App("c", "chrome"), Out("ci", CableIn)], Wires = [new() { From = "c", To = "ci" }] };
        var r = GraphBuilder.Build(g, Devices, apps: Ctx());
        Assert.Single(r.Graph.Wires);
        Assert.DoesNotContain(r.Issues, i => i.Kind == IssueKind.Feedback);
    }

    [Fact]
    public void Plain_system_capture_and_app_into_same_output_warns_double_sound()
    {
        var g = new GraphConfig
        {
            Nodes = [App("d", "discord"), App("c", "chrome", takeOver: false), Sys(), Out("hp", Hp), Out("spk", Spk)],
            Wires =
            [
                new() { From = "d", To = "hp" }, new() { From = "c", To = "spk" },
                new() { From = "sys", To = "hp" },
            ],
        };
        // без плана «Весь звук системы» — один exclude-захват, приложения в нём есть: «Забрать себе» тут ни при чём
        var warn = Assert.Single(GraphBuilder.Build(g, Devices, apps: Ctx(sink: null)).Issues, i => i.Kind == IssueKind.DoubleSound);
        Assert.Equal("sys", warn.Subject);
        Assert.Contains("d", warn.Message);
        Assert.DoesNotContain("c,", warn.Message); // chrome идёт в другой выход

        var apart = g with { Wires = [new() { From = "d", To = "hp" }, new() { From = "sys", To = "spk" }] };
        Assert.DoesNotContain(GraphBuilder.Build(apart, Devices, apps: Ctx()).Issues, i => i.Kind == IssueKind.DoubleSound);
    }

    private static RuntimeContext CtxWithPlan(AppSnapshot world) =>
        Ctx() with { Roots = exe => world.Roots(exe), PlanSystem = exes => SystemCapture.Plan(world, exes) };

    [Fact]
    public void System_without_apps_on_graph_captures_each_audio_process_except_them()
    {
        var world = World.Make(null, World.Session(101), World.Session(201), World.Session(202, state: AudioSessionState.Inactive), World.Session(1000));
        var g = new GraphConfig
        {
            Nodes = [App("d", "discord"), Sys(), Out("hp", Hp)],
            Wires = [new() { From = "d", To = "hp" }, new() { From = "sys", To = "hp", GainDb = -3 }],
        };
        var r = GraphBuilder.Build(g, Devices, apps: CtxWithPlan(world));
        // chrome — один захват на корень 200, discord — только своим узлом, микшер — никогда
        Assert.Equal(["d@100", "d@300", "sys@200"], r.Graph.Sources.Select(s => s.Id).OrderBy(x => x));
        var sys = r.Graph.Sources.Single(s => s.Id == "sys@200");
        Assert.Equal((SourceKind.App, 200u), (sys.Kind, sys.ProcessId));
        Assert.Contains(new WireSpec("sys@200", "hp", -3), r.Graph.Wires);
        Assert.Equal(["sys@200"], r.EngineSources["sys"]);
        Assert.Equal(["discord.exe"], r.SystemPlans["sys"].Excluded);
        Assert.DoesNotContain(r.Issues, i => i.Kind == IssueKind.DoubleSound);

        // приложений на схеме нет — прежний один exclude-захват
        var plain = GraphBuilder.Build(g with { Nodes = [Sys(), Out("hp", Hp)], Wires = [new() { From = "sys", To = "hp" }] }, Devices, apps: CtxWithPlan(world));
        Assert.Equal((SourceKind.System, World.Self, "sys"), plain.Graph.Sources.Select(s => (s.Kind, s.ProcessId, s.Id)).Single());
        Assert.Empty(plain.SystemPlans);
    }

    [Fact]
    public void Sink_used_in_graph_is_reported()
    {
        var g = new GraphConfig
        {
            Nodes =
            [
                App("d", "discord"), Out("ci", CableIn),
                new() { Id = "co", Kind = NodeKind.DeviceInput, Name = "co", EndpointId = CableOut.Id, EndpointName = CableOut.Name },
            ],
        };
        var issues = GraphBuilder.Build(g, Devices, apps: Ctx(sink: CableIn.Id)).Issues.Where(i => i.Kind == IssueKind.SinkConflict).ToList();
        Assert.Equal(["ci", "co"], issues.Select(i => i.Subject).Order());
    }
}

public class FormatTests
{
    [Fact]
    public void Process_loopback_format_is_float_7_1_extensible()
    {
        var f = WaveFormat.ProcessLoopback(44100);
        byte[] raw = f.ToBytes();
        Assert.Equal(40, raw.Length);
        var back = WaveFormat.FromBytes(raw);
        Assert.Equal((44100, 8, SampleType.Float32, 0x63Fu, 32), (back.SampleRate, back.Channels, back.Type, back.ChannelMask, back.BlockAlign));
        Assert.Equal(0xFFFE, BitConverter.ToUInt16(raw, 0));
        Assert.Equal(44100 * 32, BitConverter.ToInt32(raw, 8));
    }

    [Fact]
    public void Pcm16_stereo_is_plain_waveformatex()
    {
        var raw = new WaveFormat(48000, 2, SampleType.Int16).ToBytes();
        Assert.Equal(18, raw.Length);
        Assert.Equal(1, BitConverter.ToUInt16(raw, 0));
        var back = WaveFormat.FromBytes(raw);
        Assert.Equal((48000, 2, SampleType.Int16), (back.SampleRate, back.Channels, back.Type));
    }

    [Theory]
    [InlineData("{0.0.0.00000000}.{abc}", EDataFlow.Render)]
    [InlineData("{0.0.1.00000000}.{def}", EDataFlow.Capture)]
    public void Endpoint_interface_path_roundtrip(string id, EDataFlow flow)
    {
        string path = EndpointPath.ToInterfacePath(id, flow);
        Assert.StartsWith(@"\\?\SWD#MMDEVAPI#", path);
        Assert.Equal(id, EndpointPath.FromInterfacePath(path));
        Assert.Null(EndpointPath.FromInterfacePath(""));
    }
}

public class WatchdogTests
{
    [Fact]
    public void Restarts_after_3s_without_packets_while_session_active()
    {
        var w = new CaptureWatchdog();
        Assert.False(w.Check("a", 0, sessionActive: true, nodeOpen: true, secondsSinceActivity: 10)); // сессия только что стала активной
        Assert.False(w.Check("a", 2.9, true, true, 12.9));
        Assert.True(w.Check("a", 3.0, true, true, 13));
    }

    [Fact]
    public void Packets_or_no_session_mean_healthy()
    {
        var w = new CaptureWatchdog();
        for (int t = 0; t < 20; t++) Assert.False(w.Check("a", t, true, true, 0.01));          // пакеты (в том числе SILENT) идут
        for (int t = 0; t < 20; t++) Assert.False(w.Check("b", t, false, true, t));            // приложение молчит без сессии — норма
        for (int t = 0; t < 20; t++) Assert.False(w.Check("c", t, true, false, t));            // узел между попытками открытия
    }

    [Fact]
    public void Session_must_stay_active_continuously()
    {
        var w = new CaptureWatchdog();
        w.Check("a", 0, true, true, 50);
        w.Check("a", 2, false, true, 52); // сессия прервалась — отсчёт заново
        Assert.False(w.Check("a", 4, true, true, 54));
        Assert.True(w.Check("a", 7, true, true, 57));
    }
}

public class SinkValidatorTests
{
    private static readonly EndpointInfo Hp = new(World.Headphones, "Headphones (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo CableIn = new(World.Sink, "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo Vaio = new("{0.0.0.00000000}.{vm5}", "Voicemeeter In 5 (VB-Audio Voicemeeter VAIO)", EDataFlow.Render, false);
    private static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Mic", EDataFlow.Capture, true);
    private static readonly List<EndpointInfo> Devices = [Hp, CableIn, Vaio, Mic];

    private static readonly ProbeResult Fine = new(ProbeOutcome.Ok, null, 0.05);

    [Fact]
    public void Vaio_without_voicemeeter_is_rejected_without_opening_a_stream()
    {
        int probes = 0;
        var r = SinkValidator.Check(Vaio.Id, Devices, World.Procs(), _ => { probes++; return Fine; });
        Assert.Equal(SinkVerdict.Rejected, r.Verdict);
        Assert.Contains("Voicemeeter не запущен", r.Message);
        Assert.Equal(0, probes); // пробный поток на мёртвом VAIO сам мог бы повесить службу
    }

    [Fact]
    public void Vaio_with_voicemeeter_is_probed_and_marked_unreliable()
    {
        var procs = World.Procs([.. World.BaseProcs, new ProcInfo(500, 1, "voicemeeterpro.exe")]);
        int probes = 0;
        var r = SinkValidator.Check(Vaio.Id, Devices, procs, _ => { probes++; return Fine; });
        Assert.Equal(1, probes);
        Assert.Equal(SinkVerdict.Warning, r.Verdict);
        Assert.Contains("ненадёжная", r.Message);
    }

    [Fact]
    public void Probe_timeout_rejects_with_reason()
    {
        var r = SinkValidator.Check(CableIn.Id, Devices, World.Procs(),
            _ => new ProbeResult(ProbeOutcome.Timeout, "IAudioClient.Initialize не ответил за 2 с", 2));
        Assert.False(r.Usable);
        Assert.Contains("Initialize не ответил", r.Message);
    }

    [Fact]
    public void Stereo_sink_under_7_1_default_warns_about_level_drop()
    {
        // тон, открытый в 7.1 на устройстве по умолчанию, после переезда на стерео-заглушку стал тише на 13,5 дБ
        int? Ch(EndpointInfo e) => e.Id == Hp.Id ? 8 : 2;
        var r = SinkValidator.Check(CableIn.Id, Devices, World.Procs(), _ => Fine, Ch);
        Assert.Equal(SinkVerdict.Warning, r.Verdict);
        Assert.Contains("13,5 дБ", r.Message);
        Assert.Contains("8 кан", r.Message);

        Assert.Equal(SinkVerdict.Ok, SinkValidator.Check(CableIn.Id, Devices, World.Procs(), _ => Fine, _ => 2).Verdict);
        Assert.Equal(SinkVerdict.Ok, SinkValidator.Check(CableIn.Id, Devices, World.Procs(), _ => Fine, _ => null).Verdict);
    }

    [Fact]
    public void Rules_for_missing_capture_and_default_devices()
    {
        Assert.Equal(SinkVerdict.Rejected, SinkValidator.Check(null, Devices, World.Procs(), _ => Fine).Verdict);
        Assert.Equal(SinkVerdict.Rejected, SinkValidator.Check("{nope}", Devices, World.Procs(), _ => Fine).Verdict);
        Assert.Contains("воспроизведения", SinkValidator.Check(Mic.Id, Devices, World.Procs(), _ => Fine).Message);
        Assert.Equal(SinkVerdict.Warning, SinkValidator.Check(Hp.Id, Devices, World.Procs(), _ => Fine).Verdict);
        var ok = SinkValidator.Check(CableIn.Id, Devices, World.Procs(), _ => Fine);
        Assert.Equal((SinkVerdict.Ok, CableIn), (ok.Verdict, ok.Endpoint));
    }
}

public class SystemCaptureTests
{
    private static SystemPlan Plan(AppSnapshot w, params string[] exclude) => SystemCapture.Plan(w, exclude);

    [Fact]
    public void Parent_of_mixer_or_of_excluded_app_is_skipped_not_captured()
    {
        // explorer — родитель и микшера, и Discord: его дерево захватить нельзя (петля и двойной звук)
        var w = World.Make(null, World.Session(1), World.Session(101), World.Session(201));
        var p = Plan(w, "discord.exe");
        Assert.Equal([200u], p.Targets.Select(t => t.Pid));
        Assert.Equal("chrome.exe", p.Targets[0].Exe);
        Assert.Equal(["explorer.exe"], p.Skipped);
        Assert.Equal(["discord.exe"], p.Excluded);
    }

    [Fact]
    public void Lifting_to_the_app_root_stops_before_a_tree_with_an_excluded_app()
    {
        ProcInfo[] procs =
        [
            .. World.BaseProcs,
            new(400, 1, "launcher.exe"), new(401, 400, "launcher.exe"), new(402, 400, "game.exe"),
        ];
        var w = World.Make(procs, World.Session(401), World.Session(402));
        var p = Plan(w, "game.exe");
        Assert.Equal([401u], p.Targets.Select(t => t.Pid)); // корень 400 содержит игру со своим узлом
        Assert.Empty(p.Skipped);

        var all = Plan(w, "notrunning.exe");
        Assert.Equal([400u], all.Targets.Select(t => t.Pid)); // игра и лаунчер — одно дерево, один захват
        Assert.Empty(all.Excluded);
    }

    [Fact]
    public void Sessions_of_one_tree_give_one_capture_and_expired_or_dead_ones_none()
    {
        var w = World.Make(null, World.Session(200), World.Session(201), World.Session(202),
            World.Session(300, state: AudioSessionState.Expired), World.Session(777), World.Session(0));
        var p = Plan(w, "discord.exe");
        Assert.Equal([200u], p.Targets.Select(t => t.Pid));
    }

    [Fact]
    public void Too_many_captures_are_capped_with_active_first()
    {
        var procs = World.BaseProcs.ToList();
        var sessions = new List<AudioSessionInfo>();
        for (uint i = 0; i < SystemCapture.MaxCaptures + 5; i++)
        {
            procs.Add(new ProcInfo(5000 + i, 4, $"app{i}.exe")); // родитель 4 не существует — каждый сам себе корень
            sessions.Add(World.Session(5000 + i, state: i >= SystemCapture.MaxCaptures ? AudioSessionState.Active : AudioSessionState.Inactive));
        }
        var p = Plan(World.Make(procs, [.. sessions]), "discord.exe");
        Assert.Equal(SystemCapture.MaxCaptures, p.Targets.Count);
        Assert.All(Enumerable.Range(SystemCapture.MaxCaptures, 5), i => Assert.Contains(p.Targets, t => t.Pid == 5000 + i));
        Assert.Equal(5, p.Skipped.Count);
    }
}
