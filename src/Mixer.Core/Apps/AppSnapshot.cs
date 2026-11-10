using Mixer.Audio.Interop;
using Mixer.Core.Sys;

namespace Mixer.Core.Apps;

/// <summary>Процессы и аудиосессии в один момент: из них считаются цели захвата, привязки и сторожевого таймера.</summary>
public sealed class AppSnapshot(ProcessSnapshot processes, IReadOnlyList<AudioSessionInfo> sessions)
{
    public ProcessSnapshot Processes { get; } = processes;
    public IReadOnlyList<AudioSessionInfo> Sessions { get; } = sessions;

    public List<uint> Roots(string exe) => Processes.Roots(exe).Select(p => p.Pid).ToList();

    public HashSet<uint> TreeOf(string exe) => Processes.Tree(Roots(exe));

    public IEnumerable<AudioSessionInfo> SessionsOf(string exe)
    {
        var tree = TreeOf(exe);
        return Sessions.Where(s => tree.Contains(s.Pid));
    }

    /// <summary>
    /// PID, на которые ставится и сбрасывается привязка: процессы дерева с аудиосессиями плюс корни
    /// (спецификация, «Проблема двойного звука», п. 4).
    /// </summary>
    public List<uint> BindingTargets(string exe)
    {
        var roots = Roots(exe);
        var tree = Processes.Tree(roots);
        return Sessions.Where(s => tree.Contains(s.Pid)).Select(s => s.Pid)
            .Concat(roots).Distinct().OrderBy(p => p).ToList();
    }

    /// <summary>Есть ли активная сессия у дерева процесса root.</summary>
    public bool TreeHasActiveSession(uint root)
    {
        var tree = Processes.Tree([root]);
        return Sessions.Any(s => s.IsActive && tree.Contains(s.Pid));
    }

    /// <summary>Есть ли активная сессия у кого-нибудь, кроме дерева микшера (для «Весь звук системы»).</summary>
    public bool OthersHaveActiveSession()
    {
        var self = Processes.Tree([Processes.SelfPid]);
        return Sessions.Any(s => s.IsActive && s.Pid != 0 && !self.Contains(s.Pid));
    }
}
