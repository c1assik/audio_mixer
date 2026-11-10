using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Mixer.Core.Sys;

namespace Mixer.App.Ui;

/// <summary>Иконки приложений по пути exe (SHGetFileInfo). Только из потока интерфейса.</summary>
public static class AppIcons
{
    private static readonly Dictionary<string, ImageSource?> ByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> PathByExe = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Запомнить путь exe (из каталога палитры), чтобы узел нашёл иконку и без запущенного процесса.</summary>
    public static void Remember(string exe, string? path)
    {
        if (path is not null) PathByExe[exe] = path;
    }

    public static ImageSource? ForExe(string exe)
    {
        if (!PathByExe.TryGetValue(exe, out string? path))
        {
            path = FindRunning(exe);
            if (path is null) return null;
            PathByExe[exe] = path;
        }
        return FromPath(path);
    }

    private static string? FindRunning(string exe)
    {
        var procs = Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(exe));
        try
        {
            foreach (var p in procs)
                if (ProcessDetails.TryGetPath((uint)p.Id) is { } path) return path;
            return null;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public static ImageSource? FromPath(string? path)
    {
        if (path is null) return null;
        if (ByPath.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        var info = new SHFILEINFOW();
        if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_LARGEICON) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                img = bmp;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        ByPath[path] = img;
        return img;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref SHFILEINFOW info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}

/// <summary>Системный акцент, тёмный заголовок окна, второй экземпляр.</summary>
public static class WindowsLook
{
    /// <summary>Акцентный цвет Windows (DWM AccentColor, ABGR); слишком тёмный осветляется для тёмной темы.</summary>
    public static Color? SystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is not int abgr) return null;
            byte r = (byte)(abgr & 0xFF), g = (byte)((abgr >> 8) & 0xFF), b = (byte)((abgr >> 16) & 0xFF);
            double lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
            if (lum < 0.45)
            {
                double k = 0.45 / Math.Max(lum, 0.05);
                (r, g, b) = (Lift(r, k), Lift(g, k), Lift(b, k));
            }
            return Color.FromRgb(r, g, b);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    private static byte Lift(byte c, double k) => (byte)Math.Min(255, c * k + 24);

    /// <summary>Подставить системный акцент в ресурсы темы.</summary>
    public static void ApplyAccent(ResourceDictionary resources)
    {
        if (SystemAccent() is not { } c) return;
        resources["AccentColor"] = c;
        resources["Accent"] = new SolidColorBrush(c);
    }

    public static void DarkTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    /// <summary>Вывести на передний план окно уже запущенного микшера.</summary>
    public static void ActivateOther()
    {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
