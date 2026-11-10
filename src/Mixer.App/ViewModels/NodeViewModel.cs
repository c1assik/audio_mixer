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
    public bool HasDevice => Kind is NodeKind.DeviceInput or NodeKind.Loopback or NodeKind.DeviceOutput;
    public bool IsCable => Config.DefaultRole is null && Config.EndpointName is { } n && FeedbackRules.IsVirtualCable(n);
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

    // ── состояние (опрос 4 раза в секунду) ──
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _subtitleWarn;
    [ObservableProperty] private NodeLook _look;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _warnings;
    [ObservableProperty] private string? _limiterText;
    [ObservableProperty] private string? _tooltip;
    [ObservableProperty] private ImageSource? _appIcon;

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
    /// <summary>Подавление компрессора сейчас: «−4 дБ» рядом с чипами (null — не жмёт).</summary>
    [ObservableProperty] private string? _reductionText;

    public MeterState Meter { get; } = new();
    /// <summary>Индикаторы цепочки (до и после обработки, подавление компрессора, гейт) — для боковой панели.</summary>
    public ChainMeterState ChainMeter { get; } = new();

    /// <summary>Подпись под тип узла: шина показывает чипы обработки и «+ FX» так же, как источник.</summary>
    public bool ShowFx => !HasGate && !HasEq && !HasComp;
    public bool HasReduction => ReductionText is not null;

    /// <summary>Узлы движка этого узла (у приложения — по захвату на корень). Обновляется опросом, читается в кадре.</summary>
    internal AudioNode[] EngineNodes = [];
    internal OutputNode? EngineOutput;
    /// <summary>Экземпляры шины в движке (у шины и у приложения с обработкой): индикатор берётся с них.</summary>
    internal BusInstance[] EngineBuses = [];
    /// <summary>Цепочки обработки узла в движке (по экземпляру на захват или на выход шины).</summary>
    internal ProcessingChain[] EngineChains = [];
    internal double NextIconTry;

    public Brush ColorBrush { get; private set; } = Brushes.Gray;
    public Brush TintBrush { get; private set; } = Brushes.Transparent;
    public Brush FaderBrush => Muted ? Freeze(new SolidColorBrush(Color.FromRgb(0x5F, 0x65, 0x6D))) : IsSource ? ColorBrush : Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0xC8, 0xCD)));
    /// <summary>Цвет провода из узла: у источника — его цвет, у шины — нейтральный.</summary>
    public Color WireColor => IsSource ? Color : Color.FromRgb(0xC4, 0xC8, 0xCD);
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
            HasGate = c.Chain?.Gate.Enabled == true;
            HasEq = c.Chain?.Eq.Enabled == true;
            HasComp = c.Chain?.Comp.Enabled == true;
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
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch (FormatException) { }
        }
        return source ? Color.FromRgb(0x4F, 0xA3, 0xFF) : Color.FromRgb(0xC4, 0xC8, 0xCD);
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

    partial void OnLookChanged(NodeLook value) => OnPropertyChanged(nameof(ShowBody));
    partial void OnHasGateChanged(bool value) => OnPropertyChanged(nameof(ShowFx));
    partial void OnHasEqChanged(bool value) => OnPropertyChanged(nameof(ShowFx));
    partial void OnHasCompChanged(bool value) => OnPropertyChanged(nameof(ShowFx));
    partial void OnReductionTextChanged(string? value) => OnPropertyChanged(nameof(HasReduction));
    partial void OnWarningsChanged(string? value) => OnPropertyChanged(nameof(HasWarnings));
    partial void OnLimiterTextChanged(string? value) => OnPropertyChanged(nameof(HasLimiter));
    partial void OnAppIconChanged(ImageSource? value) => OnPropertyChanged(nameof(HasAppIcon));

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
}
