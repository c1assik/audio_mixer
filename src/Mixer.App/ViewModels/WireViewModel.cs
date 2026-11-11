using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Mixer.App.Ui;
using Mixer.Audio.Engine;
using Mixer.Core.Config;

namespace Mixer.App.ViewModels;

/// <summary>Провод: кривая рисуется <see cref="WireLayer"/>, пилюля с усилением — отдельным элементом в его середине.</summary>
public sealed partial class WireViewModel : ObservableObject
{
    public WireViewModel(WireConfig config, NodeViewModel source, NodeViewModel target)
    {
        Source = source;
        Target = target;
        Config = config;
        Load(config);
    }

    public string From => Config.From;
    public string To => Config.To;
    public NodeViewModel Source { get; }
    public NodeViewModel Target { get; }
    public WireConfig Config { get; private set; }

    [ObservableProperty] private double _gainDb;
    [ObservableProperty] private bool _enabled;
    /// <summary>Причина запрета (петля), если граф пришёл с таким проводом.</summary>
    [ObservableProperty] private string? _forbidden;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDimmed;
    [ObservableProperty] private bool _isHovered;
    /// <summary>Конец провода тянут мышью: на месте его не рисуем.</summary>
    [ObservableProperty] private bool _isDetached;
    [ObservableProperty] private double _pillX;
    [ObservableProperty] private double _pillY;

    // ── подсказка ──
    [ObservableProperty] private string _tipTitle = "";
    [ObservableProperty] private string _tipGain = "";
    [ObservableProperty] private string? _tipFormat;
    [ObservableProperty] private string _tipFailures = "";
    [ObservableProperty] private bool _hasFailures;
    /// <summary>Звук по проводу сейчас не идёт: один из концов ждёт или в ошибке.</summary>
    [ObservableProperty] private bool _isIdle;
    /// <summary>Провод прямого маршрута: приложение играет в выход само, мимо микшера.</summary>
    [ObservableProperty] private bool _isDirect;

    public MeterState Meter { get; } = new();
    internal Wire[] EngineWires = [];
    /// <summary>Провода из шины в движке (по экземпляру на выход).</summary>
    internal BusLink[] EngineLinks = [];

    public string PillText => Forbidden is not null ? Strings.Wire_Loop : !Enabled ? Strings.Wire_Off : IsDirect ? Strings.Wire_Direct : Fmt.Pill(GainDb);
    /// <summary>На пилюле значок «напрямую» вместо дБ.</summary>
    public bool ShowDirect => IsDirect && Enabled && Forbidden is null;
    public bool IsPillOff => !Enabled || Forbidden is not null;
    public string TipHint => Strings.Wire_Hint;

    public Point A => Source.OutPoint;
    public Point B => Target.InPoint;

    internal void Load(WireConfig c)
    {
        Config = c;
        GainDb = c.GainDb;
        Enabled = c.Enabled;
    }

    partial void OnGainDbChanged(double value) => OnPropertyChanged(nameof(PillText));

    partial void OnIsDirectChanged(bool value)
    {
        OnPropertyChanged(nameof(PillText));
        OnPropertyChanged(nameof(ShowDirect));
    }

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(PillText));
        OnPropertyChanged(nameof(IsPillOff));
        OnPropertyChanged(nameof(ShowDirect));
    }

    partial void OnForbiddenChanged(string? value)
    {
        OnPropertyChanged(nameof(PillText));
        OnPropertyChanged(nameof(IsPillOff));
        OnPropertyChanged(nameof(ShowDirect));
    }
}
