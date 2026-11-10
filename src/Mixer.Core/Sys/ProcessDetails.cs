using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mixer.Core.Sys;

/// <summary>Путь к exe, его описание и процессы с окнами — для палитры «Добавить источник».</summary>
public static class ProcessDetails
{
    /// <summary>Полный путь к exe процесса или null (нет прав, процесс завершился).</summary>
    public static string? TryGetPath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>«Google Chrome» из FileDescription, иначе имя файла без .exe.</summary>
    public static string Describe(string exe, string? path)
    {
        if (path is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                string? d = info.FileDescription?.Trim();
                if (!string.IsNullOrEmpty(d) && !d.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return d;
                d = info.ProductName?.Trim();
                if (!string.IsNullOrEmpty(d)) return d;
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
            {
            }
        }
        string name = Path.GetFileNameWithoutExtension(exe);
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : exe;
    }

    /// <summary>PID процессов с видимым окном верхнего уровня, у которого есть заголовок (приложения, а не службы).</summary>
    public static HashSet<uint> WindowOwners()
    {
        var pids = new HashSet<uint>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            if (GetWindowTextLengthW(hwnd) == 0) return true;
            long ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0) pids.Add(pid);
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
