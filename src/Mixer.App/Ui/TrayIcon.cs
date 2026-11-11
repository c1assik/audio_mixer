using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mixer.App.Ui;

/// <summary>Что показывает значок в трее.</summary>
public enum TrayState { Normal, MicOn, MicOff, Problem }

/// <summary>
/// Значок в области уведомлений (v2, «Трей и автозапуск») без WinForms: Shell_NotifyIcon и скрытое окно для
/// сообщений. Перезапуск Проводника (TaskbarCreated) возвращает значок на место. Всплывающие уведомления Windows —
/// через NIF_INFO.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 17;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 1, NIIF_WARNING = 2;

    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private IntPtr _icon;
    private string _tip = "";
    private bool _added;

    /// <summary>Щелчок левой кнопкой по значку.</summary>
    public event Action? Click;
    /// <summary>Щелчок правой кнопкой: точка курсора в пикселях экрана.</summary>
    public event Action<Point>? RightClick;

    public TrayIcon()
    {
        _window = new HwndSource(new HwndSourceParameters("Mixer.Tray") { WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>Окно значка (для SetForegroundWindow перед меню).</summary>
    public IntPtr Handle => _window.Handle;

    /// <summary>Показать или обновить значок. false — Windows не приняла значок (нет панели задач).</summary>
    public bool Show(TrayState state, string tip)
    {
        var icon = TrayIcons.Create(state);
        var old = _icon;
        _icon = icon;
        _tip = tip;
        bool ok = Notify(_added ? NIM_MODIFY : NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP);
        if (!_added && ok) _added = true;
        if (old != IntPtr.Zero) DestroyIcon(old);
        return ok;
    }

    /// <summary>Уведомление Windows у значка.</summary>
    public void Balloon(string title, string text, bool warning = false)
    {
        if (!_added) return;
        var d = Data(NIF_INFO);
        d.szInfoTitle = Trim(title, 63);
        d.szInfo = Trim(text, 255);
        d.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    private bool Notify(uint message, uint flags)
    {
        var d = Data(flags);
        return Shell_NotifyIcon(message, ref d);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = Trim(_tip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case WM_LBUTTONUP or WM_LBUTTONDBLCLK:
                    Click?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    GetCursorPos(out var p);
                    RightClick?.Invoke(new Point(p.X, p.Y));
                    break;
            }
            handled = true;
        }
        else if (_taskbarCreated != 0 && msg == (int)_taskbarCreated && _added)
        {
            // Проводник перезапустился: значок надо добавить заново
            _added = false;
            Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP);
            _added = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added) Notify(NIM_DELETE, 0);
        _added = false;
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint p);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);
}

/// <summary>
/// Значок трея рисуется из логотипа шапки: синий квадрат с двумя кривыми, в углу — состояние главного микрофона
/// (зелёная точка — включён, красная с чертой — выключен) или жёлтый знак проблемы (узел в ошибке).
/// </summary>
public static class TrayIcons
{
    private static readonly Color Accent = Color.FromRgb(0x4F, 0xA3, 0xFF);
    private static readonly Color Ink = Color.FromRgb(0x0E, 0x11, 0x16);
    private static readonly Color Green = Color.FromRgb(0x3D, 0xCC, 0x91);
    private static readonly Color Red = Color.FromRgb(0xF2, 0x5C, 0x54);
    private static readonly Color Yellow = Color.FromRgb(0xFF, 0xC4, 0x4D);

    /// <summary>Пиксели значка 32×32, BGRA без премультипликации (для тестов и CreateIconIndirect).</summary>
    public static byte[] Pixels(TrayState state, int size = 32)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double s = size / 32.0;
            dc.DrawRoundedRectangle(new SolidColorBrush(Accent), null, new Rect(1 * s, 1 * s, 30 * s, 30 * s), 7 * s, 7 * s);
            var logo = Geometry.Parse("M4 7c6 0 10 10 16 10M4 17c6 0 10-10 16-10");
            dc.PushTransform(new MatrixTransform(s, 0, 0, s, 4 * s, 4 * s));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Ink), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, logo);
            dc.Pop();
            var badge = new Point(24.5 * s, 24.5 * s);
            switch (state)
            {
                case TrayState.MicOn:
                    dc.DrawEllipse(new SolidColorBrush(Green), new Pen(new SolidColorBrush(Ink), 2 * s), badge, 6.5 * s, 6.5 * s);
                    break;
                case TrayState.MicOff:
                    dc.DrawEllipse(new SolidColorBrush(Red), new Pen(new SolidColorBrush(Ink), 2 * s), badge, 6.5 * s, 6.5 * s);
                    dc.DrawLine(new Pen(Brushes.White, 2 * s), new Point(20.5 * s, 28.5 * s), new Point(28.5 * s, 20.5 * s));
                    break;
                case TrayState.Problem:
                    dc.PushTransform(new ScaleTransform(s, s));
                    dc.DrawGeometry(new SolidColorBrush(Yellow), new Pen(new SolidColorBrush(Ink), 1.5), Geometry.Parse("M24.5 17 L32 31 L17 31 Z"));
                    dc.Pop();
                    break;
            }
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var px = new byte[size * size * 4];
        bmp.CopyPixels(px, size * 4, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            byte a = px[i + 3];
            if (a is 0 or 255) continue;
            px[i] = (byte)Math.Min(255, px[i] * 255 / a);
            px[i + 1] = (byte)Math.Min(255, px[i + 1] * 255 / a);
            px[i + 2] = (byte)Math.Min(255, px[i + 2] * 255 / a);
        }
        return px;
    }

    /// <summary>HICON значка (освобождать DestroyIcon).</summary>
    public static IntPtr Create(TrayState state)
    {
        int size = Math.Clamp(GetSystemMetrics(49 /* SM_CXSMICON */), 16, 64);
        byte[] px = Pixels(state, size);
        IntPtr color = CreateBitmap(size, size, 1, 32, px);
        IntPtr mask = CreateBitmap(size, size, 1, 1, new byte[(size + 15) / 16 * 2 * size]);
        var info = new IconInfo { fIcon = true, hbmColor = color, hbmMask = mask };
        IntPtr icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bits, byte[] data);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
