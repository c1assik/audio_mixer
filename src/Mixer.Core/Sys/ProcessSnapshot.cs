using System.Runtime.InteropServices;

namespace Mixer.Core.Sys;

public sealed record ProcInfo(uint Pid, uint ParentPid, string Exe);

/// <summary>
/// Снимок процессов (Toolhelp32): нужен PID родителя, чтобы найти корень дерева многопроцессного приложения.
/// Сам микшер (SelfPid) в выборки по exe не попадает: свой процесс захватывать или перенаправлять нельзя.
/// </summary>
public sealed class ProcessSnapshot
{
    public IReadOnlyDictionary<uint, ProcInfo> ByPid { get; }
    public uint SelfPid { get; }

    private ILookup<uint, uint>? _children;

    public ProcessSnapshot(IEnumerable<ProcInfo> processes, uint selfPid)
    {
        var map = new Dictionary<uint, ProcInfo>();
        foreach (var p in processes) map[p.Pid] = p;
        ByPid = map;
        SelfPid = selfPid;
    }

    public static ProcessSnapshot Take()
    {
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            throw new InvalidOperationException($"CreateToolhelp32Snapshot: error {Marshal.GetLastWin32Error()}");
        try
        {
            var list = new List<ProcInfo>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref entry))
            {
                do list.Add(new ProcInfo(entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile));
                while (Process32NextW(snap, ref entry));
            }
            return new ProcessSnapshot(list, (uint)Environment.ProcessId);
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    /// <summary>«Discord» → «discord.exe»: журнал и узлы хранят имя exe в нижнем регистре.</summary>
    public static string NormalizeExe(string exe)
    {
        string name = Path.GetFileName(exe.Trim()).ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }

    /// <summary>Все процессы exe, кроме микшера.</summary>
    public List<ProcInfo> All(string exe)
    {
        string name = NormalizeExe(exe);
        return ByPid.Values.Where(p => p.Pid != SelfPid && p.Exe.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Процессы exe, чей родитель не тот же exe: корни деревьев многопроцессных приложений (Chrome, Discord).</summary>
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

    /// <summary>PID корней и всех их потомков.</summary>
    public HashSet<uint> Tree(IEnumerable<uint> roots)
    {
        _children ??= ByPid.Values.Where(p => p.ParentPid != p.Pid).ToLookup(p => p.ParentPid, p => p.Pid);
        var result = new HashSet<uint>();
        var queue = new Queue<uint>(roots);
        while (queue.Count > 0)
        {
            uint pid = queue.Dequeue();
            if (!result.Add(pid)) continue;
            foreach (uint c in _children[pid]) queue.Enqueue(c);
        }
        return result;
    }

    public string ExeOf(uint pid) => ByPid.TryGetValue(pid, out var p) ? p.Exe : "?";

    // ── Toolhelp32 ──

    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
