using System.Runtime.InteropServices;
using Mixer.Probe.Interop;

namespace Mixer.Probe.Sys;

public sealed record ProcInfo(uint Pid, uint ParentPid, string Exe);

/// <summary>Снимок процессов через Toolhelp32: нужен PID родителя, чтобы найти корень дерева.</summary>
public sealed class ProcessSnapshot
{
    public IReadOnlyDictionary<uint, ProcInfo> ByPid { get; }

    private ProcessSnapshot(Dictionary<uint, ProcInfo> byPid) => ByPid = byPid;

    public static ProcessSnapshot Take()
    {
        IntPtr snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            throw new AudioException("CreateToolhelp32Snapshot", Marshal.GetHRForLastWin32Error());
        try
        {
            var map = new Dictionary<uint, ProcInfo>();
            var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (NativeMethods.Process32FirstW(snap, ref entry))
            {
                do
                {
                    map[entry.th32ProcessID] = new ProcInfo(entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile);
                } while (NativeMethods.Process32NextW(snap, ref entry));
            }
            return new ProcessSnapshot(map);
        }
        finally
        {
            NativeMethods.CloseHandle(snap);
        }
    }

    public static string NormalizeExe(string exe) =>
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";

    /// <summary>Все процессы exe, кроме самой утилиты: свой процесс захватывать или перенаправлять нельзя.</summary>
    public List<ProcInfo> All(string exe)
    {
        string name = NormalizeExe(exe);
        uint self = (uint)Environment.ProcessId;
        return ByPid.Values.Where(p => p.Pid != self && p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Процессы exe, чей родитель не тот же exe: корни деревьев многопроцессных приложений.</summary>
    public List<ProcInfo> Roots(string exe)
    {
        string name = NormalizeExe(exe);
        return All(exe)
            .Where(p => !(p.ParentPid != p.Pid
                          && ByPid.TryGetValue(p.ParentPid, out var parent)
                          && parent.Exe.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Pid)
            .ToList();
    }

    public HashSet<uint> Tree(IEnumerable<uint> roots)
    {
        var children = ByPid.Values
            .Where(p => p.ParentPid != p.Pid)
            .ToLookup(p => p.ParentPid, p => p.Pid);
        var result = new HashSet<uint>();
        var queue = new Queue<uint>(roots);
        while (queue.Count > 0)
        {
            uint pid = queue.Dequeue();
            if (!result.Add(pid)) continue;
            foreach (uint c in children[pid]) queue.Enqueue(c);
        }
        return result;
    }

    public string ExeOf(uint pid) => ByPid.TryGetValue(pid, out var p) ? p.Exe : "?";
}
