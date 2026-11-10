using CommunityToolkit.Mvvm.ComponentModel;

namespace Mixer.App.ViewModels;

/// <summary>
/// Один параметр обработки: подпись, значение, положение ползунка 0…1 (по логарифму для частот и времён),
/// текст. Правка через <see cref="Value"/> или <see cref="Position"/> уходит в граф; <see cref="Load"/> обновляет
/// из графа без обратного вызова.
/// </summary>
public sealed partial class ParamViewModel : ObservableObject
{
    private readonly Action<double> _commit;
    private readonly Func<double, string> _format;
    private bool _loading;

    public ParamViewModel(string key, string label, double min, double max, double @default, Func<double, string> format, Action<double> commit,
        bool log = false, double step = 0.1, double wheelStep = 1)
    {
        Key = key;
        Label = label;
        Min = min;
        Max = max;
        Default = @default;
        Log = log && min > 0;
        Step = step;
        WheelStep = wheelStep;
        _format = format;
        _commit = commit;
        _value = @default;
    }

    public string Key { get; }
    public string Label { get; }
    public double Min { get; }
    public double Max { get; }
    public double Default { get; }
    public bool Log { get; }
    /// <summary>Точность значения: 0,1 дБ, 1 Гц и т. п.</summary>
    public double Step { get; }
    /// <summary>Шаг колеса (у логарифмических — доля октавы положения).</summary>
    public double WheelStep { get; }

    [ObservableProperty] private double _value;

    public string Text => _format(Value);

    /// <summary>Положение ползунка 0…1.</summary>
    public double Position
    {
        get => ToPos(Value);
        set => Value = Snap(FromPos(Math.Clamp(value, 0, 1)));
    }

    private double ToPos(double v) => Log
        ? Math.Log(Math.Clamp(v, Min, Max) / Min) / Math.Log(Max / Min)
        : (Math.Clamp(v, Min, Max) - Min) / (Max - Min);

    private double FromPos(double p) => Log ? Min * Math.Pow(Max / Min, p) : Min + p * (Max - Min);

    private double Snap(double v)
    {
        // у логарифмических шаг относительный: 1 Гц внизу, 10 Гц на килогерцах
        double step = Log ? Math.Max(Step, Math.Pow(10, Math.Floor(Math.Log10(Math.Max(v, 1e-9))) - 2) * 1) : Step;
        return Math.Clamp(Math.Round(v / step) * step, Min, Max);
    }

    /// <summary>Колесо или стрелки: delta шагов (у логарифмических — по 1/24 диапазона).</summary>
    public void Nudge(double delta)
    {
        if (Log) Position += delta * WheelStep / 24;
        else Value = Snap(Value + delta * WheelStep);
    }

    public void Reset() => Value = Default;

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(Text));
        if (!_loading) _commit(value);
    }

    internal void Load(double v)
    {
        if (Value == v) return;
        _loading = true;
        try { Value = v; }
        finally { _loading = false; }
    }
}
