using Mixer.Audio.Interop;
using Mixer.Core.Sys;

namespace Mixer.Core.Apps;

public enum BindingState
{
    /// <summary>Приложение не запущено. Привязка (если уже стоит) хранится за exe и сработает при запуске.</summary>
    NotRunning,
    /// <summary>Процесс есть, но ещё не обращался к звуку: Set/Get отвечают E_INVALIDARG. Повтор на следующем такте.</summary>
    WaitingAudio,
    /// <summary>Приложение выводит в заглушку, звук идёт только через микшер.</summary>
    Bound,
    /// <summary>Привязку не удалось поставить или сбросить; повтор на следующем такте.</summary>
    Failed,
    /// <summary>Привязку надо снять, но приложение не запущено или ещё не обращалось к звуку: снимется, как только появится.</summary>
    ResetPending,
    /// <summary>«Забрать себе» нужен, но недоступен (нет годной заглушки или API).</summary>
    Unavailable,
}

public sealed record AppBinding(string Exe, BindingState State, string? Message = null)
{
    /// <summary>Привязка стоит, но активная сессия приложения ещё на прежнем устройстве: нужно перезапустить воспроизведение.</summary>
    public bool PlaysDirect { get; init; }
    /// <summary>Куда привязано (Bound): заглушка или выход прямого маршрута.</summary>
    public string? Target { get; init; }
}

/// <summary>
/// Режим «Забрать себе»: вывод приложений перенаправляется в заглушку, их звук остаётся только в захвате микшера.
/// Приложение на прямом маршруте привязывается не к заглушке, а прямо к своему выходу (v2).
/// Привязка хранится в Windows за exe и переживает и приложение, и микшер, поэтому каждое приложение с привязкой
/// записано в журнал boundApps <b>до</b> вызова Set и вычёркивается только после успешного сброса.
/// Вызывается из одного управляющего потока, без блокировок.
/// Тот же механизм назначает приложениям кабель микрофоном (micFor): политика устройства записи, свой журнал micApps,
/// без проверки «играет мимо» (checkPlayback — она про воспроизведение).
/// </summary>
public sealed class TakeOverManager(IAppEndpointPolicy policy, IBindingJournal journal, bool checkPlayback = true)
{
    public static readonly ERole[] Roles = [ERole.Console, ERole.Multimedia, ERole.Communications];

    /// <summary>exe → куда привязано и на каких PID это уже подтверждено.</summary>
    private readonly Dictionary<string, (string Target, HashSet<uint> Pids)> _applied = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppBinding> _status = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, AppBinding> Status => _status;

    /// <summary>
    /// Привести привязки к желаемому: desired — exe узлов с «Забрать себе», sinkId — проверенная заглушка
    /// (null — режим недоступен, все привязки снимаются). Остальное из журнала сбрасывается, если процесс жив.
    /// </summary>
    public void Reconcile(IEnumerable<string> desired, string? sinkId, AppSnapshot world, string? unavailableReason = null) =>
        Reconcile(desired.Select(ProcessSnapshot.NormalizeExe).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(e => e, _ => sinkId),
            world, unavailableReason);

    /// <summary>
    /// То же с целью для каждого exe: заглушка или выход прямого маршрута; null — режим для exe недоступен
    /// (unavailableReason), привязка снимается. partial — только поставить перечисленные привязки, ничего не снимая
    /// (переход на прямой маршрут до перестройки движка: сначала приложение уходит в выход, потом гаснет захват).
    /// </summary>
    public void Reconcile(IReadOnlyDictionary<string, string?> desired, AppSnapshot world, string? unavailableReason = null, bool partial = false)
    {
        var want = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, target) in desired) want.TryAdd(ProcessSnapshot.NormalizeExe(exe), target);
        if (!partial) _status.Clear();
        foreach (var (exe, target) in want)
        {
            if (target is not null) Bind(exe, target, world);
            else if (!partial) _status[exe] = new AppBinding(exe, BindingState.Unavailable, unavailableReason ?? CoreStrings.Bind_NoSink);
        }
        if (partial) return;
        foreach (string exe in journal.Apps.ToList())
        {
            if (want.GetValueOrDefault(exe) is not null) continue;
            var r = Release(exe, world);
            // недоступный режим не затирает объяснение у узла, но о сбросе тоже надо знать
            if (!_status.ContainsKey(exe) && r is not null) _status[exe] = r;
        }
    }

    /// <summary>Штатный выход: снять все привязки у живых процессов. Остальные остаются в журнале до следующего запуска.</summary>
    public void ReleaseAll(AppSnapshot world)
    {
        _status.Clear();
        foreach (string exe in journal.Apps.ToList())
        {
            var r = Release(exe, world);
            if (r is not null) _status[exe] = r;
        }
        _applied.Clear();
    }

    private void Bind(string exe, string target, AppSnapshot world)
    {
        var targets = world.BindingTargets(exe);
        if (targets.Count == 0)
        {
            _applied.Remove(exe);
            _status[exe] = new AppBinding(exe, BindingState.NotRunning, CoreStrings.Bind_NotRunning);
            return;
        }

        if (!journal.Apps.Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                journal.Add(exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // без записи в журнале привязку ставить нельзя: после падения её никто не снимет
                _status[exe] = new AppBinding(exe, BindingState.Failed, Loc.F(CoreStrings.Bind_JournalWriteFailedFormat, ex.Message));
                return;
            }
        }

        if (!_applied.TryGetValue(exe, out var entry) || !string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase))
            _applied[exe] = entry = (target, []); // цель сменилась: всё ставится заново
        var applied = entry.Pids;
        applied.IntersectWith(targets); // завершившиеся процессы забываем

        string? failure = null;
        foreach (uint pid in targets)
        {
            if (applied.Contains(pid)) continue;
            switch (Apply(pid, target, out string? error))
            {
                case ApplyResult.Ok: applied.Add(pid); break;
                case ApplyResult.Failed: failure ??= error; break;
            }
        }

        bool direct = checkPlayback && world.SessionsOf(exe).Any(s => s.IsActive && !string.Equals(s.EndpointId, target, StringComparison.OrdinalIgnoreCase));
        _status[exe] = failure is not null
            ? new AppBinding(exe, BindingState.Failed, Loc.F(CoreStrings.Bind_FailedFormat, failure))
            : applied.Count == 0
                ? new AppBinding(exe, BindingState.WaitingAudio, CoreStrings.Bind_NoAudioYet)
                : new AppBinding(exe, BindingState.Bound, direct ? CoreStrings.Bind_PlaysDirect : null) { PlaysDirect = direct, Target = target };
    }

    /// <summary>Сбросить привязку exe. null — сброшено и вычеркнуто из журнала.</summary>
    private AppBinding? Release(string exe, AppSnapshot world)
    {
        var targets = world.BindingTargets(exe);
        if (targets.Count == 0)
            return new AppBinding(exe, BindingState.ResetPending, CoreStrings.Bind_ResetOnNextStart);

        bool any = false;
        string? failure = null;
        foreach (uint pid in targets)
        {
            switch (Apply(pid, null, out string? error))
            {
                case ApplyResult.Ok: any = true; break;
                case ApplyResult.Failed: failure ??= error; break;
            }
        }
        if (failure is not null)
            return new AppBinding(exe, BindingState.Failed, Loc.F(CoreStrings.Bind_ReleaseFailedFormat, failure));
        if (!any)
            return new AppBinding(exe, BindingState.ResetPending, CoreStrings.Bind_ResetOnAudio);

        _applied.Remove(exe);
        try
        {
            journal.Remove(exe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // привязка уже снята; лишняя запись в журнале при следующем запуске даст лишь повторный сброс
            return new AppBinding(exe, BindingState.Failed, Loc.F(CoreStrings.Bind_ReleasedJournalFailedFormat, ex.Message));
        }
        return null;
    }

    private enum ApplyResult { Ok, NoAudioContext, Failed }

    /// <summary>Set для всех трёх ролей и сверка через Get.</summary>
    private ApplyResult Apply(uint pid, string? endpointId, out string? error)
    {
        error = null;
        foreach (var role in Roles)
        {
            int hrSet = policy.Set(pid, role, endpointId);
            int hrGet = policy.Get(pid, role, out string? got);
            if (hrSet == Hr.E_INVALIDARG && hrGet == Hr.E_INVALIDARG)
                return ApplyResult.NoAudioContext; // процесс без аудиоконтекста — не ошибка (п. 4)
            if (hrSet < 0 || hrGet < 0 || !string.Equals(got, endpointId, StringComparison.OrdinalIgnoreCase))
            {
                error = $"PID {pid}, {role}: Set {Hr.Describe(hrSet)}, Get {Hr.Describe(hrGet)} → {got ?? CoreStrings.Bind_DefaultEndpoint}";
                return ApplyResult.Failed;
            }
        }
        return ApplyResult.Ok;
    }
}
