using System.ComponentModel;
using Mixer.App.ViewModels;
using Mixer.Core.Config;

namespace Mixer.App.Ui;

/// <summary>
/// Горячие клавиши схемы — в Windows: RegisterHotKey для mute, громкости и сцен, хук клавиатуры — только если
/// назначен push-to-talk или push-to-mute. Перерегистрируется при правке клавиш; пока в поле назначения ловят
/// сочетание, всё отпущено.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly GlobalHotkeys _keys = new();
    private readonly Dictionary<int, Action> _actions = new();
    private KeyboardHook? _hook;
    private string? _hookError;

    public HotkeyService(MainViewModel vm)
    {
        _vm = vm;
        _vm.IsChordFree = _keys.IsFree;
        _vm.HotkeysChanged += Apply;
        _vm.PropertyChanged += OnVmChanged;
        _keys.Pressed += id =>
        {
            if (_actions.TryGetValue(id, out var a)) a();
        };
        Apply();
    }

    /// <summary>Хук push-to-talk стоит.</summary>
    public bool HookInstalled => _hook?.Installed == true;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CapturingHotkey)) Apply();
    }

    private void Apply()
    {
        _keys.UnregisterAll();
        _actions.Clear();
        if (_vm.CapturingHotkey)
        {
            _hook?.SetChords([]);
            return;
        }
        var g = _vm.Graph;
        int id = 1;
        var failed = new List<string>();
        foreach (var h in g.Hotkeys.Where(h => h.Action is not (HotkeyAction.PushToTalk or HotkeyAction.PushToMute)))
        {
            if (KeyChord.Parse(h.Keys) is not { } c) continue;
            var hk = h;
            if (_keys.Register(id, c, repeat: h.Action is HotkeyAction.VolumeUp or HotkeyAction.VolumeDown)) _actions[id] = () => _vm.RunHotkey(hk);
            else failed.Add(c.ToString());
            id++;
        }
        foreach (var s in g.Scenes.Where(s => s.Hotkey is not null))
        {
            if (KeyChord.Parse(s.Hotkey) is not { } c) continue;
            string sid = s.Id;
            if (_keys.Register(id, c)) _actions[id] = () => _vm.RunSceneHotkey(sid);
            else failed.Add(c.ToString());
            id++;
        }
        if (_actions.Count > 0) App.Log?.Write("hotkeys", $"registered {_actions.Count}: " + string.Join(", ", g.Hotkeys.Select(h => h.Keys).Concat(g.Scenes.Select(s => s.Hotkey)).OfType<string>()));
        if (failed.Count > 0)
        {
            string text = string.Join(" ", failed.Select(k => Fmt.F(Strings.Hotkey_RegisterFailedFormat, k)));
            _vm.Notify(text, error: true);
            App.Log?.Write("hotkeys", "not registered (taken): " + string.Join(", ", failed));
        }

        // push-to-talk: хук только когда он нужен (античиты и антивирусы не любят хуки клавиатуры)
        var push = _vm.PushHotkeys;
        if (push.Count == 0)
        {
            _hook?.Dispose();
            _hook = null;
            return;
        }
        if (_hook is null)
        {
            _hook = new KeyboardHook(push.Select(p => p.Chord));
            _hook.Changed += OnPush;
            if (!_hook.Installed)
            {
                _hookError = Strings.Hotkey_HookFailed;
                _vm.Notify(_hookError, error: true);
                App.Log?.Write("hotkeys", "keyboard hook refused by Windows");
            }
            else App.Log?.Write("hotkeys", "push-to-talk keyboard hook installed");
        }
        else _hook.SetChords(push.Select(p => p.Chord));
    }

    private void OnPush(KeyChord chord, bool down)
    {
        foreach (var (c, h) in _vm.PushHotkeys)
            if (c == chord) _vm.RunPush(h, down);
    }

    public void Dispose()
    {
        _vm.HotkeysChanged -= Apply;
        _vm.PropertyChanged -= OnVmChanged;
        _hook?.Dispose();
        _keys.Dispose();
    }
}
