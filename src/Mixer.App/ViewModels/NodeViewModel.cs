using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixer.App.Ui;
using Mixer.Audio.Dsp;
using Mixer.Audio.Engine;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;

namespace Mixer.App.ViewModels;

public enum NodeLook { Normal, Waiting, Error }

/// <summary>Подсветка порта, пока тянут провод.</summary>
public enum PortLook { None, Origin, Valid, Occupied, Forbidden }

/// <summary>
/// Карточка узла. Свойства — отражение NodeConfig текущего графа: правка через сеттер уходит в историю
/// (<see cref="MainViewModel"/>), а <see cref="Load"/> обновляет свойства из нового снимка без обратного вызова.
/// </summary>
public sealed partial class NodeViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _loading;

    public NodeViewModel(MainViewModel owner, NodeConfig config)
    {
        _owner = owner;
        Id = config.Id;
        Config = config;
        Load(config);
    }

    public string Id { get; }
    public NodeConfig Config { get; private set; }
    public NodeKind Kind => Config.Kind;
    public bool IsSource => Config.IsSource;
    public bool IsOutput => Config.IsOutput;
    public bool IsBus => Config.IsBus;
    public bool HasInPort => Config.HasInPort;
    public bool HasOutPort => Config.HasOutPort;
    public NodeColumn Column => Config.Column;
    public bool IsApp => Kind == NodeKind.App;
    public bool IsSystem => Kind == NodeKind.System;
    /// <summary>Исключения узла «Весь звук системы» (нормализованные exe).</summary>
    public IReadOnlyList<string> Exclude => Config.Exclude ?? [];
    public bool HasDevice => Kind is NodeKind.DeviceInput or NodeKind.Loopback or NodeKind.DeviceOutput;
    /// <summary>У узла есть «Низкая задержка» (вход и выход устройства).</summary>
    public bool SupportsLowLatency => Config.SupportsLowLatency;
    public bool IsCable => Cable is not null || Config.DefaultRole is null && Config.EndpointName is { } n && FeedbackRules.IsVirtualCable(n);
    /// <summary>Связь с виртуальным кабелем: записанная в узле или найденная по таблице сопоставления.</summary>
    public CableLink? Cable => Mixer.Core.Cables.CableTable.Default.LinkOf(Config);
    /// <summary>Выход в кабель с ролью «виртуальный микрофон»: его можно назначить микрофоном приложению.</summary>
    public bool IsVirtualMic => IsOutput && Cable?.Role == CableRole.VirtualMic;
    /// <summary>Кому назначен кабель микрофоном (micFor).</summary>
    public IReadOnlyList<string> MicFor => Config.MicFor ?? [];
    /// <summary>Узел «Устройство по умолчанию»: устройство меняется вслед за Windows.</summary>
    public bool FollowsDefault => Config.FollowsDefault;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private Color _color;
    [ObservableProperty] private double _gainDb;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _mono;
    [ObservableProperty] private double _balance;
    [ObservableProperty] private bool _takeOver;
    /// <summary>Прямой маршрут разрешён (настройка узла «Приложение»).</summary>
    [ObservableProperty] private bool _directRoute;
    /// <summary>«Низкая задержка» (IAudioClient3) с учётом умолчаний: у выхода включена, у входа выключена.</summary>
    [ObservableProperty] private bool _lowLatency;
    /// <summary>«Весь звук системы»: не исключать приложения со своим узлом.</summary>
    [ObservableProperty] private bool _includeOwnNodes;

    // ── состояние (опрос 4 раза в секунду) ──
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _subtitleWarn;
    [ObservableProperty] private NodeLook _look;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _warnings;
    [ObservableProperty] private string? _limiterText;
    [ObservableProperty] private string? _tooltip;
    [ObservableProperty] private ImageSource? _appIcon;
    /// <summary>Узел сейчас на прямом маршруте: звук идёт мимо микшера.</summary>
    [ObservableProperty] private bool _isDirect;
    /// <summary>«Весь звук системы»: значок «пропущено: N» (null — пропущенных нет) и список с причинами.</summary>
    [ObservableProperty] private string? _skippedText;
    [ObservableProperty] private string? _skippedTip;
    /// <summary>Кабель-микрофон: «Микрофон для: OBS Studio» (null — никому не назначен) и состояние назначений.</summary>
    [ObservableProperty] private string? _micForText;
    [ObservableProperty] private string? _micForTip;
    /// <summary>Push-to-talk / push-to-mute: узел сейчас молчит (true) или звучит (false) по клавише; null — клавиши нет.</summary>
    [ObservableProperty] private bool? _pushMuted;

    // ── взаимодействие ──
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDimmed;
    [ObservableProperty] private bool _isRenaming;
    /// <summary>Подсветка входного порта (слева: у шины и выхода), пока тянут провод.</summary>
    [ObservableProperty] private PortLook _inPort;
    /// <summary>Подсветка выходного порта (справа: у источника и шины).</summary>
    [ObservableProperty] private PortLook _outPort;
    [ObservableProperty] private string? _portReason;
    /// <summary>Шина ни во что не ведёт или в неё ничего не входит: звука нет.</summary>
    [ObservableProperty] private bool _isSilent;

    // ── обработка ──
    [ObservableProperty] private bool _hasGate;
    [ObservableProperty] private bool _hasEq;
    [ObservableProperty] private bool _hasComp;
    /// <summary>Сколько слотов плагинов включено: чип «FX 2».</summary>
    [ObservableProperty] private int _pluginsOn;
    [ObservableProperty] private bool _hasPlugins;
    /// <summary>Один из слотов в сбое: оранжевая точка на чипе.</summary>
    [ObservableProperty] private bool _pluginFault;
    /// <summary>Подавление компрессора сейчас: «−4 дБ» рядом с чипами (null — не жмёт).</summary>
    [ObservableProperty] private string? _reductionText;

    public MeterState Meter { get; } = new();
    /// <summary>Индикаторы цепочки (до и после обработки, подавление компрессора, гейт) — для боковой панели.</summary>
    public ChainMeterState ChainMeter { get; } = new();

    /// <summary>Подпись под тип узла: шина показывает чипы обработки и «+ FX» так же, как источник.</summary>
    public bool ShowFx => !HasGate && !HasEq && !HasComp && !HasPlugins && !HasMicFor;
    public string FxText => "FX " + PluginsOn;
    public bool HasMicFor => MicForText is not null;
    public bool HasReduction => ReductionText is not null;
    /// <summary>Слоты плагинов по порядку (модули 4HP темы Modular).</summary>
    public IReadOnlyList<PluginConfig> PluginSlots => Config.Plugins ?? [];

    /// <summary>Узлы движка этого узла (у приложения — по захвату на корень). Обновляется опросом, читается в кадре.</summary>
    internal AudioNode[] EngineNodes = [];
    internal OutputNode? EngineOutput;
    /// <summary>Экземпляры шины в движке (у шины и у приложения с обработкой): индикатор берётся с них.</summary>
    internal BusInstance[] EngineBuses = [];
    /// <summary>Цепочки обработки узла в движке (по экземпляру на захват или на выход шины).</summary>
    internal ProcessingChain[] EngineChains = [];
    internal double NextIconTry;
    /// <summary>Прямой маршрут: индикатор сессии приложения от хоста и его последние пики (их же показывает провод).</summary>
    internal Mixer.Audio.Dsp.PeakMeter? DirectMeter;
    internal (float L, float R) DirectPeak;

    public Brush ColorBrush { get; private set; } = Brushes.Gray;
    public Brush TintBrush { get; private set; } = Brushes.Transparent;
    public Brush FaderBrush => Muted ? ThemeColors.Brush("WireOff", Color.FromRgb(0x5F, 0x65, 0x6D)) : IsSource ? ColorBrush : ThemeColors.Brush("WireNeutral", Color.FromRgb(0xC4, 0xC8, 0xCD));
    /// <summary>Цвет провода из узла: у источника — его цвет, у шины — нейтральный.</summary>
    public Color WireColor => IsSource ? Color : ThemeColors.Get("WireNeutral", Color.FromRgb(0xC4, 0xC8, 0xCD));
    public Geometry Glyph => (Geometry)Application.Current.Resources[GlyphKey];
    public bool HasAppIcon => AppIcon is not null;

    public string GlyphKey => Kind switch
    {
        NodeKind.DeviceInput => IsCable ? "IconPlug" : "IconMic",
        NodeKind.Loopback => "IconSpeaker",
        NodeKind.App => "IconApp",
        NodeKind.System => "IconSystem",
        NodeKind.Bus => "IconBus",
        _ => IsCable ? "IconBroadcast" : "IconHeadphones",
    };

    public string GainText => Fmt.Gain(GainDb);
    public bool ShowBody => Look == NodeLook.Normal;
    public bool HasWarnings => !string.IsNullOrEmpty(Warnings);
    public bool HasLimiter => LimiterText is not null;
    public bool HasSkipped => SkippedText is not null;

    /// <summary>Центр выходного порта на холсте (справа у источника и шины).</summary>
    public Point OutPoint => new(X + GraphLayout.CardWidth, Y + GraphLayout.PortY);
    /// <summary>Центр входного порта (слева у шины и выхода).</summary>
    public Point InPoint => new(X, Y + GraphLayout.PortY);
    /// <summary>Порт, к которому тянут провод: входной, если звук идёт в узел.</summary>
    public Point PortFor(bool input) => input ? InPoint : OutPoint;

    public Rect Bounds => new(X, Y, GraphLayout.CardWidth, GraphLayout.CardHeight);

    internal void Load(NodeConfig c)
    {
        _loading = true;
        try
        {
            Config = c;
            Name = c.Name;
            X = c.X;
            Y = c.Y;
            Color = ParseColor(c.Color, IsSource);
            GainDb = c.GainDb;
            Muted = c.Muted;
            Mono = c.Mono ?? false;
            Balance = c.Balance ?? 0;
            TakeOver = GraphBuilder.TakesOver(c);
            DirectRoute = c.DirectRoute ?? true;
            LowLatency = c.LowLatencyOn;
            IncludeOwnNodes = c.IncludeOwnNodes ?? false;
            HasGate = c.Chain?.Gate.Enabled == true;
            HasEq = c.Chain?.Eq.Enabled == true;
            HasComp = c.Chain?.Comp.Enabled == true;
            HasPlugins = c.HasPlugins;
            PluginsOn = c.Plugins?.Count(p => p.Enabled) ?? 0;
            OnPropertyChanged(nameof(PluginSlots));
            MicForText = c.MicFor is { Count: > 0 } apps && IsOutput
                ? Fmt.F(Strings.Node_MicForFormat, string.Join(", ", apps.Select(Mixer.Core.Cables.AppNames.Friendly)))
                : null;
        }
        finally
        {
            _loading = false;
        }
    }

    public static Color ParseColor(string? hex, bool source)
    {
        if (hex is { Length: > 0 })
        {
            try { return ThemeColors.Source((Color)ColorConverter.ConvertFromString(hex)); }
            catch (FormatException) { }
        }
        return source ? ThemeColors.Get("Source0", Color.FromRgb(0x4F, 0xA3, 0xFF)) : ThemeColors.Get("WireNeutral", Color.FromRgb(0xC4, 0xC8, 0xCD));
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    partial void OnColorChanged(Color value)
    {
        ColorBrush = Freeze(new SolidColorBrush(value));
        TintBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x29, value.R, value.G, value.B)));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(TintBrush));
        OnPropertyChanged(nameof(FaderBrush));
        _owner.InvalidateWires();
    }

    partial void OnXChanged(double value) => _owner.NodeMoved(this);
    partial void OnYChanged(double value) => _owner.NodeMoved(this);

    partial void OnGainDbChanged(double value)
    {
        OnPropertyChanged(nameof(GainText));
        if (!_loading) _owner.SetNodeGain(this, value);
    }

    partial void OnMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(FaderBrush));
        _owner.InvalidateWires();
        if (!_loading) _owner.Edit(g => GraphEdits.UpdateNode(g, Id, n => n with { Muted = value }));
    }

    partial void OnMonoChanged(bool value)
    {
        if (!_loading) _owner.Edit(g => GraphEdits.UpdateNode(g, Id, n => n with { Mono = value }));
    }

    partial void OnBalanceChanged(double value)
    {
        if (!_loading) _owner.Edit(g => GraphEdits.UpdateNode(g, Id, n => n with { Balance = (float)Math.Round(value, 2) }), "balance:" + Id);
    }

    partial void OnTakeOverChanged(bool value)
    {
        if (!_loading) _owner.Edit(g => GraphEdits.UpdateNode(g, Id, n => n with { TakeOver = value }));
    }

    partial void OnDirectRouteChanged(bool value)
    {
        if (!_loading) _owner.Edit(g => GraphEdits.UpdateNode(g, Id, n => n with { DirectRoute = value ? null : false }));
    }

    partial void OnLowLatencyChanged(bool value)
    {
        // значение по умолчанию (у выхода — вкл., у входа и кабеля — выкл.) в файл не пишется
        if (!_loading) _owner.Edit(g => GraphEdits.UpdateNode(g, Id, n => n with { LowLatency = value == (n with { LowLatency = null }).LowLatencyOn ? null : value }));
    }

    partial void OnIncludeOwnNodesChanged(bool value)
    {
        if (!_loading) _owner.Edit(g => GraphEdits.UpdateNode(g, Id, n => n with { IncludeOwnNodes = value ? true : null }));
    }

    partial void OnSkippedTextChanged(string? value) => OnPropertyChanged(nameof(HasSkipped));
    partial void OnMicForTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasMicFor));
        OnPropertyChanged(nameof(ShowFx));
    }

    partial void OnLookChanged(NodeLook value) => OnPropertyChanged(nameof(ShowBody));
    partial void OnHasGateChanged(bool value) => OnPropertyChanged(nameof(ShowFx));
    partial void OnHasEqChanged(bool value) => OnPropertyChanged(nameof(ShowFx));
    partial void OnHasCompChanged(bool value) => OnPropertyChanged(nameof(ShowFx));
    partial void OnHasPluginsChanged(bool value) => OnPropertyChanged(nameof(ShowFx));
    partial void OnPluginsOnChanged(int value) => OnPropertyChanged(nameof(FxText));
    partial void OnReductionTextChanged(string? value) => OnPropertyChanged(nameof(HasReduction));
    partial void OnWarningsChanged(string? value) => OnPropertyChanged(nameof(HasWarnings));
    partial void OnLimiterTextChanged(string? value) => OnPropertyChanged(nameof(HasLimiter));
    partial void OnAppIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasAppIcon));

    /// <summary>Перетаскивание ручки закончилось: следующая правка — новый шаг истории.</summary>
    public void EndEdit() => _owner.EndMerge();

    [RelayCommand]
    private void ResetGain()
    {
        GainDb = 0;
        _owner.EndMerge();
    }

    [RelayCommand]
    private void BeginRename()
    {
        _owner.Select(this);
        IsRenaming = true;
    }

    public void CommitRename(string? text)
    {
        IsRenaming = false;
        string name = (text ?? "").Trim();
        if (name.Length == 0 || name == Name) return;
        _owner.Edit(g => GraphEdits.UpdateNode(g, Id, n => n with { Name = name }));
    }

    [RelayCommand]
    private void Delete() => _owner.DeleteNode(this);

    [RelayCommand]
    private void ChooseDevice() => _owner.RequestDeviceMenu(this);

    /// <summary>Открыть боковую панель обработки (по чипу или «+ FX»); block — к какому блоку прокрутить.</summary>
    [RelayCommand]
    private void OpenChain(string? block) => _owner.OpenProcessing(this, block);

    /// <summary>EDIT на модуле плагина: окно плагина (у плагина без окна — список параметров в панели).</summary>
    [RelayCommand]
    private void OpenPlugin(string slotId) => _owner.OpenPluginEditor(this, slotId);
}
