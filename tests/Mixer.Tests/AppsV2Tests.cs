using System.Windows;
using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Audio.Dsp;
using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;
using Mixer.Core.Sys;

namespace Mixer.Tests;

/// <summary>v2, этап 3: исключения и пропущенные в «Весь звук системы», прямые маршруты, громкость сессий.</summary>
public class SystemExclusionTests
{
    private static readonly EndpointInfo Hp = new(World.Headphones, "Headphones (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo Spk = new("{0.0.0.00000000}.{spk}", "Speakers (USB)", EDataFlow.Render, false);
    private static readonly List<EndpointInfo> Devices = [Hp, Spk];

    /// <summary>Мир с Telegram (свой корень) и лаунчером, который запустил Telegram и сам играет.</summary>
    private static readonly ProcInfo[] Procs =
    [
        .. World.BaseProcs,
        new(600, 1, "Telegram.exe"),
        new(700, 1, "launcher.exe"), new(701, 700, "telegram.exe"),
    ];

    private static AppSnapshot WorldWith(params AudioSessionInfo[] s) => World.Make(Procs, s);

    private static RuntimeContext Ctx(AppSnapshot w) => new()
    {
        SelfPid = World.Self,
        Roots = exe => w.Roots(exe),
        PlanSystem = exes => SystemCapture.Plan(w, exes),
    };

    private static NodeConfig Sys(params string[] exclude) =>
        new() { Id = "sys", Kind = NodeKind.System, Name = "Весь звук", Exclude = exclude.Length == 0 ? null : exclude.ToList() };
    private static NodeConfig App(string id, string exe) => new() { Id = id, Kind = NodeKind.App, Name = id, Exe = exe };
    private static NodeConfig Out(string id, EndpointInfo ep) => new() { Id = id, Kind = NodeKind.DeviceOutput, Name = id, EndpointId = ep.Id, EndpointName = ep.Name };

    [Fact]
    public void Excluded_app_without_own_node_is_not_captured_and_others_are()
    {
        // критерий приёмки: исключение telegram.exe без своего узла — Telegram в узел не попадает, остальные попадают
        var w = WorldWith(World.Session(600), World.Session(201), World.Session(102));
        var g = new GraphConfig { Nodes = [Sys("Telegram"), Out("hp", Hp)], Wires = [new() { From = "sys", To = "hp" }] };
        var r = GraphBuilder.Build(g, Devices, apps: Ctx(w));
        var plan = r.SystemPlans["sys"];
        Assert.Equal(["sys@100", "sys@200"], r.Graph.Sources.Select(s => s.Id).Order());
        Assert.DoesNotContain(r.Graph.Sources, s => s.ProcessId is 600 or 701);
        Assert.Equal(["telegram.exe"], plan.Excluded);
    }

    [Fact]
    public void Skipped_processes_carry_their_reason()
    {
        // explorer — предок микшера; лаунчер — предок исключённого Telegram; оба играют
        var w = WorldWith(World.Session(1), World.Session(700), World.Session(201));
        var p = SystemCapture.Plan(w, ["telegram.exe"]);
        Assert.Equal([200u], p.Targets.Select(t => t.Pid));
        Assert.Equal(
            [new SkippedProcess("explorer.exe", SkipReason.ContainsMixer), new SkippedProcess("launcher.exe", SkipReason.ContainsExcluded, "telegram.exe")],
            p.Skipped);

        string tip = MainViewModel.SkippedTooltip(p);
        Assert.Contains("explorer.exe: в его дереве процессов работает микшер", tip);
        Assert.Contains("launcher.exe: в его дереве процессов работает исключённое telegram.exe", tip);
        Assert.Contains("захватов уже 24", MainViewModel.SkippedTooltip(p with { Skipped = [new("x.exe", SkipReason.CaptureLimit)] }));
    }

    [Fact]
    public void Exclusions_combine_own_nodes_and_list_unless_own_nodes_are_kept()
    {
        Assert.Equal(["discord.exe", "telegram.exe"], GraphBuilder.SystemExclusions(Sys("Telegram", " "), ["discord.exe"]));
        var keep = Sys("telegram.exe") with { IncludeOwnNodes = true };
        Assert.Equal(["telegram.exe"], GraphBuilder.SystemExclusions(keep, ["discord.exe"]));
        Assert.Empty(GraphBuilder.SystemExclusions(Sys() with { IncludeOwnNodes = true }, ["discord.exe"]));
    }

    [Fact]
    public void Keeping_own_nodes_falls_back_to_one_capture_and_warns_on_a_shared_output()
    {
        var w = WorldWith(World.Session(102), World.Session(201));
        var sys = Sys() with { IncludeOwnNodes = true };
        var g = new GraphConfig
        {
            Nodes = [App("d", "discord"), sys, Out("hp", Hp), Out("spk", Spk)],
            Wires = [new() { From = "d", To = "spk" }, new() { From = "sys", To = "hp" }],
        };
        var r = GraphBuilder.Build(g, Devices, apps: Ctx(w));
        Assert.Contains(r.Graph.Sources, s => s is { Id: "sys", Kind: SourceKind.System });
        Assert.Empty(r.SystemPlans);
        Assert.DoesNotContain(r.Issues, i => i.Kind == IssueKind.DoubleSound); // разные выходы — так и задумано

        var same = g with { Wires = [new() { From = "d", To = "hp" }, new() { From = "sys", To = "hp" }] };
        Assert.Contains(GraphBuilder.Build(same, Devices, apps: Ctx(w)).Issues, i => i.Kind == IssueKind.DoubleSound && i.Subject == "sys");

        // со списком исключений — снова по захвату на процесс, но Discord в нём остаётся: тоже двойной звук
        var listed = same with { Nodes = [App("d", "discord"), sys with { Exclude = ["telegram.exe"] }, Out("hp", Hp), Out("spk", Spk)] };
        var lr = GraphBuilder.Build(listed, Devices, apps: Ctx(w));
        Assert.Contains(lr.Graph.Sources, s => s.Id == "sys@100");
        Assert.Contains(lr.Issues, i => i.Kind == IssueKind.DoubleSound);
    }

    [Fact]
    public void Exclusion_edits_normalize_and_are_structural()
    {
        var g = new GraphConfig { Nodes = [Sys()] };
        var a = GraphEdits.SetExcluded(g, "sys", "Telegram", true);
        Assert.Equal(["telegram.exe"], a.Nodes[0].Exclude);
        var b = GraphEdits.SetExcluded(a, "sys", "telegram.EXE", true);
        Assert.Equal(["telegram.exe"], b.Nodes[0].Exclude);
        Assert.Null(GraphEdits.SetExcluded(b, "sys", "telegram.exe", false).Nodes[0].Exclude);
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, a));
        // тот же список в другом экземпляре — движок не трогается
        Assert.True(GraphChange.Classify(a, a with { Nodes = [a.Nodes[0] with { Exclude = ["telegram.exe"] }] }) <= GraphChangeKind.Layout);
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, g with { Nodes = [g.Nodes[0] with { IncludeOwnNodes = true }] }));
    }

    [Fact]
    public void New_fields_survive_json()
    {
        var g = new GraphConfig
        {
            VolumeApps = ["firefox.exe"],
            Nodes = [Sys("telegram.exe") with { IncludeOwnNodes = true }, App("f", "firefox.exe") with { DirectRoute = false }],
        };
        var back = GraphSerializer.Deserialize(GraphSerializer.Serialize(g));
        Assert.Equal(["telegram.exe"], back.Nodes[0].Exclude);
        Assert.True(back.Nodes[0].IncludeOwnNodes);
        Assert.False(back.Nodes[1].DirectRoute);
        Assert.Equal(["firefox.exe"], back.VolumeApps);
    }
}

public class DirectRouteTests
{
    private static readonly EndpointInfo Hp = new(World.Headphones, "Headphones (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo Spk = new("{0.0.0.00000000}.{spk}", "Speakers (USB)", EDataFlow.Render, false);
    private static readonly EndpointInfo CableIn = new(World.Sink, "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly List<EndpointInfo> Devices = [Hp, Spk, CableIn];

    private static RuntimeContext Ctx(bool direct = true, string? sink = World.Sink, string? noLoopback = null) => new()
    {
        SelfPid = World.Self,
        Roots = exe => World.Procs().Roots(exe).Select(p => p.Pid).ToList(),
        ActiveSinkEndpointId = sink,
        DirectRoutes = direct,
        ProcessLoopbackUnavailable = noLoopback,
    };

    private static NodeConfig Chrome => new() { Id = "c", Kind = NodeKind.App, Name = "Chrome", Exe = "chrome.exe" };
    private static NodeConfig Out(string id, EndpointInfo ep) => new() { Id = id, Kind = NodeKind.DeviceOutput, Name = id, EndpointId = ep.Id, EndpointName = ep.Name };
    private static readonly NodeConfig Bus = new() { Id = "b", Kind = NodeKind.Bus, Name = "Шина" };
    private static readonly ChainSettings Comp = new() { Comp = new CompSettings { Enabled = true } };

    private static GraphConfig One(NodeConfig? app = null, NodeConfig? output = null, float wireDb = 0) => new()
    {
        Nodes = [app ?? Chrome, output ?? Out("hp", Hp), Out("spk", Spk), Bus],
        Wires = [new() { From = "c", To = "hp", GainDb = wireDb }],
    };

    [Fact]
    public void One_wire_to_an_output_goes_direct_without_capture()
    {
        var g = One(Chrome with { GainDb = -3 }, Out("hp", Hp) with { GainDb = -1, Muted = true }, wireDb: -2);
        var r = GraphBuilder.Build(g, Devices, apps: Ctx());
        var route = r.DirectRoutes["c"];
        Assert.Equal(("chrome.exe", "hp", Hp.Id, -6f, true), (route.Exe, route.OutputId, route.EndpointId, route.GainDb, route.Muted));
        Assert.Equal(0.501f, route.Level, 3);
        Assert.True(route.Attenuates);
        Assert.Empty(r.Graph.Sources);
        Assert.Empty(r.Graph.Wires);
        Assert.Empty(r.EngineSources["c"]);

        var unity = GraphBuilder.Build(One(), Devices, apps: Ctx()).DirectRoutes["c"];
        Assert.Equal(1f, unity.Level);
        Assert.False(unity.Attenuates);
    }

    public static TheoryData<string, GraphConfig, RuntimeContext> NotDirect => new()
    {
        { "второй провод", One() with { Wires = [new() { From = "c", To = "hp" }, new() { From = "c", To = "spk" }] }, Ctx() },
        { "провод в шину", One() with { Wires = [new() { From = "c", To = "b" }, new() { From = "b", To = "hp" }] }, Ctx() },
        { "обработка узла", One(Chrome with { Chain = Comp }), Ctx() },
        { "обработка выхода", One(output: Out("hp", Hp) with { Chain = Comp }), Ctx() },
        { "выше 0 дБ", One(Chrome with { GainDb = 1 }, wireDb: -0.5f), Ctx() },
        { "моно", One(Chrome with { Mono = true }), Ctx() },
        { "баланс", One(Chrome with { Balance = 0.3f }), Ctx() },
        { "выключен в узле", One(Chrome with { DirectRoute = false }), Ctx() },
        { "без «Забрать себе»", One(Chrome with { TakeOver = false }), Ctx() },
        { "привязки недоступны", One(), Ctx(direct: false) },
        { "выход отключён", One(output: Out("hp", Hp) with { EndpointId = "{gone}", EndpointName = "нет такого" }), Ctx() },
        { "выход — заглушка", One(output: Out("hp", CableIn)), Ctx() },
        { "провод выключен", One() with { Wires = [new() { From = "c", To = "hp", Enabled = false }] }, Ctx() },
    };

    [Theory]
    [MemberData(nameof(NotDirect))]
    public void Otherwise_the_node_stays_on_capture(string why, GraphConfig g, RuntimeContext ctx)
    {
        var r = GraphBuilder.Build(g, Devices, apps: ctx);
        Assert.False(r.DirectRoutes.ContainsKey("c"), why);
    }

    [Fact]
    public void A_disabled_second_wire_does_not_break_the_direct_route()
    {
        var g = One() with { Wires = [new() { From = "c", To = "hp" }, new() { From = "c", To = "spk", Enabled = false }] };
        Assert.True(GraphBuilder.Build(g, Devices, apps: Ctx()).DirectRoutes.ContainsKey("c"));
    }

    [Fact]
    public void Direct_route_needs_neither_a_running_app_nor_process_loopback()
    {
        var notRunning = One(Chrome with { Exe = "spotify.exe" });
        var r = GraphBuilder.Build(notRunning, Devices, apps: Ctx(sink: null));
        Assert.True(r.DirectRoutes.ContainsKey("c")); // привязка ставится при запуске; заглушка прямому маршруту не нужна
        Assert.Contains(r.Issues, i => i.Kind == IssueKind.WaitingProcess);

        // Windows 10 без process loopback: прямой маршрут работает, узел на захвате — нет
        var two = One() with { Nodes = [.. One().Nodes, Chrome with { Id = "d", Exe = "discord.exe" }], Wires = [new() { From = "c", To = "hp" }, new() { From = "d", To = "hp", GainDb = 3 }] };
        var w10 = GraphBuilder.Build(two, Devices, apps: Ctx(noLoopback: "нужна сборка 20348+"));
        Assert.True(w10.DirectRoutes.ContainsKey("c"));
        Assert.Equal(["d"], w10.Issues.Where(i => i.Kind == IssueKind.Unsupported).Select(i => i.Subject));
    }

    [Fact]
    public void Direct_route_switch_is_structural()
    {
        var g = One();
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(g, g with { Nodes = [Chrome with { DirectRoute = false }, .. g.Nodes.Skip(1)] }));
    }
}

public class DirectBindingTests
{
    private readonly FakePolicy _policy = new(() => World.Procs());
    private readonly FakeJournal _journal = new();
    private readonly TakeOverManager _mgr;
    private static readonly AppSnapshot W = World.Make(null, World.Session(102), World.Session(201));

    public DirectBindingTests() => _mgr = new TakeOverManager(_policy, _journal);

    private static Dictionary<string, string?> Want(params (string Exe, string? Target)[] x) => x.ToDictionary(p => p.Exe, p => p.Target);

    [Fact]
    public void Each_app_is_bound_to_its_own_target()
    {
        _mgr.Reconcile(Want(("chrome", World.Headphones), ("discord", World.Sink)), W);
        Assert.Equal(World.Headphones, _policy.Bindings[("chrome.exe", ERole.Console)]);
        Assert.Equal(World.Sink, _policy.Bindings[("discord.exe", ERole.Multimedia)]);
        Assert.Equal(World.Headphones, _mgr.Status["chrome.exe"].Target);
        Assert.Equal(["chrome.exe", "discord.exe"], _journal.List.Order()); // прямой маршрут — тоже привязка: в журнал до Set

        // уход с прямого маршрута: цель сменилась — ставится заново
        int before = _policy.Sets.Count;
        _mgr.Reconcile(Want(("chrome", World.Sink), ("discord", World.Sink)), W);
        Assert.Equal(World.Sink, _policy.Bindings[("chrome.exe", ERole.Communications)]);
        Assert.True(_policy.Sets.Count > before);
    }

    [Fact]
    public void Partial_reconcile_binds_without_releasing_or_clearing_status()
    {
        _mgr.Reconcile(Want(("discord", World.Sink)), W);
        _mgr.Reconcile(Want(("chrome", World.Headphones)), W, partial: true);
        Assert.True(_policy.IsBound("discord.exe")); // не снята, хотя её нет в частичном наборе
        Assert.True(_policy.IsBound("chrome.exe"));
        Assert.Contains("discord.exe", _mgr.Status.Keys);

        _mgr.Reconcile(Want(("chrome", World.Headphones)), W); // полное сведение снимает лишнее
        Assert.False(_policy.IsBound("discord.exe"));
    }

    [Fact]
    public void Plays_direct_is_measured_against_the_target()
    {
        var w = World.Make(null, World.Session(201, ep: World.Headphones));
        _mgr.Reconcile(Want(("chrome", World.Headphones)), w);
        Assert.False(_mgr.Status["chrome.exe"].PlaysDirect);
        _mgr.Reconcile(Want(("chrome", World.Sink)), w);
        Assert.True(_mgr.Status["chrome.exe"].PlaysDirect); // сессия ещё в наушниках — перезапустить воспроизведение
    }
}

internal sealed class FakeSession(uint pid, string ep) : IAppSession
{
    public uint Pid { get; } = pid;
    public string EndpointId { get; } = ep;
    public float Level = 1;
    public bool Muted;
    public (float, float) Peak = (0.5f, 0.25f);
    public bool Disposed;
    public Action? OnSet;
    public (float Level, bool Muted)? Volume => (Level, Muted);

    public int SetVolume(float level, bool muted)
    {
        OnSet?.Invoke();
        Level = level;
        Muted = muted;
        return 0;
    }

    public (float L, float R) Peaks() => Peak;
    public void Dispose() => Disposed = true;
}

public class DirectVolumeTests
{
    private readonly FakeJournal _journal = new();
    private readonly Dictionary<string, List<FakeSession>> _sessions = new();
    private readonly DirectVolumes _dv;
    private AppSnapshot _world = World.Make(null, World.Session(201));
    private static readonly string[] Render = [World.Headphones, World.Sink];

    public DirectVolumeTests()
    {
        _sessions[World.Headphones] = [new FakeSession(201, World.Headphones), new FakeSession(102, World.Headphones)];
        _sessions[World.Sink] = [new FakeSession(201, World.Sink)];
        _dv = new DirectVolumes((ep, filter) => _sessions.GetValueOrDefault(ep, []).Where(s => filter(s.Pid)).ToList(), _journal);
    }

    private static DirectRoute Route(float db = -6, bool muted = false) => new("c", "chrome.exe", "hp", World.Headphones, db, muted);

    private void Update(params DirectRoute[] routes) => _dv.Update(routes.ToDictionary(r => r.NodeId), _world, reopen: true, Render);

    [Fact]
    public void Lowered_level_is_journaled_before_the_session_volume_changes()
    {
        var chrome = _sessions[World.Headphones][0];
        chrome.OnSet = () => Assert.Contains("chrome.exe", _journal.List);
        Update(Route(-6, muted: true));
        Assert.Equal((0.501f, true), (MathF.Round(chrome.Level, 3), chrome.Muted));
        Assert.Equal(1f, _sessions[World.Headphones][1].Level); // Discord не трогаем
        Assert.Equal(1f, _sessions[World.Sink][0].Level);       // и сессию Chrome на другом устройстве тоже
        Assert.Equal(1, _dv.SessionCounts["c"]);
    }

    [Fact]
    public void Leaving_the_direct_route_restores_full_volume_and_clears_the_journal()
    {
        Update(Route(-12));
        var chrome = _sessions[World.Headphones][0];
        Update();
        Assert.Equal((1f, false), (chrome.Level, chrome.Muted));
        Assert.True(chrome.Disposed);
        Assert.Empty(_journal.List);
        Assert.False(_dv.Any);
    }

    [Fact]
    public void Journal_left_by_a_crash_is_restored_once_the_app_runs()
    {
        _journal.List.Add("chrome.exe");
        var chrome = _sessions[World.Headphones][0];
        chrome.Level = 0.25f; // осталось от прошлого запуска
        _world = World.Make(World.BaseProcs.Where(p => !p.Exe.StartsWith("chrome")).ToArray());
        Update();
        Assert.Equal(["chrome.exe"], _journal.List); // не запущен — ждёт
        _world = World.Make(null, World.Session(201));
        Update();
        Assert.Equal(1f, chrome.Level);
        Assert.Empty(_journal.List);
    }

    [Fact]
    public void Without_a_journal_the_volume_is_not_touched()
    {
        _journal.Fail = true;
        Update(Route(-6));
        Assert.Equal(1f, _sessions[World.Headphones][0].Level);
        Assert.Contains("chrome.exe", _dv.Errors.Keys);
    }

    [Fact]
    public void Meter_follows_session_peaks_after_the_level()
    {
        Update(Route(-6));
        _dv.PollMeters();
        var (l, r) = _dv.Meters["c"].Take();
        float level = MathF.Pow(10, -6 / 20f);
        Assert.Equal(0.5f * level, l, 1e-5f);
        Assert.Equal(0.25f * level, r, 1e-5f);

        Update(Route(-6, muted: true));
        _dv.PollMeters();
        Assert.Equal((0f, 0f), _dv.Meters["c"].Take());
    }

    [Fact]
    public void Exit_restores_everything()
    {
        Update(Route(-6));
        _dv.RestoreAll(_world, Render);
        Assert.Equal(1f, _sessions[World.Headphones][0].Level);
        Assert.Empty(_journal.List);
    }
}

public class SinkCableTests
{
    private static readonly EndpointInfo CableIn = new(World.Sink, "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo Cable16 = new("{0.0.0.00000000}.{c16}", "CABLE In 16ch (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo CableA = new("{0.0.0.00000000}.{ca}", "CABLE-A Input (VB-Audio Cable A)", EDataFlow.Render, false);
    private static readonly List<EndpointInfo> Devices = [CableIn, Cable16, CableA];

    [Fact]
    public void Output_on_the_other_input_of_the_sink_cable_is_a_conflict()
    {
        // проверка 11 ноября 2026: пока открыт CABLE In 16ch, тон, перенесённый в CABLE Input, обрывался
        NodeConfig Out(string id, EndpointInfo ep) => new() { Id = id, Kind = NodeKind.DeviceOutput, Name = id, EndpointId = ep.Id, EndpointName = ep.Name };
        var g = new GraphConfig
        {
            Nodes = [new() { Id = "f", Kind = NodeKind.App, Name = "Firefox", Exe = "firefox.exe" }, Out("c16", Cable16), Out("ca", CableA)],
        };
        var r = GraphBuilder.Build(g, Devices, apps: new RuntimeContext { ActiveSinkEndpointId = CableIn.Id });
        var issue = Assert.Single(r.Issues, i => i.Kind == IssueKind.SinkConflict);
        Assert.Equal("c16", issue.Subject);
        Assert.Contains("два входа одного виртуального кабеля", issue.Message);
        Assert.True(FeedbackRules.SameCable(Cable16, CableIn));
        Assert.False(FeedbackRules.SameCable(CableA, CableIn)); // Cable A — другой кабель
        Assert.False(FeedbackRules.SameCable(CableIn, CableIn));
    }
}

public class TrimTipTests
{
    [Fact]
    public void Tooltip_shows_the_full_text_only_when_it_is_cut()
    {
        Assert.Equal("Весь звук системы — всё, кроме Discord", TrimTip.Compose("Весь звук системы — всё, кроме Discord", trimmed: true, extra: null));
        Assert.Equal("длинное имя\n\ndiscord.exe", TrimTip.Compose("длинное имя", trimmed: true, extra: "discord.exe"));
        Assert.Equal("discord.exe", TrimTip.Compose("имя", trimmed: false, extra: "discord.exe"));
        Assert.Null(TrimTip.Compose("имя", trimmed: false, extra: null));
        Assert.Equal("CABLE Input", TrimTip.Compose("CABLE Input", trimmed: true, extra: "CABLE Input")); // без повтора
    }

    [Fact]
    public void Trimming_is_measured_against_the_actual_width()
    {
        WpfThread.Run(() =>
        {
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = "Потяните от точки к другому узлу, чтобы соединить. Ctrl + клик по источникам — выделить их",
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12,
            };
            tb.Measure(new Size(120, 20));
            tb.Arrange(new Rect(0, 0, 120, 20));
            Assert.True(TrimTip.IsTrimmed(tb));
            tb.Measure(new Size(2000, 20));
            tb.Arrange(new Rect(0, 0, 2000, 20));
            Assert.False(TrimTip.IsTrimmed(tb));
        });
    }
}
