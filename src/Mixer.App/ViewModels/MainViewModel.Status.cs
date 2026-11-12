using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Mixer.App.Ui;
using Mixer.Audio.Engine;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Graph;
using Mixer.Core.Host;
using Mixer.Core.Sys;

namespace Mixer.App.ViewModels;

/// <summary>Опрос хоста (4 раза в секунду) и индикаторы (30 кадров в секунду).</summary>
public sealed partial class MainViewModel
{
    private readonly Process _self = Process.GetCurrentProcess();
    private TimeSpan _cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
    private long _cpuAt = Environment.TickCount64;
    private int _pollCount;
    private bool _pollQueued;
    private double _now;

    // ── шапка и подвал ──
    [ObservableProperty] private string _latencyText = Strings.Header_LatencyNone;
    [ObservableProperty] private string _latencyTip = "";
    [ObservableProperty] private string _cpuText = Strings.Header_CpuNone;
    [ObservableProperty] private string _failuresText = Fmt.F(Strings.Header_DropoutsFormat, 0);
    [ObservableProperty] private string _failuresTip = Strings.Header_DropoutsTipInitial;
    [ObservableProperty] private bool _hasFailures;
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private string _cableText = "";
    [ObservableProperty] private string _cableTip = "";
    [ObservableProperty] private string _systemText = "";
    [ObservableProperty] private string _systemTip = "";
    [ObservableProperty] private bool _systemWarn;

    private void PollSoon()
    {
        if (_pollQueued) return;
        _pollQueued = true;
        // хосту нужно время применить граф; повтор через 150 мс покажет новое состояние без ожидания такта
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _pollQueued = false;
            Poll();
        };
        t.Start();
    }

    private void UpdateCounts()
    {
        int src = Nodes.Count(n => n.IsSource), buses = Nodes.Count(n => n.IsBus), outs = Nodes.Count(n => n.IsOutput), off = Wires.Count(w => !w.Enabled);
        int direct = Wires.Count(x => x.ShowDirect);
        string s = Fmt.Plural(src, Strings.Plural_Source), o = Fmt.Plural(outs, Strings.Plural_Output), w = Fmt.Plural(Wires.Count, Strings.Plural_Wire);
        if (direct > 0) w = Fmt.F(Strings.Status_WiresDirectFormat, w, direct);
        if (buses > 0) s += " · " + Fmt.Plural(buses, Strings.Plural_Bus);
        CountsText = off > 0
            ? Fmt.F(Strings.Status_SummaryOffFormat, s, o, w, Fmt.Plural(off, Strings.Plural_WireOff))
            : Fmt.F(Strings.Status_SummaryFormat, s, o, w);
    }

    /// <summary>Состояние узлов и проводов из хоста и движка. Только чтение снимков: без блокировок и COM.</summary>
    private void Poll()
    {
        _pollCount++;
        var st = _host?.Status;
        var snap = _host?.Engine.Snapshot ?? EngineSnapshot.Empty;
        var build = st?.Build;
        var devices = Devices;
        var issues = build?.Issues ?? [];

        foreach (var n in Nodes) PollNode(n, st, snap, build, issues);
        if (_host is not null) WatchDevices();
        var paths = devices.Count > 0 ? new GraphPaths(Graph) : null;
        int directBefore = Wires.Count(x => x.ShowDirect);
        foreach (var w in Wires) PollWire(w, snap, devices, paths, build);
        if (Wires.Count(x => x.ShowDirect) != directBefore) UpdateCounts();

        // ── шапка ──
        double latency = 0;
        int failures = 0, under = 0, over = 0, resync = 0;
        foreach (var w in snap.Wires.Values)
        {
            under += w.Underruns;
            over += w.Overruns;
            resync += w.Resyncs;
            if (w.State != WireState.Running) continue;
            var s = snap.Sources.GetValueOrDefault(w.FromId);
            var o = snap.Outputs.GetValueOrDefault(w.OutputId);
            latency = Math.Max(latency, (s?.CaptureLatencyMs ?? 0) + w.LatencyMs + (o?.RenderLatencyMs ?? 0));
        }
        failures = under + over + resync;
        LatencyText = latency > 0 ? Fmt.F(Strings.Header_LatencyFormat, Fmt.Ms(latency)) : Strings.Header_LatencyNone;
        LatencyTip = Strings.Header_LatencyTip;
        FailuresText = Fmt.F(Strings.Header_DropoutsFormat, failures);
        HasFailures = failures > 0;
        FailuresTip = failures == 0
            ? Strings.Header_DropoutsTipNone
            : Fmt.F(Strings.Header_DropoutsTipFormat, under, over, resync);

        Processing?.Poll();
        if (_pollCount % 4 == 1) PollCpu();
        if (_pollCount % 8 == 1) PollFooter(st, devices);
    }

    /// <summary>Узлы, чьё устройство сейчас отключено (для уведомлений «отключено» / «снова подключено»).</summary>
    private readonly HashSet<string> _lostDevices = new();
    private bool _devicesWatched;

    /// <summary>Устройство узла пропало (true) или вернулось (false): имя узла.</summary>
    public event Action<string, bool>? DeviceAvailabilityChanged;

    private void WatchDevices()
    {
        foreach (var n in Nodes)
        {
            bool lost = n.HasDevice && n.Look == NodeLook.Waiting && n.Subtitle == Strings.NodeSub_DeviceDisconnected;
            if (lost && _lostDevices.Add(n.Id))
            {
                if (_devicesWatched) DeviceAvailabilityChanged?.Invoke(n.Name, true);
            }
            else if (!lost && n.Look == NodeLook.Normal && _lostDevices.Remove(n.Id))
            {
                DeviceAvailabilityChanged?.Invoke(n.Name, false);
            }
        }
        _lostDevices.RemoveWhere(id => FindNode(id) is null);
        // первый опрос после запуска: устройства, которых нет с самого начала, — не новость
        _devicesWatched = _pollCount > 8;
    }

    private void PollCpu()
    {
        _self.Refresh();
        var cpu = _self.TotalProcessorTime;
        long now = Environment.TickCount64;
        double dt = Math.Max(1, now - _cpuAt) / 1000.0;
        double pct = (cpu - _cpuBefore).TotalSeconds / dt / Environment.ProcessorCount * 100;
        _cpuBefore = cpu;
        _cpuAt = now;
        CpuText = Fmt.F(Strings.Header_CpuFormat, Fmt.Percent(pct));
    }

    private void PollFooter(HostStatus? st, IReadOnlyList<Mixer.Audio.Interop.EndpointInfo> devices)
    {
        (CableText, CableTip) = CableFooter(devices);
        string win = Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
        if (_host is null)
        {
            SystemText = Fmt.F(Strings.Status_NoAudioFormat, win);
            SystemTip = Strings.Status_NoAudioTip;
            SystemWarn = true;
            return;
        }
        string? pl = st?.ProcessLoopbackError;
        SystemText = Fmt.F(pl is null ? Strings.Status_AppCaptureAvailableFormat : Strings.Status_AppCaptureUnavailableFormat, win);
        SystemTip = pl ?? (st?.PolicyError is { } pe
            ? Fmt.F(Strings.Status_TakeOverUnavailableFormat, pe)
            : Strings.Status_AppCaptureWorks);
        SystemWarn = pl is not null || st?.PolicyError is not null;
        if (st?.DeviceWatchError is { } dw)
            SystemTip += "\n\n" + Fmt.F(Strings.Status_DeviceWatchFailedFormat, dw);
    }

    /// <summary>
    /// Подвал: «Кабели: CABLE-A (Микрофон стрима) · CABLE-B свободен» — кабели среди устройств и узлы, которые ими
    /// пользуются. Кабелей нет — подсказка, где их взять.
    /// </summary>
    public (string Text, string Tip) CableFooter(IReadOnlyList<Mixer.Audio.Interop.EndpointInfo> devices)
    {
        if (devices.Count == 0) return ("", "");
        var pairs = Mixer.Core.Cables.CableTable.Default.Pairs(devices).Where(p => p.Complete).ToList();
        if (pairs.Count == 0)
        {
            bool any = devices.Any(d => FeedbackRules.IsVirtualCable(d.Name));
            return any ? (Strings.Status_VbCableFound, Strings.Status_VbCableFoundTip) : (Strings.Status_VbCableMissing, Strings.Status_VbCableMissingTip);
        }
        var table = Mixer.Core.Cables.CableTable.Default;
        var parts = pairs.Select(p =>
        {
            var users = Graph.Nodes.Where(n => table.LinkOf(n) is { } l && l.PairId == p.Id).Select(n => n.Name).Distinct().ToList();
            return users.Count == 0 ? Fmt.F(Strings.Status_CableFreeFormat, p.Name) : Fmt.F(Strings.Status_CableUsedFormat, p.Name, string.Join(", ", users));
        });
        string tip = Strings.Status_CablesTip + "\n\n" + string.Join("\n", pairs.Select(p => $"{p.Label}: {p.Input!.Name} → {p.Output!.Name}"));
        return (Fmt.F(Strings.Status_CablesFormat, string.Join(" · ", parts)), tip);
    }

    /// <summary>Подсказка «Микрофон для: …»: состояние назначения у каждого приложения и напоминание про «По умолчанию».</summary>
    private static string MicForTooltip(NodeConfig c, HostStatus? st)
    {
        var sb = new StringBuilder(Strings.MicTip_Header);
        foreach (string app in c.MicFor ?? [])
        {
            string exe = ProcessSnapshot.NormalizeExe(app);
            string state = st?.MicBindings.GetValueOrDefault(exe) is not { } b ? Strings.MicState_NotRunning : b.State switch
            {
                BindingState.Bound => Strings.MicState_Bound,
                BindingState.NotRunning => Strings.MicState_NotRunning,
                BindingState.WaitingAudio => Strings.MicState_Waiting,
                BindingState.Unavailable => Fmt.F(Strings.MicState_UnavailableFormat, b.Message),
                _ => Fmt.F(Strings.MicState_FailedFormat, b.Message),
            };
            sb.Append("\n• ").Append(Fmt.F(Strings.MicTip_RowFormat, Mixer.Core.Cables.AppNames.Friendly(exe), state));
        }
        sb.Append("\n\n").Append(Strings.MicTip_Note);
        return sb.ToString();
    }

    private static string StripSubject(string message, string name)
    {
        string prefix = Mixer.Core.Loc.SubjectPrefix(name);
        return message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;
    }

    private static string Capitalize(string s) => Fmt.Capitalize(s);

    private void PollNode(NodeViewModel n, HostStatus? st, EngineSnapshot snap, BuildResult? build, IReadOnlyList<GraphIssue> issues)
    {
        var c = n.Config;
        var direct = build?.DirectRoutes.GetValueOrDefault(n.Id);
        n.IsDirect = direct is not null && _host is not null;
        n.DirectMeter = direct is not null ? st?.DirectMeters.GetValueOrDefault(n.Id) : null;
        // узлы движка
        if (n.IsSource)
        {
            var ids = build?.EngineSources.GetValueOrDefault(n.Id) ?? [];
            var list = new List<AudioNode>(ids.Count);
            foreach (string id in ids)
                if (snap.Sources.TryGetValue(id, out var s)) list.Add(s);
            if (!SameNodes(n.EngineNodes, list)) n.EngineNodes = list.ToArray();
            n.EngineOutput = null;
        }
        else if (n.IsOutput)
        {
            var o = snap.Outputs.GetValueOrDefault(n.Id);
            n.EngineOutput = o;
            if (n.EngineNodes.Length != (o is null ? 0 : 1) || o is not null && n.EngineNodes[0] != o)
                n.EngineNodes = o is null ? [] : [o];
        }
        // шина (и неявная шина обработки у приложения): экземпляры по выходам
        string? busId = build?.EngineBuses.GetValueOrDefault(n.Id);
        var buses = new List<BusInstance>();
        if (busId is not null)
            foreach (var (key, bus) in snap.Buses)
                if (key.Bus == busId) buses.Add(bus);
        if (buses.Count != n.EngineBuses.Length || buses.Where((b, i) => b != n.EngineBuses[i]).Any()) n.EngineBuses = buses.ToArray();
        var chains = busId is not null ? n.EngineBuses.Select(b => b.Chain) : n.EngineNodes.Select(e => e.Chain);
        if (!chains.SequenceEqual(n.EngineChains)) n.EngineChains = chains.ToArray();
        n.ReductionText = n.HasComp && n.ChainMeter.ReductionDb >= 0.5f ? Fmt.Pill(-Math.Round(n.ChainMeter.ReductionDb)) : null;

        GraphIssue? wait = null, error = null;
        var warnings = new List<string>();
        foreach (var i in issues)
        {
            if (i.Subject != n.Id) continue;
            switch (i.Kind)
            {
                case IssueKind.WaitingDevice or IssueKind.WaitingProcess: wait ??= i; break;
                case IssueKind.Unsupported or IssueKind.Invalid: error ??= i; break;
                case IssueKind.DoubleSound or IssueKind.SinkConflict: warnings.Add(StripSubject(i.Message, c.Name)); break;
            }
        }
        var engineError = n.EngineNodes.FirstOrDefault(e => e.State == NodeState.Error);
        var engineWait = n.EngineNodes.FirstOrDefault(e => e.State == NodeState.WaitingDevice);
        var format = n.EngineNodes.Select(e => e.Format).FirstOrDefault(f => f is not null);

        // узел «по умолчанию»: какое устройство сейчас за ним стоит
        var current = c.FollowsDefault ? GraphBuilder.Resolve(c, Devices) : null;
        var cable = n.Cable is not null ? Mixer.Core.Cables.CableTable.Default.PairOf(c, Devices) : null;
        string sub = BaseSubtitle(n, format?.SampleRate, current?.Name, cable?.Name);
        bool warn = false;
        // кабель: проверки хоста и назначения микрофона
        if (st?.CableWarnings.GetValueOrDefault(n.Id) is { Count: > 0 } cableWarnings)
        {
            warn = true;
            warnings.AddRange(cableWarnings);
        }
        if (n.IsVirtualMic && c.MicFor is { Count: > 0 } micFor)
        {
            n.MicForTip = MicForTooltip(c, st);
            foreach (string app in micFor)
            {
                if (st?.MicBindings.GetValueOrDefault(ProcessSnapshot.NormalizeExe(app)) is not { State: BindingState.Failed or BindingState.Unavailable } b) continue;
                warn = true;
                warnings.Add(Fmt.F(Strings.MicTip_RowFormat, Mixer.Core.Cables.AppNames.Friendly(app),
                    Fmt.F(b.State == BindingState.Failed ? Strings.MicState_FailedFormat : Strings.MicState_UnavailableFormat, b.Message)));
            }
        }
        else n.MicForTip = null;
        if (c.Kind == NodeKind.App && st is not null)
        {
            string exe = ProcessSnapshot.NormalizeExe(c.Exe ?? "");
            if (n.TakeOver && st.Bindings.TryGetValue(exe, out var b))
            {
                switch (b.State)
                {
                    case BindingState.Bound when b.PlaysDirect:
                        sub = Strings.NodeSub_RestartPlayback;
                        warn = true;
                        warnings.Add(Strings.NodeWarn_PlaysDirect);
                        break;
                    case BindingState.Bound when direct is not null:
                        string to = Devices.FirstOrDefault(d => string.Equals(d.Id, direct.EndpointId, StringComparison.OrdinalIgnoreCase))?.Name
                                    ?? Nodes.FirstOrDefault(x => x.Id == direct.OutputId)?.Name ?? "";
                        sub = Fmt.F(Strings.NodeSub_AppDirectFormat, Fmt.ShortDevice(to));
                        break;
                    case BindingState.Bound:
                        sub = Strings.NodeSub_AppTakenOver;
                        break;
                    case BindingState.WaitingAudio:
                        sub = Strings.NodeSub_AppWaitingAudio;
                        break;
                    case BindingState.Failed:
                        sub = Strings.NodeSub_TakeOverFailed;
                        warn = true;
                        warnings.Add(MixerHost.Describe(b));
                        break;
                    case BindingState.Unavailable:
                        sub = Strings.NodeSub_TakeOverUnavailable;
                        warn = true;
                        warnings.Add(Fmt.F(Strings.NodeWarn_SetSinkFormat, Capitalize(b.Message ?? Strings.NodeWarn_NoSink)));
                        break;
                }
            }
            if (direct is not null && st.DirectErrors.TryGetValue(exe, out string? volumeError))
            {
                warn = true;
                warnings.Add(Capitalize(volumeError));
            }
        }
        if (c.Muted && error is null && wait is null) sub = Strings.NodeSub_Muted;
        else if (n.PushMuted is { } pm && error is null && wait is null)
        {
            bool ptt = Graph.Hotkeys.Any(h => h.Target == n.Id && h.Action == HotkeyAction.PushToTalk);
            sub = ptt ? (pm ? Strings.NodeSub_PushToTalkOff : Strings.NodeSub_PushToTalkOn) : Strings.NodeSub_PushToMuteOn;
        }
        var lowLatency = c.LowLatencyOn ? n.EngineNodes.FirstOrDefault(e => e.State == NodeState.Running)?.LowLatency : null;
        if (lowLatency is { Status: LowLatencyStatus.FellBack } fb)
        {
            warn = true;
            warnings.Add(Fmt.F(Strings.NodeWarn_LowLatencyFellBackFormat, GlitchWindow.Limit, Fmt.Millis(fb.DefaultMs)));
        }

        if (n.IsBus)
        {
            int inputs = Wires.Count(w => w.Target == n);
            sub = c.Muted ? Strings.NodeSub_Muted : Fmt.F(Strings.NodeSub_BusFormat, Fmt.Plural(inputs, Strings.Plural_Input));
        }

        if (_host is null || n.IsBus)
        {
            n.Look = NodeLook.Normal;
            n.Message = null;
        }
        else if (error is not null || engineError is not null)
        {
            n.Look = NodeLook.Error;
            bool device = engineError is not null && error is null;
            sub = device ? Strings.NodeSub_DeviceError : Strings.NodeSub_Error;
            warn = true;
            string text = Capitalize(error is not null ? StripSubject(error.Message, c.Name) : engineError!.Message ?? Strings.NodeMsg_UnknownError);
            n.Message = device ? Fmt.F(Strings.NodeMsg_RetryFormat, text) : text;
        }
        else if (wait is not null || engineWait is not null)
        {
            n.Look = NodeLook.Waiting;
            bool app = c.Kind == NodeKind.App;
            string detail = wait is not null ? StripSubject(wait.Message, c.Name) : engineWait!.Message ?? "";
            bool vm = detail.Contains("Voicemeeter", StringComparison.Ordinal);
            bool busy = wait is null && detail == AudioNode.BusyMessage;
            bool noDefault = wait is not null && c.FollowsDefault;
            sub = app ? Fmt.F(Strings.NodeSub_WaitingForAppFormat, ProcessSnapshot.NormalizeExe(c.Exe ?? ""))
                : vm ? Strings.NodeSub_WaitingVoicemeeter
                : busy ? Strings.NodeSub_DeviceBusy
                : noDefault ? Strings.NodeSub_NoDefaultDevice
                : Strings.NodeSub_DeviceDisconnected;
            string reconnect = app ? Strings.NodeMsg_ReconnectApp
                : vm ? ""
                : busy ? Strings.NodeMsg_ReconnectBusy
                : Strings.NodeMsg_ReconnectDevice;
            n.Message = string.Join(" ", new[]
            {
                vm || busy || noDefault ? Fmt.F(Strings.NodeMsg_SentenceFormat, Capitalize(detail)) : "",
                Strings.NodeMsg_Kept,
                reconnect,
            }.Where(p => p.Length > 0));
        }
        else
        {
            n.Look = NodeLook.Normal;
            n.Message = null;
        }

        n.Subtitle = sub;
        n.SubtitleWarn = warn && n.Look == NodeLook.Normal;
        n.Warnings = warnings.Count == 0 ? null : string.Join("\n\n", warnings.Distinct());
        n.LimiterText = n.Meter.ReductionDb > 0.1f ? Fmt.F(Strings.NodeState_LimiterFormat, Fmt.Term(n.Meter.ReductionDb)) : null;
        var plan = c.Kind == NodeKind.System ? build?.SystemPlans.GetValueOrDefault(n.Id) : null;
        if (plan is not null && n.Look == NodeLook.Normal && !c.Muted && !warn && SystemExceptNames(c) is { Count: > 0 } except)
            n.Subtitle = Fmt.F(Strings.NodeSub_SystemExceptFormat, string.Join(", ", except));
        if (plan is { Skipped.Count: > 0 } && _host is not null)
        {
            n.SkippedText = Fmt.F(Strings.Node_SkippedFormat, plan.Skipped.Count);
            n.SkippedTip = SkippedTooltip(plan);
        }
        else
        {
            n.SkippedText = null;
            n.SkippedTip = null;
        }
        n.Tooltip = c.Kind switch
        {
            NodeKind.App => n.EngineNodes.Length > 1
                ? Fmt.F(Strings.NodeTip_AppInstancesFormat, ProcessSnapshot.NormalizeExe(c.Exe ?? ""), Fmt.Plural(n.EngineNodes.Length, Strings.Plural_Instance))
                : ProcessSnapshot.NormalizeExe(c.Exe ?? ""),
            NodeKind.System when plan is not null => SystemTooltip(c, plan),
            NodeKind.System => Strings.NodeTip_System,
            NodeKind.Bus => n.IsSilent ? Strings.NodeTip_BusSilent : Strings.NodeTip_Bus,
            _ when c.DefaultRole is { } role => Fmt.F(Strings.NodeTip_DefaultFormat,
                role == DefaultRole.Communications ? Strings.NodeTip_DefaultCommsDevice : Strings.NodeTip_DefaultDevice,
                current is null ? Strings.NodeTip_DefaultNowNone : Fmt.F(Strings.NodeTip_DefaultNowFormat, current.Name)),
            _ when cable is { Input: { } ci, Output: { } co } && n.Cable is { } link =>
                Fmt.F(Strings.NodeTip_CableFormat, c.EndpointName, cable.Name, ci.Name, co.Name) + "\n\n"
                + (link.Role == CableRole.VirtualMic ? Strings.NodeTip_CableVirtualMic : Strings.NodeTip_CableVirtualInput),
            _ => c.EndpointName,
        };
        if (LowLatencyTip(lowLatency) is { } llTip) n.Tooltip = string.IsNullOrEmpty(n.Tooltip) ? llTip : n.Tooltip + "\n\n" + llTip;

        if (c.Kind == NodeKind.App && n.AppIcon is null && _now >= n.NextIconTry)
        {
            n.NextIconTry = _now + 5;
            n.AppIcon = AppIcons.ForExe(ProcessSnapshot.NormalizeExe(c.Exe ?? ""));
        }
    }

    /// <summary>Строка подсказки узла о «Низкой задержке» (null — режим не просили или сказать нечего).</summary>
    public static string? LowLatencyTip(LowLatencyInfo? i) => i?.Status switch
    {
        LowLatencyStatus.Active => Fmt.F(Strings.NodeTip_LowLatencyActiveFormat, Fmt.Millis(i.PeriodMs), Fmt.Millis(i.DefaultMs)),
        LowLatencyStatus.Unsupported => Fmt.F(Strings.NodeTip_LowLatencyUnsupportedFormat, Fmt.Millis(i.MinMs)),
        LowLatencyStatus.Unavailable => Fmt.F(Strings.NodeTip_LowLatencyUnavailableFormat, i.Error),
        _ => null, // откат показывается предупреждением
    };

    /// <summary>Кого «Весь звук системы» не слышит по настройке: узлы приложений (если их не оставили) и исключённые exe.</summary>
    private List<string> SystemExceptNames(NodeConfig sys)
    {
        var names = new List<string>();
        if (sys.IncludeOwnNodes != true) names.AddRange(Nodes.Where(x => x.Kind == NodeKind.App).Select(x => x.Name));
        names.AddRange(sys.Exclude ?? []);
        return names.Distinct().ToList();
    }

    private string SystemTooltip(NodeConfig sys, SystemPlan plan)
    {
        var sb = new StringBuilder();
        var apps = Nodes.Where(x => x.Kind == NodeKind.App).Select(x => x.Name).Distinct().ToList();
        if (sys.IncludeOwnNodes == true) sb.Append(Strings.NodeTip_System).Append(' ').Append(Strings.NodeTip_SystemIncludesOwnNodes);
        else if (apps.Count > 0) sb.Append(Fmt.F(Strings.NodeTip_SystemPlanFormat, string.Join(", ", apps)));
        else sb.Append(Strings.NodeTip_System);
        if (sys.Exclude is { Count: > 0 } ex) sb.Append("\n\n").Append(Fmt.F(Strings.NodeTip_SystemUserExcludedFormat, string.Join(", ", ex)));
        sb.Append("\n\n").Append(plan.Targets.Count == 0
            ? Strings.NodeTip_SystemNothingPlaying
            : Fmt.F(Strings.NodeTip_SystemCapturedFormat, string.Join(", ", plan.Targets.Select(t => t.Exe).Distinct())));
        return sb.ToString();
    }

    /// <summary>Список пропущенных процессов с причиной для каждого (значок «пропущено: N»).</summary>
    public static string SkippedTooltip(SystemPlan plan)
    {
        var sb = new StringBuilder(Strings.NodeTip_SkippedHeader);
        foreach (var s in plan.Skipped)
            sb.Append("\n• ").Append(s.Reason switch
            {
                SkipReason.ContainsMixer => Fmt.F(Strings.Skip_ContainsMixerFormat, s.Exe),
                SkipReason.ContainsExcluded => Fmt.F(Strings.Skip_ContainsExcludedFormat, s.Exe, s.Because ?? "?"),
                _ => Fmt.F(Strings.Skip_CaptureLimitFormat, s.Exe, SystemCapture.MaxCaptures),
            });
        return sb.ToString();
    }

    private static bool SameNodes(AudioNode[] a, List<AudioNode> b)
    {
        if (a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <param name="current">Устройство узла «по умолчанию» сейчас (у обычного узла не нужно).</param>
    private static string BaseSubtitle(NodeViewModel n, int? rate, string? current = null, string? cableName = null)
    {
        // кабель с ролью: «виртуальный микрофон · CABLE-A», «виртуальный вход · CABLE»; системное имя — в подсказке
        if (n.Cable is { } link && n.Config.DefaultRole is null)
        {
            string pair = cableName ?? (n.Config.EndpointName is { } en ? Fmt.ShortDevice(en) : n.Name);
            if (n.IsOutput && link.Role == CableRole.VirtualMic) return Fmt.F(Strings.NodeSub_VirtualMicFormat, pair);
            if (n.Kind == NodeKind.DeviceInput && link.Role == CableRole.VirtualInput)
                return Fmt.F(Strings.NodeSub_VirtualInputFormat, pair) + (n.Config.Mono == true ? " · " + Strings.NodeSub_Mono : "");
        }
        var c = n.Config;
        string? name = c.FollowsDefault ? current : c.EndpointName;
        string? dev = name is null ? null : Fmt.ShortDevice(name);
        if (c.DefaultRole is { } role)
        {
            string what = role == DefaultRole.Communications ? Strings.NodeSub_DefaultComms : Strings.NodeSub_Default;
            string now = dev is null ? "" : " · " + dev;
            return c.Kind switch
            {
                NodeKind.DeviceInput => what + now + (c.Mono == true ? " · " + Strings.NodeSub_Mono : ""),
                NodeKind.Loopback => what + now + (c.Mono == true ? " · " + Strings.NodeSub_Mono : ""),
                _ => what + now + (rate is > 0 ? " · " + Fmt.F(Strings.NodeSub_KhzFormat, Fmt.Khz(rate.Value)) : ""),
            };
        }
        string khz = rate is > 0 ? " · " + Fmt.F(Strings.NodeSub_KhzFormat, Fmt.Khz(rate.Value)) : "";
        string mono = c.Mono == true ? " · " + Strings.NodeSub_Mono : "";
        return c.Kind switch
        {
            NodeKind.DeviceInput => (n.IsCable ? Fmt.F(Strings.NodeSub_CableFormat, dev) : Strings.NodeSub_InputDevice) + mono,
            NodeKind.Loopback => Fmt.F(Strings.NodeSub_LoopbackFormat, dev) + mono,
            NodeKind.App => Fmt.F(Strings.NodeSub_AppFormat, ProcessSnapshot.NormalizeExe(c.Exe ?? "")) + mono,
            NodeKind.System => Strings.NodeSub_SystemExceptMixer + mono,
            _ => (n.IsCable ? dev ?? Strings.NodeSub_Cable : Strings.NodeSub_OutputDevice) + khz,
        };
    }

    private void PollWire(WireViewModel w, EngineSnapshot snap, IReadOnlyList<Mixer.Audio.Interop.EndpointInfo> devices, GraphPaths? paths, BuildResult? build)
    {
        bool direct = _host is not null && build?.DirectRoutes.GetValueOrDefault(w.From) is { } route && route.OutputId == w.To;
        if (direct != w.IsDirect)
        {
            w.IsDirect = direct;
            WiresInvalidated?.Invoke();
        }
        var list = new List<Wire>(1);
        foreach (var (key, wire) in snap.Wires)
            if (key.To == w.To && GraphBuilder.NodeIdOf(key.From) == w.From) list.Add(wire);
        if (list.Count != w.EngineWires.Length || list.Where((x, i) => x != w.EngineWires[i]).Any()) w.EngineWires = list.ToArray();
        var links = new List<BusLink>();
        foreach (var (key, link) in snap.Links)
            if (key.To == w.To && GraphBuilder.NodeIdOf(key.From) == w.From) links.Add(link);
        if (links.Count != w.EngineLinks.Length || links.Where((x, i) => x != w.EngineLinks[i]).Any()) w.EngineLinks = links.ToArray();

        string? forbidden = paths is not null ? Mixer.Core.Editing.ConnectionRules.WhyWireForbidden(Graph, w.Config, devices, paths) : null;
        if (forbidden != w.Forbidden)
        {
            w.Forbidden = forbidden;
            WiresInvalidated?.Invoke();
        }
        bool idle = _host is not null && (w.Source.Look != NodeLook.Normal || w.Target.Look != NodeLook.Normal || list.Count + links.Count == 0 && !direct);
        if (idle != w.IsIdle)
        {
            w.IsIdle = idle;
            WiresInvalidated?.Invoke();
        }

        var s = w.Source.Config;
        var o = w.Target.Config;
        w.TipTitle = Fmt.F(Strings.Wire_TooltipTitleFormat, s.Name, o.Name);
        if (forbidden is not null)
        {
            w.TipGain = Strings.Wire_TooltipNotWorking;
            w.TipFormat = forbidden;
            w.TipFailures = Strings.Wire_TooltipDeleteIt;
            w.HasFailures = true;
            return;
        }
        if (!w.Enabled) w.TipGain = Strings.Wire_TooltipOff;
        else if (s.Muted) w.TipGain = Fmt.F(Strings.Wire_TooltipMutedFormat, s.Name);
        else if (o.Muted) w.TipGain = Fmt.F(Strings.Wire_TooltipMutedFormat, o.Name);
        else
        {
            double total = s.GainDb + w.GainDb + o.GainDb;
            var terms = new StringBuilder(s.GainDb < 0 ? "−" : "").Append(Fmt.Term(s.GainDb));
            foreach (double v in new double[] { w.GainDb, o.GainDb })
                terms.Append(v < 0 ? " − " : " + ").Append(Fmt.Term(v));
            w.TipGain = Fmt.F(Strings.Wire_TooltipNetFormat, Fmt.Pill(total), terms);
        }

        if (direct && w.Enabled)
        {
            w.TipFormat = Strings.Wire_TooltipDirect;
            w.TipFailures = Fmt.F(Strings.Wire_TooltipDirectFormat, s.Name, o.Name);
            w.HasFailures = false;
            return;
        }

        var running = list.FirstOrDefault(x => x.State == WireState.Running) ?? list.FirstOrDefault();
        if (running is not null && running.InputRate > 0 && running.OutputRate > 0)
        {
            var src = snap.Sources.GetValueOrDefault(running.FromId);
            var dst = snap.Outputs.GetValueOrDefault(running.OutputId);
            double total = (src?.CaptureLatencyMs ?? 0) + running.LatencyMs + (dst?.RenderLatencyMs ?? 0);
            w.TipFormat = Fmt.F(Strings.Wire_TooltipRateFormat, Fmt.Khz(running.InputRate), Fmt.Khz(running.OutputRate), Fmt.Ms(running.FillMs), Fmt.Ms(total)) +
                          (list.Count > 1 ? " · " + Fmt.Plural(list.Count, Strings.Plural_Capture) : "");
        }
        else if (links.Count > 0)
        {
            w.TipFormat = Strings.Wire_TooltipFromBus;
        }
        else
        {
            w.TipFormat = _host is null ? Strings.Wire_TooltipNoAudio
                : w.Source.Look != NodeLook.Normal ? Fmt.F(w.Source.Look == NodeLook.Error ? Strings.Wire_TooltipErrorFormat : Strings.Wire_TooltipWaitingFormat, s.Name)
                : w.Target.Look != NodeLook.Normal ? Fmt.F(w.Target.Look == NodeLook.Error ? Strings.Wire_TooltipErrorFormat : Strings.Wire_TooltipWaitingFormat, o.Name)
                : Strings.Wire_TooltipConnecting;
        }

        int under = 0, over = 0, resync = 0, gaps = 0;
        foreach (var x in list)
        {
            under += x.Underruns;
            over += x.Overruns;
            resync += x.Resyncs;
            gaps += x.Gaps;
        }
        int sum = under + over + resync;
        w.HasFailures = sum > 0;
        w.TipFailures = (sum == 0 ? Strings.Wire_TooltipDropoutsNone : Fmt.F(Strings.Wire_TooltipDropoutsFormat, sum, under, over, resync)) +
                        (gaps > 0 ? " · " + Fmt.F(Strings.Wire_TooltipGapsFormat, gaps) : "");
    }

    // ── индикаторы ──

    private void OnFrame(double now, double dt)
    {
        _now = now;
        foreach (var n in Nodes)
        {
            if (n.DirectMeter is { } dm)
            {
                // прямой маршрут: индикатор сессии приложения у Windows (опрашивает хост)
                n.DirectPeak = dm.Take();
                n.Meter.Push(n.DirectPeak.L, n.DirectPeak.R, now, dt);
                continue;
            }
            if (n.EngineBuses.Length > 0)
            {
                // фейдер и обработка у такого узла — на шине: индикатор после них
                float bl = 0, br = 0;
                foreach (var b in n.EngineBuses)
                {
                    var (pl, pr) = b.Peak.Take();
                    if (pl > bl) bl = pl;
                    if (pr > br) br = pr;
                }
                foreach (var e in n.EngineNodes) e.Peak.Take();
                n.Meter.Push(bl, br, now, dt);
                continue;
            }
            var nodes = n.EngineNodes;
            if (nodes.Length == 0)
            {
                n.Meter.Reset();
                continue;
            }
            float l = 0, r = 0;
            foreach (var e in nodes)
            {
                var (pl, pr) = e.Peak.Take();
                if (pl > l) l = pl;
                if (pr > r) r = pr;
            }
            n.Meter.Push(l, r, now, dt);
            if (n.EngineOutput is { } o) n.Meter.PushReduction(o.TakeLimiterMinGain(), now);
        }
        foreach (var n in Nodes)
        {
            if ((n.HasGate || n.HasEq || n.HasComp) && n.EngineChains.Length > 0) n.ChainMeter.Push(n.EngineChains, now, dt);
            else n.ChainMeter.Reset();
        }
        foreach (var w in Wires)
        {
            var wires = w.EngineWires;
            var links = w.EngineLinks;
            if (w.IsDirect && w.Source.DirectMeter is not null)
            {
                w.Meter.Push(w.Source.DirectPeak.L, w.Source.DirectPeak.R, now, dt);
                continue;
            }
            if (wires.Length == 0 && links.Length == 0)
            {
                w.Meter.Reset();
                continue;
            }
            float l = 0, r = 0;
            foreach (var e in wires)
            {
                var (pl, pr) = e.Peak.Take();
                if (pl > l) l = pl;
                if (pr > r) r = pr;
            }
            foreach (var e in links)
            {
                var (pl, pr) = e.Peak.Take();
                if (pl > l) l = pl;
                if (pr > r) r = pr;
            }
            w.Meter.Push(l, r, now, dt);
        }
    }
}
