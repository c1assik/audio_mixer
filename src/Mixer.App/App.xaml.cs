using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
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

        // один экземпляр за раз: два микшера дрались бы за устройства и за журнал привязок
        if (!opt.NoAudio)
        {
            _mutex = new Mutex(true, @"Local\Mixer.SingleInstance", out bool first);
            if (!first)
            {
                _log.Write("app", "second instance: brought the first window to the front, exiting");
                WindowsLook.ActivateOther();
                Shutdown();
                return;
            }
        }

        WindowsLook.ApplyAccent(Resources);
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
        window.Show();

        switch (opt.Open)
        {
            case "palette-sources": _vm.OpenPalette(PaletteMode.Sources, null); break;
            case "palette-outputs": _vm.OpenPalette(PaletteMode.Outputs, null); break;
            case "settings": _vm.OpenSettingsCommand.Execute(null); break;
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
        Shutdown();
    }

    /// <summary>Параметры для нового экземпляра: без --lang (язык теперь из настроек) и без разовых --open, --snapshot.</summary>
    public static List<string> RestartArgs(IReadOnlyList<string> args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is "--lang" or "--open" or "--snapshot")
            {
                i++;
                continue;
            }
            list.Add(args[i]);
        }
        return list;
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
