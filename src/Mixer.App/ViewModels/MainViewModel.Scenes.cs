using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixer.App.Ui;
using Mixer.Core.Config;
using Mixer.Core.Editing;

namespace Mixer.App.ViewModels;

/// <summary>Сцены, горячие клавиши, push-to-talk и главный микрофон (v2, «Удобство: сцены, горячие клавиши, трей»).</summary>
public sealed partial class MainViewModel
{
    // ── сцены ──

    private DispatcherTimer? _sceneTimer;

    /// <summary>Имя текущей сцены в шапке (null — сцены не выбраны).</summary>
    [ObservableProperty] private string? _sceneName;
    /// <summary>Текущую сцену поправили руками: точка рядом с именем, кнопки «Сохранить» и «Сбросить».</summary>
    [ObservableProperty] private bool _sceneDirty;
    /// <summary>Подсказка с клавишами сцен: «Ctrl+1 Игра · Ctrl+2 Стрим».</summary>
    [ObservableProperty] private string _sceneKeys = "";

    public IReadOnlyList<SceneConfig> SceneList => Graph.Scenes;
    public SceneConfig? ActiveScene => Graph.Scenes.FirstOrDefault(s => s.Id == Graph.ActiveScene);

    /// <summary>Сцены сменились (меню трея перестраивается).</summary>
    public event Action? ScenesChanged;

    private void UpdateSceneState()
    {
        var a = ActiveScene;
        SceneName = a?.Name;
        // пока идёт переход, промежуточные уровни не «правка руками»
        SceneDirty = a is not null && _sceneTimer is null && !Scenes.Matches(Graph, a);
        SceneKeys = string.Join(" · ", Graph.Scenes.Where(s => s.Hotkey is not null).Select(s => $"{s.Hotkey} {s.Name}"));
        OnPropertyChanged(nameof(SceneList));
        ScenesChanged?.Invoke();
    }

    /// <summary>Применить сцену плавно, за <see cref="Scenes.FadeMs"/>: один шаг истории.</summary>
    public void ApplyScene(string id)
    {
        if (Graph.Scenes.FirstOrDefault(s => s.Id == id) is not { } scene) return;
        StopSceneFade();
        var from = Graph;
        var to = Scenes.Apply(from, scene);
        var sw = Stopwatch.StartNew();
        _sceneTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _sceneTimer.Tick += (_, _) =>
        {
            double t = sw.Elapsed.TotalMilliseconds / Scenes.FadeMs;
            if (t >= 1)
            {
                StopSceneFade();
                Edit(_ => to, "scene");
                EndMerge();
                UpdateSceneState();
                return;
            }
            Edit(_ => Scenes.Interpolate(from, to, t), "scene");
        };
        Edit(_ => Scenes.Interpolate(from, to, 0.001) with { ActiveScene = id }, "scene");
        _sceneTimer.Start();
    }

    /// <summary>Применить сцену сразу (тесты и снимок без плавного перехода).</summary>
    public void ApplySceneNow(string id)
    {
        if (Graph.Scenes.FirstOrDefault(s => s.Id == id) is not { } scene) return;
        StopSceneFade();
        Edit(g => Scenes.Apply(g, scene));
    }

    private void StopSceneFade()
    {
        _sceneTimer?.Stop();
        _sceneTimer = null;
    }

    /// <summary>Новая сцена из текущих уровней: «Сцена 1», «Сцена 2»… (или с заданным именем).</summary>
    public void NewScene(string? name = null)
    {
        var g = Graph;
        var used = g.Scenes.Select(s => s.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        name = string.IsNullOrWhiteSpace(name) ? Enumerable.Range(1, 1000).Select(i => Fmt.F(Strings.Scene_NameFormat, i)).First(n => !used.Contains(n)) : name.Trim();
        Edit(x => Scenes.Add(x, Scenes.Capture(x, Scenes.NewId(x), name)));
        Notify(Fmt.F(Strings.Scene_CreatedFormat, name));
    }

    [RelayCommand]
    private void SaveScene()
    {
        if (ActiveScene is { } a) Edit(g => Scenes.Save(g, a.Id));
        else NewScene();
    }

    [RelayCommand]
    private void ResetScene()
    {
        if (ActiveScene is { } a) ApplyScene(a.Id);
    }

    public void RenameScene(string id, string name) => Edit(g => Scenes.Rename(g, id, name));

    public void DeleteScene(string id) => Edit(g => Scenes.Delete(g, id));

    // ── горячие клавиши ──

    /// <summary>Проверка сочетания в Windows (RegisterHotKey); null — без проверки (тесты, нет окна).</summary>
    public Func<KeyChord, bool>? IsChordFree { get; set; }

    /// <summary>Горячие клавиши в графе изменились: пора перерегистрировать.</summary>
    public event Action? HotkeysChanged;

    /// <summary>Сработала горячая клавиша: короткое подтверждение у трея.</summary>
    public event Action<string>? HotkeyFired;

    /// <summary>Пока в поле назначения ловят сочетание, свои горячие клавиши отпущены (иначе их сработает действие).</summary>
    [ObservableProperty] private bool _capturingHotkey;

    private string _hotkeysKey = "";

    private void CheckHotkeysChanged(GraphConfig g)
    {
        string key = string.Join(";", g.Hotkeys.Select(h => $"{h.Action}:{h.Target}:{h.Keys}"))
                     + "|" + string.Join(";", g.Scenes.Select(s => $"{s.Id}:{s.Hotkey}"));
        if (key == _hotkeysKey) return;
        _hotkeysKey = key;
        SyncPushOverrides(g);
        HotkeysChanged?.Invoke();
    }

    /// <summary>Клавиша действия над узлом. Нажата без модификатора — только F1…F24, цифровой блок и мультимедийные (кроме push-to-talk).</summary>
    public static bool NeedsModifier(KeyChord c, HotkeyAction action) =>
        action is not (HotkeyAction.PushToTalk or HotkeyAction.PushToMute)
        && c.Modifiers == System.Windows.Input.ModifierKeys.None
        && c.Key is not (>= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24)
        && c.Key is not (>= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.Divide)
        && c.Key is not (>= System.Windows.Input.Key.BrowserBack and <= System.Windows.Input.Key.LaunchApplication2)
        && c.Key is not (System.Windows.Input.Key.Pause or System.Windows.Input.Key.Scroll);

    /// <summary>Назначить клавишу действию над узлом (null — снять). Возвращает причину отказа или null.</summary>
    public string? AssignHotkey(HotkeyAction action, NodeViewModel n, KeyChord? chord)
    {
        if (chord is { } c)
        {
            if (NeedsModifier(c, action)) return Strings.Hotkey_NeedModifier;
            if (Check(c, h => h.Action == action && h.Target == n.Id, null) is { } why) return why;
        }
        string? keys = chord?.ToString();
        string? taken = keys is null ? null : TakenBy(keys, h => h.Action == action && h.Target == n.Id, null);
        Edit(g => GraphEdits.SetHotkey(g, action, n.Id, keys));
        if (taken is not null) Notify(Fmt.F(Strings.Hotkey_ReassignedFormat, keys, taken));
        return null;
    }

    /// <summary>Назначить клавишу сцене (null — снять).</summary>
    public string? AssignSceneHotkey(string sceneId, KeyChord? chord)
    {
        if (chord is { } c)
        {
            if (NeedsModifier(c, HotkeyAction.Mute)) return Strings.Hotkey_NeedModifier;
            if (Check(c, _ => false, sceneId) is { } why) return why;
        }
        string? keys = chord?.ToString();
        string? taken = keys is null ? null : TakenBy(keys, _ => false, sceneId);
        Edit(g => Scenes.SetHotkey(g, sceneId, keys));
        if (taken is not null) Notify(Fmt.F(Strings.Hotkey_ReassignedFormat, keys, taken));
        return null;
    }

    /// <summary>Занято ли сочетание в Windows (наши собственные клавиши не в счёт: их можно переназначить).</summary>
    private string? Check(KeyChord c, Func<HotkeyConfig, bool> self, string? selfScene)
    {
        string keys = c.ToString();
        bool ours = Graph.Hotkeys.Any(h => string.Equals(h.Keys, keys, StringComparison.OrdinalIgnoreCase))
                    || Graph.Scenes.Any(s => string.Equals(s.Hotkey, keys, StringComparison.OrdinalIgnoreCase));
        if (!ours && IsChordFree is { } free && !free(c)) return Fmt.F(Strings.Hotkey_TakenFormat, keys);
        return null;
    }

    /// <summary>Кому сейчас принадлежит сочетание (кроме того, кому его назначают).</summary>
    private string? TakenBy(string keys, Func<HotkeyConfig, bool> self, string? selfScene)
    {
        var h = Graph.Hotkeys.FirstOrDefault(x => string.Equals(x.Keys, keys, StringComparison.OrdinalIgnoreCase) && !self(x));
        if (h is not null) return DescribeHotkey(h);
        var s = Graph.Scenes.FirstOrDefault(x => x.Id != selfScene && string.Equals(x.Hotkey, keys, StringComparison.OrdinalIgnoreCase));
        return s is null ? null : Fmt.F(Strings.Hotkey_SceneFormat, s.Name);
    }

    public string DescribeHotkey(HotkeyConfig h) => Fmt.F(Strings.Hotkey_ActionOnFormat, ActionName(h.Action), FindNode(h.Target)?.Name ?? h.Target);

    public static string ActionName(HotkeyAction a) => a switch
    {
        HotkeyAction.Mute => Strings.Hotkey_Mute,
        HotkeyAction.VolumeUp => Strings.Hotkey_VolumeUp,
        HotkeyAction.VolumeDown => Strings.Hotkey_VolumeDown,
        HotkeyAction.PushToTalk => Strings.Hotkey_PushToTalk,
        _ => Strings.Hotkey_PushToMute,
    };

    /// <summary>Клавиша действия над узлом (null — не назначена).</summary>
    public string? HotkeyOf(HotkeyAction action, string nodeId) => Graph.Hotkeys.FirstOrDefault(h => h.Action == action && h.Target == nodeId)?.Keys;

    /// <summary>Горячая клавиша нажата (RegisterHotKey): mute, громкость ±1 дБ.</summary>
    public void RunHotkey(HotkeyConfig h)
    {
        if (FindNode(h.Target) is not { } n) return;
        switch (h.Action)
        {
            case HotkeyAction.Mute:
                Edit(g => GraphEdits.UpdateNode(g, n.Id, c => c with { Muted = !c.Muted }));
                HotkeyFired?.Invoke(Fmt.F(n.Config.Muted ? Strings.Hotkey_FiredMutedFormat : Strings.Hotkey_FiredOnFormat, n.Name));
                break;
            case HotkeyAction.VolumeUp or HotkeyAction.VolumeDown:
                double db = Math.Clamp(n.GainDb + (h.Action == HotkeyAction.VolumeUp ? 1 : -1), Mixer.Audio.Dsp.Db.Min, Mixer.Audio.Dsp.Db.Max);
                Edit(g => GraphEdits.UpdateNode(g, n.Id, c => c with { GainDb = (float)Math.Round(db, 1) }), "hotkey-gain:" + n.Id);
                HotkeyFired?.Invoke(Fmt.F(Strings.Hotkey_FiredGainFormat, n.Name, Fmt.Gain(db)));
                break;
        }
    }

    /// <summary>Клавиша сцены.</summary>
    public void RunSceneHotkey(string sceneId)
    {
        if (Graph.Scenes.FirstOrDefault(s => s.Id == sceneId) is not { } s) return;
        ApplyScene(s.Id);
        HotkeyFired?.Invoke(Fmt.F(Strings.Hotkey_FiredSceneFormat, s.Name));
    }

    // ── push-to-talk ──

    /// <summary>Узлы под push-to-talk / push-to-mute: молчит ли узел сейчас (поверх его mute в схеме, мимо истории).</summary>
    private readonly Dictionary<string, bool> _push = new();

    /// <summary>Сочетания push-to-talk и push-to-mute (для хука клавиатуры).</summary>
    public IReadOnlyList<(KeyChord Chord, HotkeyConfig Hotkey)> PushHotkeys =>
        Graph.Hotkeys.Where(h => h.Action is HotkeyAction.PushToTalk or HotkeyAction.PushToMute)
            .Select(h => (KeyChord.Parse(h.Keys), h)).Where(x => x.Item1 is not null).Select(x => (x.Item1!.Value, x.h)).ToList();

    /// <summary>
    /// Состояние push-to-talk: узел под push-to-talk молчит, пока клавиша не зажата; под push-to-mute — звучит.
    /// Это не правка схемы: история и graph.json его не видят, хост получает схему с наложенным mute.
    /// </summary>
    private void SyncPushOverrides(GraphConfig g)
    {
        var want = g.Hotkeys.Where(h => h.Action == HotkeyAction.PushToTalk).Select(h => h.Target).ToHashSet();
        bool changed = false;
        foreach (string id in _push.Keys.Where(id => !want.Contains(id)).ToList())
        {
            _push.Remove(id);
            changed = true;
        }
        foreach (string id in want.Where(id => !_push.ContainsKey(id)))
        {
            _push[id] = true;
            changed = true;
        }
        if (changed) PushChanged();
    }

    /// <summary>Клавиша push-to-talk / push-to-mute зажата или отпущена (хук клавиатуры).</summary>
    public void RunPush(HotkeyConfig h, bool down)
    {
        if (FindNode(h.Target) is null) return;
        if (h.Action == HotkeyAction.PushToTalk) _push[h.Target] = !down;
        else if (down) _push[h.Target] = true;
        else _push.Remove(h.Target);
        PushChanged();
    }

    private void PushChanged()
    {
        _host?.Update(Effective(Graph));
        foreach (var n in Nodes) n.PushMuted = _push.TryGetValue(n.Id, out bool m) ? m : null;
        WiresInvalidated?.Invoke();
        MicStateChanged?.Invoke();
        Poll(); // подпись «push-to-talk · в эфире» — сразу, а не на следующем опросе
    }

    /// <summary>Схема для хоста: mute push-to-talk поверх схемы.</summary>
    public GraphConfig Effective(GraphConfig g) => _push.Count == 0 ? g : g with
    {
        Nodes = g.Nodes.Select(n => _push.TryGetValue(n.Id, out bool muted) && muted && !n.Muted ? n with { Muted = true } : n).ToList(),
    };

    // ── главный микрофон ──

    /// <summary>Главный микрофон включился или выключился (значок в трее).</summary>
    public event Action? MicStateChanged;

    public NodeViewModel? MainMic => Graph.MainMic is { } id ? FindNode(id) : null;

    /// <summary>Главный микрофон сейчас молчит (mute или push-to-talk не зажат). null — главного микрофона нет.</summary>
    public bool? MainMicMuted => MainMic is { } m ? m.Muted || m.PushMuted == true : null;

    public void SetMainMic(string? id) => Edit(g => GraphEdits.SetMainMic(g, id));

    /// <summary>Меню трея: выключить или включить главный микрофон.</summary>
    public void ToggleMainMic()
    {
        if (MainMic is not { } m) return;
        Edit(g => GraphEdits.UpdateNode(g, m.Id, c => c with { Muted = !c.Muted }));
    }
}
