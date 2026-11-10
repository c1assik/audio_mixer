using Mixer.Core.Sys;

namespace Mixer.Core.Apps;

/// <summary>Процесс, чьё дерево захватывает «Весь звук системы» (process loopback, INCLUDE_TARGET_PROCESS_TREE).</summary>
public sealed record SystemTarget(uint Pid, string Exe);

/// <param name="Targets">Что захватывать: по захвату на дерево.</param>
/// <param name="Excluded">Приложения со своим узлом, которые сейчас запущены и из «Весь звук системы» убраны.</param>
/// <param name="Skipped">Процессы со звуком, которые захватить нельзя: в их дереве микшер или приложение со своим узлом.</param>
public sealed record SystemPlan(IReadOnlyList<SystemTarget> Targets, IReadOnlyList<string> Excluded, IReadOnlyList<string> Skipped);

/// <summary>
/// «Весь звук системы» без приложений, у которых на схеме свой узел. Режим exclude у process loopback принимает
/// только один PID — им занят микшер (иначе петля). Поэтому, если на схеме есть приложения, узел собирается иначе:
/// как сумма include-захватов всех процессов с аудиосессией, кроме микшера и этих приложений (спецификация,
/// «Почему в v1 нет узла „Всё, кроме приложения“»). Новые сессии подхватываются наблюдателем процессов.
/// <para>
/// Include захватывает всё дерево процесса, исключить из него поддерево нельзя. Процесс, в дереве которого есть
/// микшер или исключённое приложение (обычно explorer.exe, запустивший их), не захватывается вовсе: иначе петля
/// или двойной звук. Такие процессы перечислены в <see cref="SystemPlan.Skipped"/>.
/// </para>
/// </summary>
public static class SystemCapture
{
    /// <summary>Больше захватов не открываем: на этапе 0 проверено до 12 клиентов в одном процессе.</summary>
    public const int MaxCaptures = 24;

    /// <param name="excludedExes">Нормализованные exe узлов «Приложение» на схеме.</param>
    public static SystemPlan Plan(AppSnapshot world, IReadOnlyCollection<string> excludedExes)
    {
        var procs = world.Processes;
        var banned = procs.Tree([procs.SelfPid]);
        var excluded = new List<string>();
        foreach (string exe in excludedExes.Select(ProcessSnapshot.NormalizeExe).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tree = world.TreeOf(exe);
            if (tree.Count == 0) continue;
            excluded.Add(exe);
            banned.UnionWith(tree);
        }

        bool Clean(uint pid) => !procs.Tree([pid]).Overlaps(banned);

        var picked = new Dictionary<uint, bool>(); // корень захвата → есть ли активная сессия
        var skipped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in world.Sessions)
        {
            if (s.Pid == 0 || s.State == Mixer.Audio.Interop.AudioSessionState.Expired) continue;
            if (!procs.ByPid.TryGetValue(s.Pid, out var p) || banned.Contains(s.Pid)) continue;
            if (!Clean(s.Pid))
            {
                skipped.Add(ProcessSnapshot.NormalizeExe(p.Exe));
                continue;
            }
            // поднимаемся к корню того же exe (у Chrome звук идёт из дочернего процесса), пока дерево чистое
            uint target = s.Pid;
            var seen = new HashSet<uint> { target };
            while (procs.ByPid.TryGetValue(target, out var cur)
                   && procs.ByPid.TryGetValue(cur.ParentPid, out var parent)
                   && seen.Add(parent.Pid)
                   && parent.Exe.Equals(cur.Exe, StringComparison.OrdinalIgnoreCase)
                   && !banned.Contains(parent.Pid) && Clean(parent.Pid))
                target = parent.Pid;
            picked[target] = picked.GetValueOrDefault(target) || s.IsActive;
        }

        // дерево, уже попавшее в чужой захват, второй раз не берём
        var roots = picked.Keys.ToList();
        var nested = new HashSet<uint>();
        foreach (uint r in roots)
            foreach (uint d in procs.Tree([r]))
                if (d != r && picked.ContainsKey(d)) nested.Add(d);

        var targets = roots.Where(r => !nested.Contains(r))
            .OrderByDescending(r => picked[r]).ThenBy(r => r)
            .ToList();
        foreach (uint r in targets.Skip(MaxCaptures)) skipped.Add(Loc.F(CoreStrings.System_CaptureLimitFormat, ProcessSnapshot.NormalizeExe(procs.ExeOf(r))));
        return new SystemPlan(
            targets.Take(MaxCaptures).OrderBy(r => r).Select(r => new SystemTarget(r, ProcessSnapshot.NormalizeExe(procs.ExeOf(r)))).ToList(),
            excluded,
            skipped.ToList());
    }
}
