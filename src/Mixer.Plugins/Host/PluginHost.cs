using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Mixer.Plugins.Host;

public sealed record PluginHostOptions
{
    /// <summary>Путь к Mixer.PluginHost.exe; null — <see cref="HostBinary.Locate"/>.</summary>
    public string? HostExe { get; init; }
    /// <summary>Частота в заголовке секции до load (load несёт свою).</summary>
    public int SampleRate { get; init; } = 48000;
    /// <summary>Наибольший блок, кадров (как <c>EngineOptions.MaxBlockFrames</c>).</summary>
    public int MaxFrames { get; init; } = 8192;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Сколько хост может молчать, пока у него нет запросов, прежде чем считаться зависшим (три пропуска ping).</summary>
    public TimeSpan HangTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Активное ожидание ответа на блок перед уходом в ядро, мкс.</summary>
    public double SpinMicroseconds { get; init; } = 50;
    /// <summary>Добавочные аргументы командной строки (внедрение сбоев: --stall-every, --stall-ms).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    /// <summary>Журнал: строки stderr хоста и события надзора (английский, как весь журнал).</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Один процесс Mixer.PluginHost.exe с одним плагином: аудиоканал (<see cref="Audio"/>), канал управления,
/// Job Object и надзор (ping раз в секунду, три пропуска — зависание). События приходят из потоков пула;
/// обработчики не должны блокировать надолго.
/// Перезапуск после падения и автоотключение — забота слота (этап «Движок»), здесь только факты.
/// </summary>
public sealed class PluginHost : IDisposable
{
    private static int s_counter;

    private readonly PluginHostOptions _options;
    private readonly ControlPipe _pipe;
    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _connected;
    private readonly TaskCompletionSource<HostExit> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly Queue<string> _stderrTail = new();
    private readonly Timer _heartbeat;
    private int _nextReq;
    private long _lastHeard;
    private int _stopReason = -1; // HostExitReason или -1: почему процесс завершится, если завершим его мы
    private int _disposed;

    private sealed record Pending(TaskCompletionSource<JsonObject> Tcs, string OkType);

    public AudioLink Audio { get; }
    public int ProcessId { get; }
    public string HostExe { get; }
    /// <summary>Что загружено (после <see cref="LoadAsync"/>).</summary>
    public PluginInfo? Info { get; private set; }
    public bool IsRunning => !_exitTcs.Task.IsCompleted;
    public HostExit? Exit => _exitTcs.Task.IsCompleted ? _exitTcs.Task.Result : null;
    public Task<HostExit> Exited => _exitTcs.Task;

    /// <summary>Правка в окне плагина (performEdit) или эхо на <see cref="SetParam"/>.</summary>
    public event Action<PluginParamValue>? ParamChanged;
    /// <summary>Список или значения параметров обновились целиком (restartComponent, новое состояние).</summary>
    public event Action<IReadOnlyList<PluginParam>>? ParamsChanged;
    /// <summary>Задержка или шины изменились.</summary>
    public event Action<PluginLayout>? LayoutChanged;
    /// <summary>В окне плагина что-то поменяли: состояние стоит сохранить (через 2 с тишины).</summary>
    public event Action? Dirty;
    public event Action<EditorBounds>? EditorOpened;
    public event Action<EditorBounds>? EditorClosed;
    public event Action<string>? EditorFailed;
    /// <summary>Хост закончился: упал, завис (и был завершён), вышел по quit или убит.</summary>
    public event Action<HostExit>? Terminated;

    private PluginHost(PluginHostOptions options)
    {
        _options = options;
        HostExe = options.HostExe ?? HostBinary.Locate(options.Log)
            ?? throw new InvalidOperationException("Mixer.PluginHost.exe is not available in this build");

        int n = Interlocked.Increment(ref s_counter);
        string id = $"Mixer.PH.{Environment.ProcessId}.{n}";
        Audio = new AudioLink(@"Local\" + id, options.SampleRate, options.MaxFrames, options.SpinMicroseconds);
        _pipe = new ControlPipe(id);
        _connected = _pipe.WaitForConnectionAsync(_cts.Token);

        var psi = new ProcessStartInfo(HostExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(HostExe)!,
        };
        psi.ArgumentList.Add("--channel");
        psi.ArgumentList.Add(Audio.Name);
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(id);
        psi.ArgumentList.Add("--parent");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        foreach (string a in options.ExtraArgs) psi.ArgumentList.Add(a);

        try
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("host did not start");
            HostJob.Assign(_process);
        }
        catch
        {
            _cts.Cancel();
            _pipe.Dispose();
            Audio.Dispose();
            throw;
        }
        ProcessId = _process.Id;
        _process.ErrorDataReceived += (_, e) => OnStderr(e.Data);
        _process.OutputDataReceived += (_, e) => OnStderr(e.Data);
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Task.Run(OnProcessExited);
        if (_process.HasExited) Task.Run(OnProcessExited);

        _lastHeard = Stopwatch.GetTimestamp();
        _connected.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) _ = _pipe.ReadLoopAsync(OnMessage);
        }, TaskScheduler.Default);
        _heartbeat = new Timer(_ => Heartbeat(), null, options.PingInterval, options.PingInterval);
    }

    /// <summary>Запустить процесс хоста (без плагина). Плагин — <see cref="LoadAsync"/>.</summary>
    public static PluginHost Start(PluginHostOptions? options = null) => new(options ?? new PluginHostOptions());

    private void Log(string text) => _options.Log?.Invoke(text);

    private void OnStderr(string? line)
    {
        if (line == null) return;
        lock (_stderrTail)
        {
            _stderrTail.Enqueue(line);
            while (_stderrTail.Count > 20) _stderrTail.Dequeue();
        }
        Log($"Plug-in host {ProcessId}: {line}");
    }

    /// <summary>Последние строки stderr хоста.</summary>
    public string StderrTail()
    {
        lock (_stderrTail) return string.Join(Environment.NewLine, _stderrTail);
    }

    // --- запросы ------------------------------------------------------------------------------------

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        var timeout = Task.Delay(_options.ConnectTimeout, ct);
        var done = await Task.WhenAny(_connected, _exitTcs.Task, timeout).ConfigureAwait(false);
        if (done == _connected)
        {
            await _connected.ConfigureAwait(false);
            return;
        }
        if (done == _exitTcs.Task) throw new PluginHostException($"host exited before connecting: {_exitTcs.Task.Result.Message}");
        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"host did not connect within {_options.ConnectTimeout.TotalSeconds:0} s");
    }

    private async Task<JsonObject> RequestAsync(JsonObject msg, string okType, TimeSpan timeout, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        if (!IsRunning) throw new PluginHostException($"host is not running: {Exit!.Message}");
        int req = Interlocked.Increment(ref _nextReq);
        msg["req"] = req;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[req] = new Pending(tcs, okType);
        try
        {
            if (!_pipe.Send(msg)) throw new PluginHostException("control pipe is closed");
            var delay = Task.Delay(timeout, ct);
            if (await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false) != tcs.Task)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"host did not answer '{msg["type"]}' within {timeout.TotalSeconds:0.#} s");
            }
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(req, out _);
            Volatile.Write(ref _lastHeard, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Загрузить и активировать плагин. Таймаут — на всё, включая окна лицензий при первой загрузке
    /// (по умолчанию 30 с, как у слота при запуске микшера). После ready — 8 блоков тишины для разогрева.
    /// </summary>
    public async Task<PluginInfo> LoadAsync(PluginLoad load, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var msg = new JsonObject
        {
            ["type"] = "load",
            ["rate"] = load.SampleRate,
            ["maxBlock"] = Audio.MaxFrames,
            ["evenBlocks"] = load.EvenBlocks,
        };
        if (load.Builtin != null)
        {
            msg["builtin"] = load.Builtin;
            var o = load.Options ?? new BuiltinOptions();
            msg["options"] = new JsonObject
            {
                ["mono"] = o.Mono, ["sleepMs"] = o.SleepMs, ["after"] = o.After, ["latency"] = o.Latency,
            };
        }
        else
        {
            msg["path"] = load.Path ?? throw new ArgumentException("path or builtin is required", nameof(load));
            if (!string.IsNullOrEmpty(load.ClassId)) msg["classId"] = load.ClassId;
        }
        if (load.State != null) msg["state"] = Convert.ToBase64String(load.State);

        var ready = await RequestAsync(msg, "ready", timeout ?? TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        Info = PluginInfo.From(ready);
        await Task.Run(() => Audio.WarmUp(), ct).ConfigureAwait(false);
        return Info;
    }

    /// <summary>Состояние плагина (формат <see cref="PluginState"/>).</summary>
    public async Task<byte[]> GetStateAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["type"] = "getState" }, "state",
            timeout ?? TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return Convert.FromBase64String((string?)r["blob"] ?? "");
    }

    /// <summary>Подменить состояние (пресет слота). На это время блоки идут сухими. Возвращает новые значения параметров.</summary>
    public async Task<IReadOnlyList<PluginParam>> SetStateAsync(byte[] blob, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["type"] = "setState", ["blob"] = Convert.ToBase64String(blob) },
            "stateSet", timeout ?? TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var list = PluginParam.ListFrom(r["params"]);
        ParamsChanged?.Invoke(list);
        return list;
    }

    /// <summary>Новая частота устройства узла: деактивация, setupProcessing, активация, состояние сохраняется.</summary>
    public async Task<int> SetRateAsync(int rate, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["type"] = "setRate", ["rate"] = rate }, "rateSet",
            timeout ?? TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return (int?)r["latency"] ?? 0;
    }

    /// <summary>Включить или выключить «ровные блоки» на ходу.</summary>
    public async Task<PluginLayout> SetEvenBlocksAsync(bool on, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["type"] = "setEvenBlocks", ["on"] = on }, "latencyChanged",
            timeout ?? TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return LayoutFrom(r);
    }

    public async Task<IReadOnlyList<PluginParam>> GetParamsAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var r = await RequestAsync(new JsonObject { ["type"] = "getParams" }, "params",
            timeout ?? TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return PluginParam.ListFrom(r["params"]);
    }

    // --- сообщения без ответа -------------------------------------------------------------------------

    /// <summary>Значение параметра (нормализованное). Хост ответит <see cref="ParamChanged"/> с текстом значения.</summary>
    public bool SetParam(uint id, double value) =>
        _pipe.Send(new JsonObject { ["type"] = "setParam", ["id"] = id, ["value"] = Math.Clamp(value, 0, 1) });

    /// <summary>
    /// Открыть окно плагина поверх <paramref name="owner"/> (или поднять уже открытое).
    /// Ответ — <see cref="EditorOpened"/> или <see cref="EditorFailed"/>.
    /// </summary>
    public bool OpenEditor(IntPtr owner, string title, EditorBounds? bounds = null)
    {
        // окно другого процесса может стать активным, только если активный процесс это разрешил
        Native.AllowSetForegroundWindow(ProcessId);
        var msg = new JsonObject { ["type"] = "openEditor", ["ownerHwnd"] = (ulong)owner, ["title"] = title };
        if (bounds is { } b)
        {
            msg["x"] = b.X;
            msg["y"] = b.Y;
            msg["w"] = b.Width;
            msg["h"] = b.Height;
        }
        return _pipe.Send(msg);
    }

    public bool CloseEditor() => _pipe.Send(new JsonObject { ["type"] = "closeEditor" });

    /// <summary>Тесты: закрыть канал управления без quit, как если бы микшер исчез.</summary>
    internal void AbandonPipeForTest() => _pipe.Dispose();

    /// <summary>Только для встроенных плагинов: имитация правки в окне, зависания главного потока и т. п.</summary>
    internal bool SendDebug(JsonObject msg)
    {
        msg["type"] = "debug";
        return _pipe.Send(msg);
    }

    // --- входящие -----------------------------------------------------------------------------------

    private void OnMessage(JsonObject m)
    {
        Volatile.Write(ref _lastHeard, Stopwatch.GetTimestamp());
        string type = (string?)m["type"] ?? "";
        if (m["req"] is JsonNode reqNode && _pending.TryGetValue((int)reqNode, out var p))
        {
            if (type is "error" or "failed")
                p.Tcs.TrySetException(new PluginHostException((string?)m["message"] ?? type));
            else if (type == p.OkType)
                p.Tcs.TrySetResult(m);
        }

        try
        {
            switch (type)
            {
                case "param":
                    ParamChanged?.Invoke(new PluginParamValue((uint)m["id"]!, (double)m["value"]!, (string?)m["text"] ?? ""));
                    break;
                case "params":
                    if (m["req"] == null) ParamsChanged?.Invoke(PluginParam.ListFrom(m["params"]));
                    break;
                case "latencyChanged":
                    LayoutChanged?.Invoke(LayoutFrom(m));
                    break;
                case "dirty":
                    Dirty?.Invoke();
                    break;
                case "editorOpened":
                    EditorOpened?.Invoke(EditorBounds.From(m));
                    break;
                case "editorClosed":
                    EditorClosed?.Invoke(EditorBounds.From(m));
                    break;
                case "editorFailed":
                    EditorFailed?.Invoke((string?)m["message"] ?? "");
                    break;
                case "error":
                    if (m["req"] == null) Log($"Plug-in host {ProcessId}: error: {(string?)m["message"]}");
                    break;
            }
        }
        catch (Exception e)
        {
            Log($"Plug-in host {ProcessId}: handler for '{type}' failed: {e.Message}");
        }
    }

    private static PluginLayout LayoutFrom(JsonObject m) =>
        new((int?)m["samples"] ?? 0, (int?)m["inChannels"] ?? 2, (int?)m["outChannels"] ?? 2);

    private void Heartbeat()
    {
        if (!IsRunning || !_connected.IsCompletedSuccessfully) return;
        _pipe.Send(new JsonObject { ["type"] = "ping", ["t"] = Environment.TickCount64 });
        // пока идёт запрос (загрузка с окном лицензии, тяжёлое состояние), за сроком следит сам запрос
        if (!_pending.IsEmpty) return;
        long silent = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastHeard);
        if (silent > _options.HangTimeout.TotalSeconds * Stopwatch.Frequency)
        {
            Log($"Plug-in host {ProcessId}: no answer for {silent * 1000 / Stopwatch.Frequency} ms, terminating");
            Terminate(HostExitReason.Hung);
        }
    }

    private void OnProcessExited()
    {
        if (_exitTcs.Task.IsCompleted) return;
        try { _process.WaitForExit(); } catch (InvalidOperationException) { }
        int? code = null;
        try { code = _process.ExitCode; } catch (InvalidOperationException) { }
        int stop = Volatile.Read(ref _stopReason);
        var reason = stop >= 0 ? (HostExitReason)stop : HostExitReason.Crashed;
        string tail = StderrTail();
        string crashLine = tail.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("crashed:", StringComparison.Ordinal)) ?? "";
        string message = reason switch
        {
            HostExitReason.Hung => "host stopped answering and was terminated",
            HostExitReason.Killed => "host was terminated",
            HostExitReason.Quit => "host exited",
            _ when crashLine.Length > 0 => crashLine,
            _ => $"host exited with code {code}",
        };
        var exit = new HostExit(reason, code, message);
        if (!_exitTcs.TrySetResult(exit)) return;
        if (reason != HostExitReason.Quit) Log($"Plug-in host {ProcessId}: {message}");
        foreach (var p in _pending.Values) p.Tcs.TrySetException(new PluginHostException(message));
        _heartbeat.Change(Timeout.Infinite, Timeout.Infinite);
        try { Terminated?.Invoke(exit); }
        catch (Exception e) { Log($"Plug-in host {ProcessId}: Terminated handler failed: {e.Message}"); }
    }

    private void Terminate(HostExitReason reason)
    {
        Interlocked.CompareExchange(ref _stopReason, (int)reason, -1);
        try
        {
            if (!_process.HasExited) _process.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Завершить процесс немедленно (обход слота, «Перезапустить»).</summary>
    public void Kill() => Terminate(HostExitReason.Killed);

    /// <summary>Попросить хост выйти (закрыть окно, остановить аудио, выгрузить плагин); не вышел — убить.</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (!IsRunning) return;
        Interlocked.CompareExchange(ref _stopReason, (int)HostExitReason.Quit, -1);
        if (_connected.IsCompletedSuccessfully) _pipe.Send(new JsonObject { ["type"] = "quit" });
        else Audio.SignalStop();
        var wait = Task.Delay(timeout ?? TimeSpan.FromSeconds(3));
        if (await Task.WhenAny(_exitTcs.Task, wait).ConfigureAwait(false) != _exitTcs.Task)
        {
            Log($"Plug-in host {ProcessId}: did not exit after quit, terminating");
            Volatile.Write(ref _stopReason, (int)HostExitReason.Killed);
            Terminate(HostExitReason.Killed);
            await Task.WhenAny(_exitTcs.Task, Task.Delay(2000)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Остановить хост (quit, затем kill) и освободить канал. Аудиопоток к этому моменту не должен звать
    /// <see cref="AudioLink.Process"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch (Exception e) { Log($"Plug-in host {ProcessId}: stop failed: {e.Message}"); }
        _heartbeat.Dispose();
        _cts.Cancel();
        _pipe.Dispose();
        Audio.Dispose();
        _process.Dispose();
    }
}
