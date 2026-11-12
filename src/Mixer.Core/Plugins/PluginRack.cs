using System.Diagnostics;
using Mixer.Audio.Dsp;
using Mixer.Core.Config;
using Mixer.Plugins.Host;

namespace Mixer.Core.Plugins;

public sealed record PluginRackOptions
{
    /// <summary>Mixer.PluginHost.exe; null — вшитый (<see cref="HostBinary.Locate"/>).</summary>
    public string? HostExe { get; init; }
    /// <summary>Наибольший блок (как <c>EngineOptions.MaxBlockFrames</c>).</summary>
    public int MaxFrames { get; init; } = 8192;
    /// <summary>Частота загрузки, пока узел ещё не звал слот; потом — частота узла (setRate).</summary>
    public int DefaultRate { get; init; } = 48000;
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Столько падений за <see cref="CrashWindow"/> — слот в сбое до «Перезапустить».</summary>
    public int CrashLimit { get; init; } = 3;
    public TimeSpan CrashWindow { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Больше стольких просрочек за <see cref="MissWindow"/> — «не успевает», слот выключается.</summary>
    public int MissLimit { get; init; } = 10;
    public TimeSpan MissWindow { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Состояние забирается у плагина через столько тишины после последнего dirty.</summary>
    public TimeSpan StateDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Загрузка с окнами лицензий: слот «загружается» до 30 с, звук идёт без него.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxPerGroup { get; init; } = 2;
    public int MaxSlots { get; init; } = 16;
    /// <summary>Плагин по GUID класса, если пути из схемы нет (каталог): путь или null.</summary>
    public Func<string, string?>? Find { get; init; }
    /// <summary>Что загрузить в хост для слота; null — VST3 по пути или <see cref="Find"/>. Тесты подставляют встроенные плагины.</summary>
    public Func<PluginConfig, PluginLoad?>? Resolve { get; init; }
    /// <summary>Папка, от которой считаются относительные пути <see cref="PluginConfig.State"/>.</summary>
    public string? StateFolder { get; init; }
    /// <summary>Настройки процессов хостов (таймаут зависания и т. п.).</summary>
    public PluginHostOptions Host { get; init; } = new();
}

/// <summary>Слот для интерфейса и журнала.</summary>
public sealed record PluginSlotStatus(
    string NodeId,
    string SlotId,
    string Name,
    PluginSlotState State,
    PluginFault Fault,
    string? Detail,
    int LatencySamples,
    int Rate,
    /// <summary>Доля периода, которую занимает process, сглаженная за 2 с.</summary>
    double Load,
    /// <summary>Недавно (5 с) выдавал выше +12 dBFS.</summary>
    bool Loud);

/// <summary>
/// Слоты плагинов схемы (VST, «Место в цепочке и в движке», «Надёжность»): по слоту и хосту на каждый
/// <see cref="PluginConfig"/>. Движок получает <see cref="PluginSlot"/> через <see cref="Slots"/>, а здесь — всё, что
/// вокруг звука: загрузка, частота узла, включение (через kIsBypass плагина, если он есть), перезапуск после падения,
/// автоотключение, состояние для перезапуска. <see cref="Sync"/> и <see cref="Tick"/> зовёт управляющий поток;
/// события хостов приходят из пула, поэтому всё под одной блокировкой.
/// </summary>
public sealed class PluginRack : IDisposable
{
    private sealed class Runner(string nodeId, PluginConfig config, PluginSlot slot)
    {
        public readonly string NodeId = nodeId;
        public PluginConfig Config = config;
        public readonly PluginSlot Slot = slot;
        public PluginHost? Host;
        public PluginParam? Bypass;
        public byte[]? State;
        public int Generation;
        public readonly List<double> Crashes = [];
        public readonly Queue<(double At, int Count)> Misses = new();
        public double? DirtyAt;
        public bool Reconfiguring, Saving;
        public double Load, LoudAt = double.NegativeInfinity;
    }

    private readonly PluginRackOptions _opt;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<(string Node, string Slot), Runner> _runners = new();
    private Dictionary<string, PluginSlot[]> _byNode = new();
    private readonly List<Task> _stopping = [];
    private double _lastTick;
    private bool _disposed;

    public PluginRack(PluginRackOptions? options = null, Action<string>? log = null)
    {
        _opt = options ?? new PluginRackOptions();
        _log = log;
    }

    /// <summary>Состояние слота забрано у плагина (после правок): этап «Интерфейс» пишет его в файл.</summary>
    public event Action<string, string, byte[]>? StateSaved;

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Слоты узла по порядку (для <see cref="Graph.RuntimeContext.Plugins"/>).</summary>
    public IReadOnlyList<PluginSlot> Slots(string nodeId)
    {
        lock (_gate) return _byNode.GetValueOrDefault(nodeId) ?? [];
    }

    public bool Any
    {
        get { lock (_gate) return _runners.Count > 0; }
    }

    public IReadOnlyList<PluginSlotStatus> Status
    {
        get
        {
            lock (_gate)
            {
                double now = Now;
                return _runners.Values.Select(r => new PluginSlotStatus(r.NodeId, r.Config.Id, r.Config.Name, r.Slot.State, r.Slot.Fault,
                    r.Slot.Detail, r.Slot.LatencySamples, r.Slot.HostRate, r.Load, now - r.LoudAt < 5)).ToList();
            }
        }
    }

    /// <summary>Последнее известное состояние слота (то, с которым он перезапустится).</summary>
    public byte[]? StateOf(string nodeId, string slotId)
    {
        lock (_gate) return _runners.GetValueOrDefault((nodeId, slotId))?.State;
    }

    /// <summary>Хост слота (окно редактора, параметры — этап «Интерфейс»); null — не запущен.</summary>
    public PluginHost? HostOf(string nodeId, string slotId)
    {
        lock (_gate) return _runners.GetValueOrDefault((nodeId, slotId))?.Host;
    }

    /// <summary>Привести слоты к схеме: новые запустить, лишние остановить, включение и группу обновить.</summary>
    public void Sync(GraphConfig graph)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var want = new List<(string Node, PluginConfig P, bool Limited)>();
            int total = 0;
            foreach (var n in graph.Nodes.Where(n => n.HasPlugins))
            {
                var perGroup = new Dictionary<PluginGroup, int>();
                foreach (var p in n.Plugins!.DistinctBy(p => p.Id))
                {
                    int inGroup = perGroup.GetValueOrDefault(p.Group);
                    bool limited = inGroup >= _opt.MaxPerGroup || total >= _opt.MaxSlots;
                    if (!limited)
                    {
                        perGroup[p.Group] = inGroup + 1;
                        total++;
                    }
                    want.Add((n.Id, p, limited));
                }
            }

            var keys = want.Select(w => (w.Node, w.P.Id)).ToHashSet();
            foreach (var key in _runners.Keys.Where(k => !keys.Contains(k)).ToList())
            {
                StopHost(_runners[key]);
                _runners.Remove(key);
            }

            foreach (var (node, p, limited) in want)
            {
                if (_runners.TryGetValue((node, p.Id), out var r) && SameLoad(r.Config, p) && (r.Slot.Fault == PluginFault.Limit) == limited)
                {
                    r.Config = p;
                    r.Slot.Post = p.Group == PluginGroup.Post;
                    ApplyEnabled(r);
                    continue;
                }
                if (r is not null) StopHost(r);
                r = new Runner(node, p, new PluginSlot(p.Id, _opt.MaxFrames) { Post = p.Group == PluginGroup.Post })
                {
                    State = ReadStateFile(p),
                };
                _runners[(node, p.Id)] = r;
                if (limited) Fail(r, PluginFault.Limit, null);
                else Start(r);
            }

            _byNode = want.Where(w => _runners.ContainsKey((w.Node, w.P.Id))).GroupBy(w => w.Node)
                .ToDictionary(g => g.Key, g => g.Select(w => _runners[(w.Node, w.P.Id)].Slot).ToArray());
        }
    }

    private static bool SameLoad(PluginConfig a, PluginConfig b) =>
        a.Format == b.Format && a.ClassId == b.ClassId && a.Path == b.Path && a.EvenBlocks == b.EvenBlocks;

    /// <summary>«Перезапустить»: сбросить счёт падений и загрузить заново (с последним состоянием).</summary>
    public bool Restart(string nodeId, string slotId)
    {
        lock (_gate)
        {
            if (!_runners.TryGetValue((nodeId, slotId), out var r) || r.Slot.Fault == PluginFault.Limit) return false;
            r.Crashes.Clear();
            StopHost(r);
            Start(r);
            return true;
        }
    }

    /// <summary>Загрузить слоты, которых не нашли (после обновления каталога).</summary>
    public void RetryMissing()
    {
        lock (_gate)
            foreach (var r in _runners.Values.Where(r => r.Slot.Fault == PluginFault.NotFound)) Start(r);
    }

    // ── загрузка ──

    private void Start(Runner r)
    {
        StopHost(r);
        int gen = ++r.Generation;
        var load = (_opt.Resolve ?? DefaultResolve)(r.Config);
        if (load is null)
        {
            Fail(r, PluginFault.NotFound, r.Config.Path ?? r.Config.ClassId);
            return;
        }
        r.Slot.State = PluginSlotState.Loading;
        r.Slot.Fault = PluginFault.None;
        r.Slot.Detail = null;
        PluginHost host;
        try
        {
            host = PluginHost.Start(_opt.Host with { HostExe = _opt.HostExe ?? _opt.Host.HostExe, MaxFrames = _opt.MaxFrames, Log = _log });
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Fail(r, PluginFault.LoadFailed, e.Message);
            return;
        }
        r.Host = host;
        r.Bypass = null;
        host.Terminated += exit => OnTerminated(r, host, exit);
        host.Dirty += () =>
        {
            lock (_gate) if (r.Host == host) r.DirtyAt = Now;
        };
        host.LayoutChanged += layout =>
        {
            lock (_gate) if (r.Host == host) r.Slot.SetLatency(layout.LatencySamples);
        };
        int rate = r.Slot.SeenRate > 0 ? r.Slot.SeenRate : _opt.DefaultRate;
        _ = LoadAsync(r, host, gen, load with { SampleRate = rate, EvenBlocks = r.Config.EvenBlocks, State = r.State });
    }

    private async Task LoadAsync(Runner r, PluginHost host, int gen, PluginLoad load)
    {
        try
        {
            var info = await host.LoadAsync(load, _opt.LoadTimeout).ConfigureAwait(false);
            lock (_gate)
            {
                if (r.Generation != gen || r.Host != host) return;
                r.Bypass = info.BypassParam;
                r.Slot.Attach(host.Audio, load.SampleRate, info.LatencySamples);
                ApplyEnabled(r);
                Log(r, $"loaded {info.Name} ({info.Vendor}), latency {info.LatencySamples}, {load.SampleRate} Hz");
            }
        }
        catch (Exception e) when (e is PluginHostException or TimeoutException or IOException or OperationCanceledException or InvalidOperationException)
        {
            lock (_gate)
            {
                if (r.Generation != gen || r.Host != host) return;
                Fail(r, PluginFault.LoadFailed, e.Message);
            }
        }
    }

    private PluginLoad? DefaultResolve(PluginConfig p)
    {
        if (!string.Equals(p.Format, "vst3", StringComparison.OrdinalIgnoreCase)) return null;
        string? path = p.Path is { } x && Path.Exists(x) ? x : p.ClassId is { } id ? _opt.Find?.Invoke(id) : null;
        return path is null ? null : PluginLoad.Vst3(path, p.ClassId);
    }

    private byte[]? ReadStateFile(PluginConfig p)
    {
        if (string.IsNullOrEmpty(p.State)) return null;
        string path = Path.IsPathRooted(p.State) || _opt.StateFolder is null ? p.State : Path.Combine(_opt.StateFolder, p.State);
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Plug-in state {path} unreadable: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Включение пользователем. У плагина с kIsBypass — его параметр: звук идёт через плагин, и хвост реверберации
    /// затухает сам. Без него — кроссфейд в обход.
    /// </summary>
    private void ApplyEnabled(Runner r)
    {
        bool on = r.Config.Enabled;
        bool attached = r.Slot.IsAttached;
        if (attached && r.Bypass is { } b && r.Host is { } host)
        {
            host.SetParam(b.Id, on ? 0 : 1);
            r.Slot.Wet = true;
        }
        else
        {
            r.Slot.Wet = on;
        }
        if (attached && r.Slot.State is PluginSlotState.Running or PluginSlotState.Off or PluginSlotState.Loading)
            r.Slot.State = on ? PluginSlotState.Running : PluginSlotState.Off;
    }

    // ── сбои ──

    private void OnTerminated(Runner r, PluginHost host, HostExit exit)
    {
        lock (_gate)
        {
            if (r.Host != host) return; // остановили сами
            StopHost(r);
            double now = Now;
            r.Crashes.Add(now);
            r.Crashes.RemoveAll(t => now - t > _opt.CrashWindow.TotalSeconds);
            if (r.Crashes.Count >= _opt.CrashLimit)
            {
                Fail(r, PluginFault.CrashedRepeatedly, exit.Message);
                return;
            }
            r.Slot.State = PluginSlotState.Failed;
            r.Slot.Fault = exit.Reason == HostExitReason.Hung ? PluginFault.Hung : PluginFault.Crashed;
            r.Slot.Detail = exit.Message;
            Log(r, $"{exit.Message}; restarting in {_opt.RestartDelay.TotalSeconds:0.#} s");
            int gen = r.Generation;
            _ = Task.Delay(_opt.RestartDelay).ContinueWith(_ =>
            {
                lock (_gate)
                    if (!_disposed && r.Generation == gen && r.Host is null && _runners.ContainsValue(r)) Start(r);
            }, TaskScheduler.Default);
        }
    }

    private void Fail(Runner r, PluginFault fault, string? detail)
    {
        StopHost(r);
        r.Slot.State = fault == PluginFault.NotFound ? PluginSlotState.Missing : PluginSlotState.Failed;
        r.Slot.Fault = fault;
        r.Slot.Detail = detail;
        Log(r, $"{fault}{(detail is null ? "" : ": " + detail)}");
    }

    /// <summary>Отключить хост от аудиопотока и остановить его в фоне (quit, затем kill).</summary>
    private void StopHost(Runner r)
    {
        var host = r.Host;
        if (host is null) return;
        r.Host = null;
        r.Generation++;
        r.Reconfiguring = r.Saving = false;
        r.DirtyAt = null;
        r.Misses.Clear();
        if (!r.Slot.Detach()) _log?.Invoke($"Plug-in slot {r.NodeId}/{r.Config.Id}: audio thread did not release the host");
        _stopping.RemoveAll(t => t.IsCompleted);
        _stopping.Add(Task.Run(host.Dispose));
    }

    private void Log(Runner r, string text) => _log?.Invoke($"Plug-in {r.Config.Name} ({r.NodeId}/{r.Config.Id}): {text}");

    // ── такт управляющего потока ──

    /// <summary>Раз в секунду: просрочки, NaN, громкость, нагрузка, частота узла, состояние после правок.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            double now = Now;
            double dt = Math.Max(1e-3, now - _lastTick);
            _lastTick = now;
            foreach (var r in _runners.Values)
            {
                var s = r.Slot;
                var host = r.Host;
                int misses = s.TakeMisses();
                if (misses > 0) r.Misses.Enqueue((now, misses));
                while (r.Misses.Count > 0 && now - r.Misses.Peek().At > _opt.MissWindow.TotalSeconds) r.Misses.Dequeue();
                if (host is not null && r.Misses.Sum(m => m.Count) > _opt.MissLimit)
                {
                    Fail(r, PluginFault.TooSlow, $"{r.Misses.Sum(m => m.Count)} blocks late in {_opt.MissWindow.TotalSeconds:0} s");
                    continue;
                }
                if (s.TakeBadOutput())
                {
                    Fail(r, PluginFault.BadOutput, "NaN or infinity in the output");
                    continue;
                }
                if (s.TakeLoud() > 0) r.LoudAt = now;

                var (ticks, frames) = s.TakeLoad();
                double load = frames > 0 && s.HostRate > 0 ? ticks / (double)Stopwatch.Frequency / (frames / (double)s.HostRate) : 0;
                r.Load += (load - r.Load) * Math.Min(1, dt / 2.0);

                if (host is null || !s.IsAttached) continue;
                if (!r.Reconfiguring && s.SeenRate > 0 && s.SeenRate != s.HostRate)
                {
                    r.Reconfiguring = true;
                    _ = SetRateAsync(r, host, r.Generation, s.SeenRate);
                }
                if (r.DirtyAt is { } d && now - d >= _opt.StateDelay.TotalSeconds && !r.Saving)
                {
                    r.DirtyAt = null;
                    r.Saving = true;
                    _ = SaveStateAsync(r, host, r.Generation);
                }
            }
        }
    }

    private async Task SetRateAsync(Runner r, PluginHost host, int gen, int rate)
    {
        try
        {
            int latency = await host.SetRateAsync(rate).ConfigureAwait(false);
            lock (_gate)
            {
                if (r.Generation != gen) return;
                r.Slot.SetHostRate(rate);
                r.Slot.SetLatency(latency);
                Log(r, $"rate {rate} Hz, latency {latency}");
            }
        }
        catch (Exception e) when (e is PluginHostException or TimeoutException or IOException)
        {
            Log(r, $"setRate {rate} failed: {e.Message}");
        }
        finally
        {
            lock (_gate) if (r.Generation == gen) r.Reconfiguring = false;
        }
    }

    private async Task SaveStateAsync(Runner r, PluginHost host, int gen)
    {
        try
        {
            byte[] blob = await host.GetStateAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (r.Generation != gen) return;
                r.State = blob;
            }
            StateSaved?.Invoke(r.NodeId, r.Config.Id, blob);
        }
        catch (Exception e) when (e is PluginHostException or TimeoutException or IOException)
        {
            Log(r, $"getState failed: {e.Message}");
        }
        finally
        {
            lock (_gate) if (r.Generation == gen) r.Saving = false;
        }
    }

    /// <summary>Остановить все хосты (выход из микшера). Job Object добьёт те, что не успели.</summary>
    public void Dispose()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var r in _runners.Values) StopHost(r);
            _runners.Clear();
            _byNode = new();
            pending = [.. _stopping];
        }
        Task.WaitAll(pending, TimeSpan.FromSeconds(5));
    }
}
