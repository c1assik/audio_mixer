using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Mixer.App.Ui;

/// <summary>
/// Глобальные горячие клавиши через RegisterHotKey (v2, «Горячие клавиши»): работают и в полноэкранной игре, хука
/// клавиатуры для них не нужно. Окно — только для сообщений (HWND_MESSAGE), в потоке интерфейса.
/// Занятое другой программой сочетание RegisterHotKey не отдаёт — это и есть проверка конфликта при назначении.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ProbeId = 0xBFFE;

    private readonly HwndSource _window;
    private readonly HashSet<int> _ids = [];

    /// <summary>Нажато зарегистрированное сочетание (ID из <see cref="Register"/>).</summary>
    public event Action<int>? Pressed;

    public GlobalHotkeys()
    {
        var p = new HwndSourceParameters("Mixer.Hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 }; // HWND_MESSAGE
        _window = new HwndSource(p);
        _window.AddHook(WndProc);
    }

    /// <summary>Зарегистрировать сочетание. false — его держит другая программа (или Windows).</summary>
    /// <param name="repeat">Повторять, пока клавиша зажата (громкость ±1 дБ); иначе одно срабатывание на нажатие.</param>
    public bool Register(int id, KeyChord chord, bool repeat = false)
    {
        if (_ids.Contains(id)) Unregister(id);
        if (!RegisterHotKey(_window.Handle, id, chord.NativeModifiers | (repeat ? 0 : MOD_NOREPEAT), chord.VirtualKey)) return false;
        _ids.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_ids.Remove(id)) UnregisterHotKey(_window.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (int id in _ids.ToList()) Unregister(id);
    }

    /// <summary>Свободно ли сочетание в Windows: пробная регистрация и сразу снятие.</summary>
    public bool IsFree(KeyChord chord)
    {
        if (!RegisterHotKey(_window.Handle, ProbeId, chord.NativeModifiers | MOD_NOREPEAT, chord.VirtualKey)) return false;
        UnregisterHotKey(_window.Handle, ProbeId);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_ids.Contains(id))
            {
                // после выхода из обработчика сообщения: действие может открыть окно или показать всплывашку
                Dispatcher.CurrentDispatcher.BeginInvoke(() => Pressed?.Invoke(id));
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>
/// Push-to-talk и push-to-mute: RegisterHotKey не сообщает отпускание клавиши, поэтому нужен хук WH_KEYBOARD_LL. Он
/// ставится, только когда задана такая клавиша, и живёт в своём потоке с циклом сообщений: обработчик хука не ждёт
/// интерфейс (иначе Windows молча снимет хук по таймауту). Клавиши не перехватываются — только наблюдаются.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_QUIT = 0x0012;

    private readonly Dispatcher _ui;
    private readonly Thread _thread;
    private readonly HookProc _proc;
    private volatile KeyChord[] _chords;
    private readonly HashSet<KeyChord> _held = [];
    private uint _threadId;
    private IntPtr _hook;

    /// <summary>Сочетание зажато (true) или отпущено (false). В потоке интерфейса.</summary>
    public event Action<KeyChord, bool>? Changed;

    /// <summary>Хук поставлен (Windows могла отказать).</summary>
    public bool Installed { get; private set; }

    public KeyboardHook(IEnumerable<KeyChord> chords)
    {
        _ui = Dispatcher.CurrentDispatcher;
        _chords = chords.ToArray();
        _proc = Proc;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            Installed = _hook != IntPtr.Zero;
            ready.Set();
            if (!Installed) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "Mixer push-to-talk hook" };
        _thread.Start();
        ready.Wait(2000);
    }

    /// <summary>Сменить набор сочетаний без переустановки хука.</summary>
    public void SetChords(IEnumerable<KeyChord> chords) => _chords = chords.ToArray();

    private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            uint vk = (uint)Marshal.ReadInt32(lParam);
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN, up = msg is WM_KEYUP or WM_SYSKEYUP;
            if (down || up)
            {
                foreach (var c in _chords)
                {
                    if (c.VirtualKey != vk) continue;
                    if (down && !_held.Contains(c) && ModifiersHeld(c))
                    {
                        _held.Add(c);
                        _ui.BeginInvoke(() => Changed?.Invoke(c, true));
                    }
                    else if (up && _held.Remove(c))
                    {
                        _ui.BeginInvoke(() => Changed?.Invoke(c, false));
                    }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool ModifiersHeld(KeyChord c)
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool ctrl = Down(0x11), alt = Down(0x12), shift = Down(0x10), win = Down(0x5B) || Down(0x5C);
        var m = c.Modifiers;
        return ctrl == m.HasFlag(System.Windows.Input.ModifierKeys.Control) && alt == m.HasFlag(System.Windows.Input.ModifierKeys.Alt)
               && shift == m.HasFlag(System.Windows.Input.ModifierKeys.Shift) && win == m.HasFlag(System.Windows.Input.ModifierKeys.Windows);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
