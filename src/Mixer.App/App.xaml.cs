using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.App.Views;
using Mixer.Core.Config;
using Mixer.Core.Host;
using Mixer.Core.Sys;

namespace Mixer.App;

/// <summary>Параметры запуска (для разработки; обычный запуск — без них).</summary>
public sealed record AppOptions
{
    /// <summary>Свой graph.json вместо %AppData%\Mixer\graph.json.</summary>
    public string? GraphPath { get; init; }
    /// <summary>Интерфейс без движка: устройства не открываются, привязки не трогаются.</summary>
    public bool NoAudio { get; init; }
    /// <summary>Снять окно в PNG через пару секунд после запуска и выйти.</summary>
    public string? Snapshot { get; init; }
    /// <summary>Открыть при запуске: palette-sources, palette-outputs, settings.</summary>
    public string? Open { get; init; }
    /// <summary>Своя папка журнала вместо %LocalAppData%\Mixer\logs.</summary>
    public string? LogDir { get; init; }
    /// <summary>Язык интерфейса: en, ru; без параметра — из настроек (settings.json), а там по умолчанию — по языку Windows.</summary>
    public string? Language { get; init; }
    /// <summary>Запуск свёрнутым в трей (автозапуск): окно не показывается, схема звучит.</summary>
    public bool Minimized { get; init; }
    /// <summary>Тема на этот запуск (graphite, daylight, modular) вместо выбранной в настройках.</summary>
    public string? Theme { get; init; }
    /// <summary>Трей и горячие клавиши и в режиме --no-audio (проверка без звука).</summary>
    public bool Tray { get; init; }
    /// <summary>Только проверить хост плагинов (распаковка, запуск, один блок) и выйти: код 0 — работает.</summary>
    public bool CheckPluginHost { get; init; }

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            o = args[i] switch
            {
                "--graph" => o with { GraphPath = Next() },
                "--no-audio" => o with { NoAudio = true },
                "--minimized" => o with { Minimized = true },
                "--tray" => o with { Tray = true },
                "--check-plugin-host" => o with { CheckPluginHost = true },
                "--theme" => o with { Theme = Next() },
                "--snapshot" => o with { Snapshot = Next() },
                "--open" => o with { Open = Next() },
                "--log-dir" => o with { LogDir = Next() },
                "--lang" => o with { Language = Next() },
                _ => o,
            };
        }
        return o;
    }

    public static string DefaultGraphPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mixer", "graph.json");

    /// <summary>
    /// Культура интерфейса: русская, если Windows на русском (или --lang ru), иначе английская. Числа форматируются
    /// по ней же: «−3,0 дБ» и «−3.0 dB».
    /// </summary>
    public static CultureInfo PickCulture(string? language, CultureInfo windows)
    {
        string lang = language?.Trim().ToLowerInvariant() switch
        {
            "ru" or "en" => language.Trim().ToLowerInvariant(),
            _ => windows.TwoLetterISOLanguageName == "ru" ? "ru" : "en",
        };
        return new CultureInfo(lang == "ru" ? "ru-RU" : "en-US");
    }
}

public partial class App : Application
{
    private Mutex? _mutex;
    private GraphStore? _store;
    private MixerHost? _host;
    private MainViewModel? _vm;
    private FileLog? _log;
    private string[] _args = [];
    private bool _restart;

    /// <summary>Журнал программы (null до запуска и в тестах).</summary>
    public static FileLog? Log => (Current as App)?._log;

    protected override void OnStartup(StartupEventArgs e)
    {
        _args = e.Args;
        var opt = AppOptions.Parse(e.Args);
        string path = opt.GraphPath ?? AppOptions.DefaultGraphPath;
        // язык: --lang, иначе выбранный в настройках, иначе как в Windows
        var prefs = Preferences.Load(Preferences.PathFor(path), CultureInfo.CurrentUICulture, out string? prefsError);
        ApplyCulture(AppOptions.PickCulture(opt.Language ?? prefs.Language, prefs.WindowsCulture));
        base.OnStartup(e);
        _log = new FileLog(opt.LogDir ?? FileLog.DefaultDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            if (a.ExceptionObject is Exception ex) _log?.Error("crash", ex);
            _log?.Dispose(); // процесс сейчас упадёт: дописать журнал
        };
        TaskScheduler.UnobservedTaskException += (_, a) => _log?.Error("task", a.Exception);

        if (opt.CheckPluginHost)
        {
            var log = _log;
            string? failure = Task.Run(() => Mixer.Plugins.Host.HostSelfTest.RunAsync(s => log.Write("plugins", s))).GetAwaiter().GetResult();
            if (failure != null) _log.Write("plugins", "Plug-in host check failed: " + failure);
            Shutdown(failure == null ? 0 : 1);
            return;
        }

        // один экземпляр за раз: два микшера дрались бы за устройства и за журнал привязок
        if (!opt.NoAudio)
        {
            var mutex = new Mutex(true, @"Local\Mixer.SingleInstance", out bool first);
            if (!first)
            {
                // мьютекс держит первый экземпляр: освобождать его при выходе нельзя (ApplicationException)
                mutex.Dispose();
                _log.Write("app", "second instance: showed the first window, exiting");
                // окно первого экземпляра может быть спрятано в трей: попросить его показаться
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
                    using (show)
                        show.Set();
                else WindowsLook.ActivateOther();
                Shutdown();
                return;
            }
            _mutex = mutex;
            WatchShowRequests();
        }

        // тема — до создания окна: XAML берёт ресурсы один раз (StaticResource)
        Theme = ThemeManager.Apply(this, opt.Theme ?? prefs.Theme);
        if (Theme == ThemeManager.Graphite) WindowsLook.ApplyAccent(Resources);
        WireGeometry.Patch = Theme == ThemeManager.Modular; // патч-кабели вместо кривых Безье
        DispatcherUnhandledException += OnUnhandled;

        if (prefsError is not null) _log.Write("app", $"settings not read ({prefsError}), using defaults");
        _log.Write("app", $"start {typeof(App).Assembly.GetName().Version}, Windows {Environment.OSVersion.Version}, " +
                          $"UI {CultureInfo.CurrentUICulture.Name}, graph {path}" + (opt.NoAudio ? ", no audio (--no-audio)" : ""));
        var graph = GraphStore.LoadOrEmpty(path, out string? loadError, out bool upgraded);
        if (upgraded) _log.Write("store", $"graph upgraded to version {GraphConfig.CurrentVersion}, original kept next to it");
        if (loadError is not null) _log.Write("store", loadError);
        _log.Write("store", $"graph: {graph.Nodes.Count} nodes, {graph.Wires.Count} wires" +
                            (graph.BoundApps.Count > 0 ? $", redirect journal: {string.Join(", ", graph.BoundApps)}" : ""));
        _store = new GraphStore(path, graph);
        _store.Saved += err =>
        {
            if (err is not null) _log?.Write("store", "graph not saved: " + err.Message);
        };
        if (!opt.NoAudio)
        {
            _host = new MixerHost(graph, _store.SaveJournal);
            _host.Log += m => _log?.Write("host", m);
            _host.Start(); // управляющий поток сам применит граф; окно не ждёт устройств
        }

        _vm = new MainViewModel(graph, _store, _host, loadError) { Preferences = prefs };
        _vm.RestartRequested += Restart;
        if (upgraded) _vm.OfferGrouping();
        var window = new MainWindow { DataContext = _vm };
        MainWindow = window;
        window.Closing += OnWindowClosing;
        // трей и глобальные клавиши — только у настоящего микшера (--no-audio идёт рядом с ним и не должен отнимать клавиши)
        if (!opt.NoAudio || opt.Tray) StartBackground(prefs);
        if (opt.Minimized && _tray is not null)
        {
            _log.Write("app", "started minimized to the tray");
            MeterClock.Stop(); // окна нет — индикаторы рисовать некому
        }
        else window.Show();

        switch (opt.Open)
        {
            case "palette-sources": _vm.OpenPalette(PaletteMode.Sources, null); break;
            case "palette-outputs": _vm.OpenPalette(PaletteMode.Outputs, null); break;
            case "settings": _vm.OpenSettingsCommand.Execute(null); break;
            // «settings:about» — настройки, прокрученные до «О программе» (снимки)
            case "settings:about":
                _vm.OpenSettingsCommand.Execute(null);
                _vm.Settings!.StartAtAbout = true;
                break;
            case "setup": _vm.OpenSetup(); break;
            // «setup:2» — помощник сразу на шаге 2
            case { } s when s.StartsWith("setup:", StringComparison.Ordinal) && int.TryParse(s[6..], out int step):
                _vm.OpenSetup();
                _vm.Setup!.Step = Math.Clamp(step, 1, ViewModels.SetupViewModel.Steps);
                break;
            // первый запуск с пустой схемой — помощник (кроме снимков и разовых --open)
            case null when opt.Snapshot is null && !upgraded:
                _vm.OfferSetup();
                break;
            // «fx:n3» — боковая панель обработки узла n3, «fx:n3:comp» — сразу у компрессора
            case { } s when s.StartsWith("fx:", StringComparison.Ordinal) && _vm.FindNode(s[3..].Split(':')[0]) is { } node:
                _vm.OpenProcessing(node, s[3..].Split(':').ElementAtOrDefault(1));
                break;
        }
        if (opt.Snapshot is { } png) ScheduleSnapshot(window, png);
    }

    // ── фон: трей, горячие клавиши, уведомления ──

    private const string ShowEventName = @"Local\Mixer.ShowWindow";
    private TrayIcon? _tray;
    private TrayToast? _toast;
    private HotkeyService? _hotkeys;
    private DispatcherTimer? _trayTimer;
    private (TrayState, string)? _trayShown;
    private bool _exiting;

    /// <summary>Тема этого запуска (graphite, daylight, modular).</summary>
    public static string Theme { get; private set; } = ThemeManager.Graphite;

    /// <summary>Второй экземпляр просит показать окно (оно может быть спрятано в трей).</summary>
    private void WatchShowRequests()
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var t = new Thread(() =>
        {
            while (ev.WaitOne()) Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true, Name = "Mixer show requests" };
        t.Start();
    }

    private void StartBackground(Preferences prefs)
    {
        var vm = _vm!;
        try
        {
            _hotkeys = new HotkeyService(vm);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log?.Write("hotkeys", "unavailable: " + ex.Message);
        }
        _toast = new TrayToast();
        vm.HotkeyFired += text =>
        {
            if (vm.Preferences?.HotkeyToasts != false) _toast.Show(text);
        };

        // Значок — только если он действительно показан: без него крестик закрывает окно, а не прячет программу
        // в никуда (_tray остаётся null, и OnWindowClosing ничего не отменяет).
        var tray = new TrayIcon();
        string? trayError = null;
        try
        {
            if (!tray.Show(TrayState.Normal, Strings.App_Title)) trayError = "refused by Windows";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            trayError = $"{ex.GetType().Name}: {ex.Message}";
        }
        if (trayError is not null)
        {
            _log?.Write("app", "tray icon unavailable (" + trayError + "): the close button exits");
            tray.Dispose();
            return;
        }
        _tray = tray;
        _log?.Write("app", "tray icon shown");
        _tray.Click += ShowWindow;
        _tray.RightClick += ShowTrayMenu;
        vm.MicStateChanged += UpdateTray;
        // проблемы (узел в ошибке) видны только опросом
        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trayTimer.Tick += (_, _) => UpdateTray();
        _trayTimer.Start();
        UpdateTray();
        vm.DeviceAvailabilityChanged += (name, lost) =>
        {
            if (vm.Preferences?.DeviceNotifications == false || MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized }) return;
            _tray?.Balloon(Fmt.F(lost ? Strings.Tray_DeviceLostFormat : Strings.Tray_DeviceBackFormat, name), lost ? Strings.Tray_DeviceLostText : "", warning: lost);
        };
    }

    /// <summary>Значок трея: главный микрофон включён или выключен, узел в ошибке; подсказка словами.</summary>
    private void UpdateTray()
    {
        if (_tray is null || _vm is null) return;
        int problems = _vm.Nodes.Count(n => n.Look == NodeLook.Error);
        bool? muted = _vm.MainMicMuted;
        var state = problems > 0 ? TrayState.Problem : muted switch { true => TrayState.MicOff, false => TrayState.MicOn, _ => TrayState.Normal };
        string what = muted switch { true => Strings.Tray_MicOff, false => Strings.Tray_MicOn, _ => Strings.Tray_NoMic };
        string tip = Fmt.F(Strings.Tray_TipFormat, problems > 0 ? Fmt.F(Strings.Tray_ProblemsFormat, what, problems) : what);
        if (_trayShown == (state, tip)) return;
        _trayShown = (state, tip);
        _tray.Show(state, tip);
    }

    /// <summary>Меню трея: сцены, mute главного микрофона, «Открыть», «Выход».</summary>
    private void ShowTrayMenu(Point screen)
    {
        if (_vm is not { } vm) return;
        var menu = new System.Windows.Controls.ContextMenu();
        if (vm.SceneList.Count > 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = Strings.Tray_Scenes, IsEnabled = false });
            foreach (var s in vm.SceneList)
            {
                string id = s.Id;
                var mi = new System.Windows.Controls.MenuItem { Header = s.Name, IsCheckable = true, IsChecked = vm.Graph.ActiveScene == id, InputGestureText = s.Hotkey ?? "" };
                mi.Click += (_, _) => vm.ApplyScene(id);
                menu.Items.Add(mi);
            }
            menu.Items.Add(new System.Windows.Controls.Separator());
        }
        if (vm.MainMic is { } mic)
        {
            var mute = new System.Windows.Controls.MenuItem { Header = Strings.Tray_MuteMic, IsCheckable = true, IsChecked = mic.Muted, ToolTip = mic.Name };
            mute.Click += (_, _) => vm.ToggleMainMic();
            menu.Items.Add(mute);
            menu.Items.Add(new System.Windows.Controls.Separator());
        }
        var open = new System.Windows.Controls.MenuItem { Header = Strings.Tray_Open, FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowWindow();
        menu.Items.Add(open);
        var exit = new System.Windows.Controls.MenuItem { Header = Strings.Tray_Exit };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        // точка курсора — в пикселях экрана; меню ставится в единицах WPF
        var dpi = VisualTreeHelper.GetDpi(MainWindow ?? new Window());
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = screen.X / dpi.DpiScaleX;
        menu.VerticalOffset = screen.Y / dpi.DpiScaleY;
        // без переднего плана меню не закрывается щелчком мимо
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource src) TrayIcon.SetForegroundWindow(src.Handle);
        };
        menu.IsOpen = true;
    }

    private void ShowWindow()
    {
        if (MainWindow is not { } w || _exiting) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    /// <summary>Крестик окна: в трей (по настройке), при первом таком закрытии — подсказка, где искать программу.</summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _tray is null || _vm?.Preferences is not { CloseToTray: true } p) return;
        e.Cancel = true;
        MainWindow?.Hide();
        if (!p.TrayHintShown)
        {
            _tray.Balloon(Strings.Tray_HintTitle, Strings.Tray_HintText);
            p.MarkTrayHintShown();
        }
    }

    /// <summary>«Выход» из меню трея: штатный выход со снятием привязок.</summary>
    private void ExitApp()
    {
        _exiting = true;
        _log?.Write("app", "exit from the tray menu");
        Shutdown();
    }

    /// <summary>Язык до создания окон и потоков: потоки хоста и движка берут культуру по умолчанию.</summary>
    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        // без этого StringFormat в XAML форматирует числа как en-US
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    private static void ScheduleSnapshot(Window window, string png)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            var root = (FrameworkElement)window.Content;
            var dpi = VisualTreeHelper.GetDpi(window);
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(png)) enc.Save(fs);
            if (Current is App app) app._exiting = true; // снимок закрывает программу, а не прячет её в трей
            window.Close();
        };
        t.Start();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // ошибка интерфейса не должна ронять звук и оставлять приложения в заглушке без журнала
        _log?.Error("ui", e.Exception);
        _vm?.Notify(Fmt.F(Strings.App_UiErrorFormat, e.Exception.Message), error: true);
        e.Handled = true;
    }

    /// <summary>Перезапуск (смена языка): штатный выход, новый экземпляр — после того как этот отпустит mutex.</summary>
    private void Restart()
    {
        _log?.Write("app", "restart requested");
        _restart = true;
        _exiting = true;
        Shutdown();
    }

    /// <summary>
    /// Параметры для нового экземпляра: без --lang и --theme (язык и тема теперь из настроек), без разовых --open,
    /// --snapshot и без --minimized (после перезапуска окно должно быть видно).
    /// </summary>
    public static List<string> RestartArgs(IReadOnlyList<string> args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is "--lang" or "--open" or "--snapshot" or "--theme")
            {
                i++;
                continue;
            }
            if (args[i] == "--minimized") continue;
            list.Add(args[i]);
        }
        return list;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayTimer?.Stop();
        _hotkeys?.Dispose();
        _toast?.Close();
        _tray?.Dispose();
        _vm?.Dispose();
        _store?.Flush();
        // сначала тишина, потом снятие привязок у живых приложений (MixerHost.Dispose)
        _host?.Dispose();
        _store?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        _log?.Write("app", "exit");
        if (_restart && Environment.ProcessPath is { } exe)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (string a in RestartArgs(_args)) psi.ArgumentList.Add(a);
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _log?.Write("app", "restart failed: " + ex.Message);
            }
        }
        _log?.Dispose();
        base.OnExit(e);
    }
}
