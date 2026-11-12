using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixer.App.Ui;
using Mixer.Audio.Dsp;
using Mixer.Core.Config;
using Mixer.Core.Plugins;
using Mixer.Plugins.Host;

namespace Mixer.App.ViewModels;

/// <summary>Параметр плагина в списке (для плагинов без окна): ползунок 0…1, текст значения — от плагина.</summary>
public sealed partial class PluginParamViewModel : ObservableObject
{
    private readonly Action<uint, double> _set;
    private bool _loading;

    public PluginParamViewModel(PluginParam p, Action<uint, double> set)
    {
        _set = set;
        Id = p.Id;
        Label = p.Title;
        Steps = p.Steps;
        Load(p.Value, p.Text, p.Units);
    }

    public uint Id { get; }
    public string Label { get; }
    public int Steps { get; }
    private string _units = "";

    [ObservableProperty] private double _position;
    [ObservableProperty] private string _text = "";

    partial void OnPositionChanged(double value)
    {
        if (_loading) return;
        double v = Steps > 0 ? Math.Round(value * Steps) / Steps : value;
        _set(Id, v);
    }

    internal void Load(double value, string text, string? units = null)
    {
        if (units is not null) _units = units;
        _loading = true;
        try
        {
            Position = value;
            Text = _units.Length > 0 && text.Length > 0 ? text + " " + _units : text;
        }
        finally
        {
            _loading = false;
        }
    }
}

/// <summary>
/// Строка слота в панели обработки (VST, «Интерфейс»): имя и производитель, индикатор выхода, задержка, нагрузка,
/// переключатель, «Открыть» (окно плагина или список параметров), меню и сбой с «Перезапустить».
/// </summary>
public sealed partial class PluginSlotViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly NodeViewModel _node;
    private bool _loading;
    private PluginHost? _hooked;

    public PluginSlotViewModel(MainViewModel main, NodeViewModel node, PluginConfig c)
    {
        _main = main;
        _node = node;
        Config = c;
        Load(c);
    }

    public PluginConfig Config { get; private set; }
    public string Id => Config.Id;
    public MeterState Meter { get; } = new();
    internal PluginSlot? Engine;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _vendor = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string? _latencyText;
    [ObservableProperty] private string? _loadText;
    /// <summary>Что со слотом не так, словами (null — работает).</summary>
    [ObservableProperty] private string? _status;
    /// <summary>Сбой или плагина нет: красная рамка.</summary>
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _canRestart;
    [ObservableProperty] private bool _paramsOpen;

    public ObservableCollection<PluginParamViewModel> Params { get; } = new();
    public bool HasStatus => Status is not null;

    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnEnabledChanged(bool value)
    {
        if (!_loading) _main.SetPluginEnabled(_node, Id, value);
    }

    internal void Load(PluginConfig c)
    {
        Config = c;
        _loading = true;
        try
        {
            Name = c.Name.Length > 0 ? c.Name : c.ClassId ?? "?";
            Vendor = c.Vendor;
            Enabled = c.Enabled;
        }
        finally
        {
            _loading = false;
        }
    }

    private PluginHost? Host => _main.Host?.Plugins.HostOf(_node.Id, Id);

    internal void Poll(PluginSlotStatus? s)
    {
        Engine = _main.Host?.Plugins.Slots(_node.Id).FirstOrDefault(x => x.Id == Id);
        LatencyText = s is { LatencySamples: > 0, Rate: > 0 }
            ? "+" + Fmt.F(Strings.Unit_msFormat, (s.LatencySamples * 1000.0 / s.Rate).ToString("0.#", CultureInfo.CurrentCulture))
            : null;
        LoadText = s is { State: PluginSlotState.Running or PluginSlotState.Off } ? Fmt.Percent(s.Load * 100) + "%" : null;
        IsFailed = s?.State is PluginSlotState.Failed or PluginSlotState.Missing;
        CanRestart = s is { State: PluginSlotState.Failed, Fault: not PluginFault.Limit };
        Status = s is null ? (_main.Host is null ? null : Strings.Plugin_StateLoading) : s.State switch
        {
            PluginSlotState.Loading => Strings.Plugin_StateLoading,
            PluginSlotState.Missing => Strings.Plugin_StateMissing,
            PluginSlotState.Failed => s.Fault switch
            {
                PluginFault.Crashed or PluginFault.Hung => Strings.Plugin_FaultCrashed,
                PluginFault.CrashedRepeatedly => Strings.Plugin_FaultCrashedRepeatedly,
                PluginFault.TooSlow => Strings.Plugin_FaultTooSlow,
                PluginFault.BadOutput => Strings.Plugin_FaultBadOutput,
                PluginFault.Limit => Strings.Plugin_FaultLimit,
                _ => Fmt.F(Strings.Plugin_FaultLoadFailedFormat, s.Detail ?? "?"),
            },
            _ when s.Loud => Strings.Plugin_Loud,
            _ => null,
        };
    }

    internal void Frame(double now, double dt)
    {
        if (Engine is null)
        {
            Meter.Reset();
            return;
        }
        var (l, r) = Engine.Meter.Take();
        Meter.Push(l, r, now, dt);
    }

    // ── окно редактора и список параметров ──

    [RelayCommand]
    private void Open()
    {
        if (Host is not { Info: { } info } host)
        {
            _main.Notify(Strings.Plugin_NotRunning, error: true);
            return;
        }
        Hook(host);
        if (!info.HasEditor)
        {
            ParamsOpen = !ParamsOpen;
            if (ParamsOpen) _ = LoadParamsAsync(host);
            return;
        }
        var owner = Application.Current?.MainWindow is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        var b = Config.Editor;
        host.OpenEditor(owner, Fmt.F(Strings.Plugin_EditorTitleFormat, Name, _node.Name), b is null ? null : new EditorBounds(b.X, b.Y, b.W, b.H));
    }

    /// <summary>События хоста (из пула потоков) — в поток интерфейса; хост меняется после перезапуска.</summary>
    private void Hook(PluginHost host)
    {
        if (_hooked == host) return;
        _hooked = host;
        var ui = Application.Current.Dispatcher;
        host.EditorClosed += b => ui.BeginInvoke(() => _main.EditorClosed(_node, Id, new PluginEditorBounds(b.X, b.Y, b.Width, b.Height)));
        host.EditorFailed += m => ui.BeginInvoke(() => _main.Notify(Fmt.F(Strings.Plugin_EditorFailedFormat, m), error: true));
        host.ParamChanged += v => ui.BeginInvoke(() =>
        {
            if (Params.FirstOrDefault(p => p.Id == v.Id) is { } p) p.Load(v.Value, v.Text);
        });
        host.ParamsChanged += list => ui.BeginInvoke(() => ShowParams(host, list));
    }

    private async Task LoadParamsAsync(PluginHost host)
    {
        try
        {
            ShowParams(host, await host.GetParamsAsync());
        }
        catch (Exception ex) when (ex is PluginHostException or TimeoutException or System.IO.IOException)
        {
            _main.Notify(Fmt.F(Strings.Plugin_ParamsFailedFormat, ex.Message), error: true);
        }
    }

    /// <summary>Скрытые параметры, только для чтения и обход не показываются (обход — переключатель слота).</summary>
    private void ShowParams(PluginHost host, IReadOnlyList<PluginParam> list)
    {
        Params.Clear();
        foreach (var p in list.Where(p => !p.Hidden && !p.ReadOnly && !p.Bypass))
            Params.Add(new PluginParamViewModel(p, (id, v) => host.SetParam(id, v)));
    }

    [RelayCommand]
    private void Restart() => _main.RestartPlugin(_node, Id);
}
