using System.Buffers.Binary;
using System.IO.Compression;
using Mixer.Plugins.Host;

namespace Mixer.PluginBench;

/// <summary>
/// Этап 1: ручная проверка окна редактора. Загружает плагин, открывает его окно (владелец — окно консоли, если
/// есть), печатает события канала управления и геометрию окна, снимает окно в PNG, закрывает и печатает, что
/// хост запомнил бы для слота.
/// </summary>
internal static unsafe class EditorCheck
{
    public static int Run(string hostExe, string plugin, double seconds, string? png, EditorBounds? bounds, bool close)
    {
        using var host = PluginHost.Start(new PluginHostOptions { HostExe = hostExe, Log = s => Console.WriteLine($"  log: {s}") });
        host.ParamChanged += v => Console.WriteLine($"  param {v.Id} = {v.Value:0.####} «{v.Text}»");
        host.Dirty += () => Console.WriteLine("  dirty");
        host.LayoutChanged += l => Console.WriteLine($"  latencyChanged {l.LatencySamples}");
        var opened = new TaskCompletionSource<EditorBounds>();
        var closed = new TaskCompletionSource<EditorBounds>();
        host.EditorOpened += b => { Console.WriteLine($"  editorOpened {b}"); opened.TrySetResult(b); };
        host.EditorClosed += b => { Console.WriteLine($"  editorClosed {b}"); closed.TrySetResult(b); };
        host.EditorFailed += m => { Console.WriteLine($"  editorFailed {m}"); opened.TrySetException(new InvalidOperationException(m)); };

        var info = host.LoadAsync(PluginLoad.Vst3(plugin)).GetAwaiter().GetResult();
        Console.WriteLine($"{info.Name} · {info.Vendor} · {info.Version} · {info.ClassId}");
        Console.WriteLine($"  задержка {info.LatencySamples}, каналы {info.InChannels}→{info.OutChannels}, окно: {(info.HasEditor ? "есть" : "нет")}, параметров {info.Params.Count}");
        foreach (var p in info.Params.Take(20))
            Console.WriteLine($"    {p.Id,8} {p.Title,-24} {p.Text} {p.Units}{(p.ReadOnly ? " [только чтение]" : "")}{(p.Hidden ? " [скрытый]" : "")}{(p.Bypass ? " [обход]" : "")}");

        // владелец — своё окно в этом процессе (как главное окно микшера): проверить, что редактор над ним
        // и прячется, когда его сворачивают
        using var ownerWindow = OwnerWindow.Create();
        IntPtr owner = ownerWindow.Handle;
        host.OpenEditor(owner, $"{info.Name} — проверка", bounds);
        opened.Task.Wait(TimeSpan.FromSeconds(10));
        Thread.Sleep(1500); // дать плагину нарисоваться

        IntPtr wnd = FindEditor(host.ProcessId);
        if (wnd == IntPtr.Zero)
        {
            Console.WriteLine("окно редактора не найдено");
            return 1;
        }
        Native.GetWindowRect(wnd, out var wr);
        Native.GetClientRect(wnd, out var cr);
        IntPtr realOwner = Native.GetWindow(wnd, Native.GW_OWNER);
        Console.WriteLine($"  окно 0x{wnd:X}: {Title(wnd)} · рамка {wr.Left},{wr.Top} {wr.Right - wr.Left}×{wr.Bottom - wr.Top} · " +
                          $"содержимое {cr.Right}×{cr.Bottom} · DPI {Native.GetDpiForWindow(wnd)} · владелец 0x{realOwner:X} (ждали 0x{owner:X})");
        if (png != null)
        {
            SavePng(wnd, wr.Right - wr.Left, wr.Bottom - wr.Top, png);
            Console.WriteLine($"  снимок: {Path.GetFullPath(png)}");
        }
        ownerWindow.Minimize();
        Thread.Sleep(500);
        bool hiddenWithOwner = Native.IsWindowVisible(wnd) == 0;
        ownerWindow.Restore();
        Thread.Sleep(500);
        bool backWithOwner = Native.IsWindowVisible(wnd) != 0;
        Console.WriteLine($"  владелец свёрнут → окно скрыто: {(hiddenWithOwner ? "да" : "НЕТ")}; развёрнут → окно видно: {(backWithOwner ? "да" : "НЕТ")}");

        if (close) host.CloseEditor();
        else Console.WriteLine($"  закройте окно (ждём до {seconds:0} с)");
        closed.Task.Wait(TimeSpan.FromSeconds(close ? 5 : seconds));
        if (!closed.Task.IsCompleted)
        {
            host.CloseEditor();
            closed.Task.Wait(TimeSpan.FromSeconds(5));
        }
        var state = host.GetStateAsync().GetAwaiter().GetResult();
        Console.WriteLine($"  состояние: {state.Length} байт");
        return closed.Task.IsCompleted ? 0 : 1;
    }

    /// <summary>Простое верхнее окно на своём потоке с очередью сообщений — замена главному окну микшера.</summary>
    private sealed class OwnerWindow : IDisposable
    {
        private static readonly Native.WndProc s_proc = (h, m, w, l) => Native.DefWindowProcW(h, m, w, l);
        private readonly Thread _thread;
        private uint _threadId;
        public IntPtr Handle { get; private set; }

        private OwnerWindow()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = Native.GetCurrentThreadId();
                fixed (char* cls = "MixerBenchOwner")
                {
                    var wc = new Native.WNDCLASSW { lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(s_proc), lpszClassName = cls };
                    Native.RegisterClassW(ref wc);
                    Handle = Native.CreateWindowExW(0, "MixerBenchOwner", "Mixer (владелец)", Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE,
                        200, 200, 500, 350, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }
                ready.Set();
                while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessageW(ref msg);
                }
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
        }

        public static OwnerWindow Create() => new();
        public void Minimize() => Native.ShowWindowAsync(Handle, 6 /* SW_MINIMIZE */);
        public void Restore() => Native.ShowWindowAsync(Handle, 9 /* SW_RESTORE */);

        public void Dispose()
        {
            Native.PostMessageW(Handle, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            Native.PostThreadMessageW(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
        }
    }

    private static IntPtr FindEditor(int pid)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out int p);
            if (p != pid || Native.IsWindowVisible(h) == 0) return 1;
            char* name = stackalloc char[64];
            int n = Native.GetClassNameW(h, name, 64);
            if (new string(name, 0, n) != "MixerPluginEditor") return 1;
            found = h;
            return 0;
        }, IntPtr.Zero);
        return found;
    }

    private static string Title(IntPtr h)
    {
        char* text = stackalloc char[256];
        int n = Native.GetWindowTextW(h, text, 256);
        return new string(text, 0, n);
    }

    private static void SavePng(IntPtr wnd, int w, int h, string path)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr dc = Native.CreateCompatibleDC(screen);
        IntPtr bmp = Native.CreateCompatibleBitmap(screen, w, h);
        IntPtr old = Native.SelectObject(dc, bmp);
        Native.PrintWindow(wnd, dc, Native.PW_RENDERFULLCONTENT);
        Native.SelectObject(dc, old);
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Native.BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var bgra = new byte[w * h * 4];
        fixed (byte* p = bgra) Native.GetDIBits(dc, bmp, 0, (uint)h, p, ref bi, 0);
        Native.DeleteObject(bmp);
        Native.DeleteDC(dc);
        Native.ReleaseDC(IntPtr.Zero, screen);

        // PNG: RGB, без фильтров, zlib
        var raw = new byte[h * (1 + w * 3)];
        for (int y = 0, o = 0; y < h; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                raw[o++] = bgra[i + 2];
                raw[o++] = bgra[i + 1];
                raw[o++] = bgra[i];
            }
        }
        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;  // бит на канал
        ihdr[9] = 2;  // RGB
        Chunk(file, "IHDR", ihdr);
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            Chunk(file, "IDAT", ms.ToArray());
        }
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(t, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        var c = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }
}
