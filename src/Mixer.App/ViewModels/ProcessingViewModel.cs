using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixer.App.Ui;
using Mixer.Audio.Dsp;
using Mixer.Core.Config;
using Mixer.Core.Editing;

namespace Mixer.App.ViewModels;

/// <summary>Полоса EQ в панели: тип, частота, усиление, добротность.</summary>
public sealed partial class EqBandViewModel : ObservableObject
{
    private readonly ProcessingViewModel _owner;

    public EqBandViewModel(ProcessingViewModel owner, int index)
    {
        _owner = owner;
        Index = index;
        Freq = new ParamViewModel($"eq{index}f", Strings.Fx_Freq, EqSettings.MinFreq, EqSettings.MaxFreq, EqSettings.DefaultBands[index].FreqHz,
            Fmt.Hz, v => owner.EditBand(index, b => b with { FreqHz = (float)v }), log: true, step: 1);
        Gain = new ParamViewModel($"eq{index}g", Strings.Fx_Gain, -EqSettings.MaxGainDb, EqSettings.MaxGainDb, 0,
            Fmt.SignedDb, v => owner.EditBand(index, b => b with { GainDb = (float)v }), step: 0.1, wheelStep: 0.5);
        Q = new ParamViewModel($"eq{index}q", Strings.Fx_Q, EqSettings.MinQ, EqSettings.MaxQ, 1,
            Fmt.Q, v => owner.EditBand(index, b => b with { Q = (float)v }), log: true, step: 0.01);
    }

    public int Index { get; }
    public string Label => (Index + 1).ToString();
    public ParamViewModel Freq { get; }
    public ParamViewModel Gain { get; }
    public ParamViewModel Q { get; }

    [ObservableProperty] private EqBandType _type;

    public bool IsOn => Type != EqBandType.Off;
    public bool HasGain => IsOn;
    /// <summary>У пика добротность — ширина полосы; у полок тоже есть (крутизна), у «выкл» — прочерк.</summary>
    public string TypeText => Fmt.BandType(Type);

    partial void OnTypeChanged(EqBandType value)
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(HasGain));
        OnPropertyChanged(nameof(TypeText));
    }

    public void SetType(EqBandType type) => _owner.EditBand(Index, b => b with { Type = type }, mergeKey: null);

    internal void Load(EqBand b)
    {
        Type = b.Type;
        Freq.Load(b.FreqHz);
        Gain.Load(b.GainDb);
        Q.Load(b.Q);
    }
}

/// <summary>
/// Боковая панель обработки узла: гейт, EQ с кривой, компрессор, пресеты, индикаторы до и после.
/// Каждое движение ручки — правка графа через историю (склеивается по ключу, пока тянут), так что движок получает
/// новую цепочку тем же путём, что и любую правку, а Ctrl+Z работает и здесь.
/// </summary>
public sealed partial class ProcessingViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _loading;

    public ProcessingViewModel(MainViewModel owner, NodeViewModel node)
    {
        _owner = owner;
        Node = node;
        var d = new ChainSettings().Normalized();

        GateParams =
        [
            new("gt", Strings.Fx_Threshold, -80, 0, d.Gate.ThresholdDb, Fmt.Dbfs, v => EditGate(g => g with { ThresholdDb = (float)v }, "gt"), wheelStep: 1),
            new("ga", Strings.Fx_Attack, 0.1, 50, d.Gate.AttackMs, Fmt.Millis, v => EditGate(g => g with { AttackMs = (float)v }, "ga"), log: true),
            new("gh", Strings.Fx_Hold, 0, 500, d.Gate.HoldMs, Fmt.Millis, v => EditGate(g => g with { HoldMs = (float)v }, "gh"), step: 1, wheelStep: 10),
            new("gr", Strings.Fx_Release, 5, 1000, d.Gate.ReleaseMs, Fmt.Millis, v => EditGate(g => g with { ReleaseMs = (float)v }, "gr"), log: true, step: 1),
            new("gd", Strings.Fx_Depth, -80, 0, d.Gate.DepthDb, Fmt.SignedDb, v => EditGate(g => g with { DepthDb = (float)v }, "gd"), step: 1, wheelStep: 1),
        ];
        HighPass = new ParamViewModel("hp", Strings.Fx_HighPass, EqSettings.MinHighPass, EqSettings.MaxHighPass, d.Eq.HighPassHz, Fmt.Hz,
            v => Edit(c => c with { Eq = c.Eq with { HighPassHz = (float)v } }, "hp"), log: true, step: 1);
        Bands = new ObservableCollection<EqBandViewModel>(Enumerable.Range(0, EqSettings.BandCount).Select(i => new EqBandViewModel(this, i)));
        CompParams =
        [
            new("ct", Strings.Fx_Threshold, -60, 0, d.Comp.ThresholdDb, Fmt.Dbfs, v => EditComp(c => c with { ThresholdDb = (float)v }, "ct"), wheelStep: 1),
            new("cr", Strings.Fx_Ratio, 1, 20, d.Comp.Ratio, Fmt.Ratio, v => EditComp(c => c with { Ratio = (float)v }, "cr"), log: true, step: 0.1),
            new("ca", Strings.Fx_Attack, 0.1, 100, d.Comp.AttackMs, Fmt.Millis, v => EditComp(c => c with { AttackMs = (float)v }, "ca"), log: true),
            new("cl", Strings.Fx_Release, 10, 1000, d.Comp.ReleaseMs, Fmt.Millis, v => EditComp(c => c with { ReleaseMs = (float)v }, "cl"), log: true, step: 1),
            new("ck", Strings.Fx_Knee, 0, 12, d.Comp.KneeDb, Fmt.PlainDb, v => EditComp(c => c with { KneeDb = (float)v }, "ck"), step: 0.5, wheelStep: 0.5),
            new("cm", Strings.Fx_Makeup, 0, 24, d.Comp.MakeupDb, Fmt.SignedDb, v => EditComp(c => c with { MakeupDb = (float)v }, "cm"), step: 0.1, wheelStep: 0.5),
        ];
        Load();
    }

    public NodeViewModel Node { get; }
    internal MainViewModel Owner => _owner;
    public IReadOnlyList<ParamViewModel> GateParams { get; }
    public ParamViewModel HighPass { get; }
    public ObservableCollection<EqBandViewModel> Bands { get; }
    public IReadOnlyList<ParamViewModel> CompParams { get; }

    /// <summary>Слоты «Плагины до» и «Плагины после» по порядку.</summary>
    public ObservableCollection<PluginSlotViewModel> PreSlots { get; } = new();
    public ObservableCollection<PluginSlotViewModel> PostSlots { get; } = new();
    [ObservableProperty] private bool _canAddPre;
    [ObservableProperty] private bool _canAddPost;

    [RelayCommand]
    private void AddPlugin(string group) =>
        _owner.OpenPluginPicker(new PluginTarget(Node, group == "post" ? PluginGroup.Post : PluginGroup.Pre));

    private void LoadSlots()
    {
        var all = Node.Config.Plugins ?? [];
        void Fill(ObservableCollection<PluginSlotViewModel> list, PluginGroup group)
        {
            var want = all.Where(p => p.Group == group).Select(p => _owner.SlotVm(Node, p)).ToList();
            if (want.SequenceEqual(list)) return;
            list.Clear();
            foreach (var x in want) list.Add(x);
        }
        Fill(PreSlots, PluginGroup.Pre);
        Fill(PostSlots, PluginGroup.Post);
        CanAddPre = PluginEdits.HasRoom(_owner.Graph, Node.Config, PluginGroup.Pre);
        CanAddPost = PluginEdits.HasRoom(_owner.Graph, Node.Config, PluginGroup.Post);
    }

    internal void Frame(double now, double dt)
    {
        foreach (var s in PreSlots) s.Frame(now, dt);
        foreach (var s in PostSlots) s.Frame(now, dt);
    }

    /// <summary>Цепочка узла сейчас (по умолчанию — всё выключено).</summary>
    public ChainSettings Chain => (Node.Config.Chain ?? new ChainSettings()).Normalized();

    [ObservableProperty] private bool _gateOn;
    [ObservableProperty] private bool _eqOn;
    [ObservableProperty] private bool _highPassOn;
    [ObservableProperty] private bool _compOn;
    [ObservableProperty] private string _presetName = "";
    /// <summary>К какому блоку прокрутить панель (gate, eq, comp); вид сбрасывает после прокрутки.</summary>
    [ObservableProperty] private string? _focusBlock;

    // ── живые подписи (опрос 4 раза в секунду) ──
    [ObservableProperty] private string _gateStateText = "";
    [ObservableProperty] private string _reductionText = "";
    [ObservableProperty] private string _inText = "";
    [ObservableProperty] private string _outText = "";

    /// <summary>Кривая EQ изменилась (правка или отмена).</summary>
    public event Action? CurveChanged;

    public string Title => Node.Name;

    public void Focus(string? block)
    {
        FocusBlock = null;
        FocusBlock = block;
    }

    // ── правки ──

    private void Edit(Func<ChainSettings, ChainSettings> change, string? mergeKey)
    {
        if (_loading) return;
        _owner.SetChain(Node, change(Chain), mergeKey);
    }

    private void EditGate(Func<GateSettings, GateSettings> change, string key) => Edit(c => c with { Gate = change(c.Gate) }, key);
    private void EditComp(Func<CompSettings, CompSettings> change, string key) => Edit(c => c with { Comp = change(c.Comp) }, key);

    internal void EditBand(int index, Func<EqBand, EqBand> change, string? mergeKey = "") =>
        Edit(c => c with { Eq = c.Eq.WithBand(index, change) }, mergeKey == "" ? "eq" + index : mergeKey);

    /// <summary>Кривая: перетащили точку полосы.</summary>
    public void DragBand(int index, double freq, double gainDb) =>
        EditBand(index, b => b with { FreqHz = (float)freq, GainDb = (float)gainDb });

    /// <summary>Кривая: колесо над точкой полосы — добротность.</summary>
    public void NudgeQ(int index, double delta) => Bands[index].Q.Nudge(delta);

    partial void OnGateOnChanged(bool value) => Edit(c => c with { Gate = c.Gate with { Enabled = value } }, null);
    partial void OnEqOnChanged(bool value) => Edit(c => c with { Eq = c.Eq with { Enabled = value } }, null);
    partial void OnHighPassOnChanged(bool value) => Edit(c => c with { Eq = c.Eq with { HighPass = value } }, null);
    partial void OnCompOnChanged(bool value) => Edit(c => c with { Comp = c.Comp with { Enabled = value } }, null);

    /// <summary>Конец протяжки ручки: следующая правка — новый шаг истории.</summary>
    public void EndEdit() => _owner.EndMerge();

    /// <summary>Значения из графа (после правки, отмены, повтора).</summary>
    public void Load()
    {
        var c = Chain;
        _loading = true;
        try
        {
            GateOn = c.Gate.Enabled;
            EqOn = c.Eq.Enabled;
            HighPassOn = c.Eq.HighPass;
            CompOn = c.Comp.Enabled;
            var g = c.Gate;
            double[] gate = [g.ThresholdDb, g.AttackMs, g.HoldMs, g.ReleaseMs, g.DepthDb];
            for (int i = 0; i < gate.Length; i++) GateParams[i].Load(gate[i]);
            HighPass.Load(c.Eq.HighPassHz);
            for (int i = 0; i < Bands.Count; i++) Bands[i].Load(c.Eq.Bands[i]);
            var k = c.Comp;
            double[] comp = [k.ThresholdDb, k.Ratio, k.AttackMs, k.ReleaseMs, k.KneeDb, k.MakeupDb];
            for (int i = 0; i < comp.Length; i++) CompParams[i].Load(comp[i]);
            LoadSlots();
            PresetName = Presets.FirstOrDefault(p => p.Chain.Normalized() == c)?.Name
                         ?? (c.IsActive ? Strings.Fx_PresetCustom : Strings.Fx_PresetNone);
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(Title));
        CurveChanged?.Invoke();
    }

    // ── пресеты ──

    /// <summary>Встроенные и свои пресеты.</summary>
    public IReadOnlyList<ChainPreset> Presets =>
    [
        .. ChainPresets.BuiltIn,
        .. (_owner.Preferences?.Presets ?? []).Select(p => new ChainPreset("user:" + p.Name, p.Name, p.Chain)),
    ];

    public void ApplyPreset(ChainPreset p)
    {
        _owner.SetChain(Node, p.Chain);
        _owner.Notify(Fmt.F(Strings.Fx_PresetAppliedFormat, p.Name, Node.Name));
    }

    /// <summary>Сохранить текущую цепочку как свой пресет. false — некуда сохранить или ошибка записи.</summary>
    public bool SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        if (_owner.Preferences is not { } prefs)
        {
            _owner.Notify(Strings.Fx_PresetNoSettings, error: true);
            return false;
        }
        try
        {
            prefs.SavePreset(name, Chain);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _owner.Notify(Fmt.F(Strings.Fx_PresetSaveFailedFormat, ex.Message), error: true);
            return false;
        }
        Load();
        _owner.Notify(Fmt.F(Strings.Fx_PresetSavedFormat, name));
        return true;
    }

    [RelayCommand]
    private void Close() => _owner.CloseProcessingCommand.Execute(null);

    // ── индикаторы ──

    internal void Poll()
    {
        var m = Node.ChainMeter;
        GateStateText = m.GateOpen switch
        {
            true => Strings.Fx_GateOpen,
            false => Strings.Fx_GateClosed,
            _ => "",
        };
        ReductionText = CompOn ? Fmt.Pill(-Math.Round(m.ReductionDb, 1)) : "";
        float inDb = Math.Max(m.In.L, m.In.R), outDb = Math.Max(m.Out.L, m.Out.R);
        InText = Fmt.Level(inDb);
        OutText = Fmt.Level(outDb);
        var status = _owner.Host?.Status.Plugins;
        foreach (var s in PreSlots.Concat(PostSlots))
            s.Poll(status?.FirstOrDefault(x => x.NodeId == Node.Id && x.SlotId == s.Id));
    }
}
