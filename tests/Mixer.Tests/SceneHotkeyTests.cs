using System.Windows.Input;
using System.Windows.Threading;
using Mixer.App;
using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Audio.Dsp;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;
using Mixer.App.Resources;

namespace Mixer.Tests;

/// <summary>Этап 7 (v2): сцены, горячие клавиши, push-to-talk, главный микрофон, автозапуск и трей.</summary>
public class SceneHotkeyTests
{
    private static GraphConfig Graph() => new()
    {
        Nodes =
        [
            new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, Name = "Микрофон", EndpointName = "Микрофон (fifine)", X = 40, Y = 60,
                Chain = new ChainSettings { Gate = new GateSettings { Enabled = true } } },
            new NodeConfig { Id = "game", Kind = NodeKind.App, Name = "Игра", Exe = "game.exe", X = 40, Y = 210, GainDb = -6 },
            new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput, Name = "Наушники", EndpointName = "Наушники (Realtek)", X = 892, Y = 60 },
        ],
        Wires = [new WireConfig { From = "mic", To = "hp" }, new WireConfig { From = "game", To = "hp", GainDb = -3 }],
    };

    // ── сцены: ядро ──

    [Fact]
    public void Scene_captures_levels_and_applies_them_without_touching_structure()
    {
        var g = Graph();
        var scene = Scenes.Capture(g, "s1", "Игра");
        Assert.Equal(-6, scene.Nodes["game"].GainDb);
        Assert.True(scene.Nodes["mic"].Gate);
        Assert.Null(scene.Nodes["hp"].Gate); // у выхода нет цепочки — блок не трогается

        // уровни поменяли, добавили узел и провод, которых нет в сцене
        var changed = GraphEdits.UpdateNode(g, "game", n => n with { GainDb = 0, Muted = true });
        changed = GraphEdits.UpdateNode(changed, "mic", n => n with { Chain = n.Chain! with { Gate = n.Chain.Gate with { Enabled = false } } });
        changed = GraphEdits.UpdateWire(changed, "game", "hp", w => w with { Enabled = false });
        changed = GraphEdits.Connect(GraphEdits.AddNode(changed, new NodeConfig { Id = "new", Kind = NodeKind.System, Name = "S", GainDb = -12 }), "new", "hp", -9);
        Assert.False(Scenes.Matches(changed, scene));

        var applied = Scenes.Apply(changed, scene);
        Assert.Equal("s1", applied.ActiveScene);
        Assert.True(Scenes.Matches(applied, scene));
        Assert.Equal(-6, applied.Nodes.Single(n => n.Id == "game").GainDb);
        Assert.False(applied.Nodes.Single(n => n.Id == "game").Muted);
        Assert.True(applied.Nodes.Single(n => n.Id == "mic").Chain!.Gate.Enabled);
        Assert.True(applied.Wires.Single(w => w.From == "game").Enabled);
        // новый узел и провод не тронуты, структура та же
        Assert.Equal(-12, applied.Nodes.Single(n => n.Id == "new").GainDb);
        Assert.Equal(-9, applied.Wires.Single(w => w.From == "new").GainDb);
        Assert.Equal(changed.Nodes.Select(n => n.Id), applied.Nodes.Select(n => n.Id));
        Assert.Equal(changed.Wires.Count, applied.Wires.Count);
    }

    [Fact]
    public void Scene_fade_ramps_levels_and_mutes_only_at_the_end()
    {
        var from = Graph();
        var to = GraphEdits.UpdateNode(GraphEdits.UpdateNode(from, "game", n => n with { Muted = true }), "mic", n => n with { GainDb = 6 });
        var unmute = GraphEdits.UpdateNode(to, "game", n => n with { Muted = true }); // для обратного пути

        Assert.Equal(from.Nodes.Select(n => n.GainDb), Scenes.Interpolate(from, to, 0).Nodes.Select(n => n.GainDb));
        Assert.Same(to, Scenes.Interpolate(from, to, 1));

        float prevMic = -100, prevGame = 100;
        for (double t = 0.1; t < 1; t += 0.1)
        {
            var g = Scenes.Interpolate(from, to, t);
            var mic = g.Nodes.Single(n => n.Id == "mic");
            var game = g.Nodes.Single(n => n.Id == "game");
            Assert.True(mic.GainDb > prevMic); // плавно вверх
            Assert.True(game.GainDb < prevGame); // плавно вниз к −60 дБ
            Assert.False(game.Muted); // mute — только в конце, после спада
            prevMic = mic.GainDb;
            prevGame = game.GainDb;
        }
        Assert.True(prevGame < -50);

        // включение: звук сразу, но с −60 дБ, и плавно вверх
        var back = Scenes.Interpolate(unmute, from, 0.2);
        var g2 = back.Nodes.Single(n => n.Id == "game");
        Assert.False(g2.Muted);
        Assert.InRange(g2.GainDb, Db.Min, -40);
        // позиции не плывут вслед за сценой
        Assert.Equal(from.Nodes.Select(n => (n.X, n.Y)), Scenes.Interpolate(from, to, 0.5).Nodes.Select(n => (n.X, n.Y)));
    }

    [Fact]
    public void Scene_edits_keep_one_key_per_action_and_survive_json()
    {
        var g = Scenes.Add(Graph(), Scenes.Capture(Graph(), "s1", "Игра"));
        g = Scenes.Add(g, Scenes.Capture(g, Scenes.NewId(g), "Стрим"));
        Assert.Equal("s2", g.ActiveScene);
        g = GraphEdits.SetHotkey(g, HotkeyAction.Mute, "mic", "Ctrl+M");
        g = Scenes.SetHotkey(g, "s1", "Ctrl+1");
        g = Scenes.SetHotkey(g, "s2", "Ctrl+1"); // та же клавиша у другой сцены снимается
        Assert.Null(g.Scenes[0].Hotkey);
        Assert.Equal("Ctrl+1", g.Scenes[1].Hotkey);
        g = Scenes.SetHotkey(g, "s1", "Ctrl+M"); // клавиша узла уступает сцене
        Assert.Empty(g.Hotkeys);
        g = GraphEdits.SetHotkey(g, HotkeyAction.PushToTalk, "mic", "Ctrl+M"); // и наоборот
        Assert.Null(g.Scenes[0].Hotkey);
        Assert.Single(g.Hotkeys);

        var back = GraphSerializer.Deserialize(GraphSerializer.Serialize(g with { MainMic = "mic" }));
        Assert.Equal(["Игра", "Стрим"], back.Scenes.Select(s => s.Name));
        Assert.Equal(-6, back.Scenes[0].Nodes["game"].GainDb);
        Assert.Equal(HotkeyAction.PushToTalk, back.Hotkeys[0].Action);
        Assert.Equal("mic", back.MainMic);
        Assert.Equal("s2", back.ActiveScene);
        Assert.Equal(GraphChangeKind.None, GraphChange.Classify(Graph(), Graph() with { Scenes = g.Scenes, Hotkeys = g.Hotkeys }));

        // удалённый узел уносит свои клавиши и роль главного микрофона
        var removed = GraphEdits.RemoveNode(back, "mic");
        Assert.Empty(removed.Hotkeys);
        Assert.Null(removed.MainMic);
        Assert.Null(Scenes.Delete(back, "s2").ActiveScene);
    }

    // ── сцены: интерфейс ──

    /// <summary>Прокрутить диспетчер, пока не выйдет время (таймеры перехода сцены).</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            frame.Continue = false;
        };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Header_shows_the_scene_marks_manual_changes_and_fades_in_one_undo_step()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = UiCableTests.Make(Graph());
            using var _ = store;
            Assert.Null(vm.SceneName);
            vm.NewScene("Игра");
            Assert.Equal("Игра", vm.SceneName);
            Assert.False(vm.SceneDirty);

            vm.FindNode("game")!.GainDb = 0;
            vm.EndMerge();
            Assert.True(vm.SceneDirty); // поправили руками — точка

            vm.ResetSceneCommand.Execute(null); // «Сбросить»: плавно назад за 150 мс
            Pump(400);
            Assert.False(vm.SceneDirty);
            Assert.Equal(-6, vm.FindNode("game")!.GainDb);
            vm.UndoCommand.Execute(null); // весь переход — один шаг
            Assert.Equal(0, vm.FindNode("game")!.GainDb);

            vm.SaveSceneCommand.Execute(null); // «Сохранить»: текущие уровни в сцену
            Assert.False(vm.SceneDirty);
            Assert.Equal(0, vm.ActiveScene!.Nodes["game"].GainDb);

            vm.NewScene();
            Assert.Equal("Сцена 1", vm.SceneName);
            vm.RenameScene(vm.ActiveScene!.Id, "Стрим");
            Assert.Equal("Стрим", vm.SceneName);
            vm.Dispose();
        });
    }

    [Fact]
    public void Scene_switch_runs_the_fade_to_the_end()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = UiCableTests.Make(Graph());
            using var _ = store;
            vm.NewScene("A");
            string a = vm.ActiveScene!.Id;
            vm.Edit(g => GraphEdits.UpdateNode(g, "game", n => n with { GainDb = -30, Muted = true }));
            vm.NewScene("B");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            vm.ApplyScene(a);
            while (vm.FindNode("game")!.GainDb != -6 && sw.ElapsedMilliseconds < 3000) Pump(5);
            // переход идёт по часам и кончается не раньше FadeMs; время до конца — по шагам в AcceptanceV21Tests
            // (здесь таймер диспетчера запаздывает, пока параллельно идут другие тесты)
            Assert.True(sw.ElapsedMilliseconds >= Scenes.FadeMs - 20, $"{sw.ElapsedMilliseconds} ms");
            Assert.Equal(-6, vm.FindNode("game")!.GainDb);
            Assert.False(vm.FindNode("game")!.Muted);
            vm.Dispose();
        });
    }

    // ── горячие клавиши ──

    [Fact]
    public void Key_chords_parse_and_print_the_same_way()
    {
        Assert.Equal("Ctrl+Shift+M", KeyChord.Parse("shift+ctrl+m")!.Value.ToString());
        Assert.Equal(new KeyChord(ModifierKeys.Control, Key.D1), KeyChord.Parse("Ctrl+1"));
        Assert.Equal("Ctrl+1", new KeyChord(ModifierKeys.Control, Key.D1).ToString());
        Assert.Equal("Ctrl+Alt+NumPad1", KeyChord.Normalize("control + alt + numpad1"));
        Assert.Equal("F13", KeyChord.Parse("F13")!.Value.ToString());
        Assert.Null(KeyChord.Parse("Ctrl+"));
        Assert.Null(KeyChord.Parse("Ctrl+A+B"));
        Assert.Null(KeyChord.Parse("Shift"));
        Assert.Null(KeyChord.FromKey(Key.LeftCtrl, ModifierKeys.Control));
        Assert.Equal(6u, new KeyChord(ModifierKeys.Control | ModifierKeys.Shift, Key.M).NativeModifiers);
        Assert.Equal(0x4Du, new KeyChord(ModifierKeys.Control, Key.M).VirtualKey);
        // одна клавиша без модификатора перехватывала бы набор текста; F-клавиши и цифровой блок можно
        Assert.True(MainViewModel.NeedsModifier(new KeyChord(ModifierKeys.None, Key.M), HotkeyAction.Mute));
        Assert.False(MainViewModel.NeedsModifier(new KeyChord(ModifierKeys.None, Key.F13), HotkeyAction.Mute));
        Assert.False(MainViewModel.NeedsModifier(new KeyChord(ModifierKeys.None, Key.NumPad5), HotkeyAction.VolumeUp));
        Assert.False(MainViewModel.NeedsModifier(new KeyChord(ModifierKeys.None, Key.CapsLock), HotkeyAction.PushToTalk)); // хук только смотрит
    }

    [Fact]
    public void Hotkeys_are_checked_when_assigned_and_run_their_action()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = UiCableTests.Make(Graph());
            using var _ = store;
            var taken = new KeyChord(ModifierKeys.Control | ModifierKeys.Alt, Key.Delete);
            vm.IsChordFree = c => c != taken;
            int changed = 0;
            vm.HotkeysChanged += () => changed++;
            var fired = new List<string>();
            vm.HotkeyFired += fired.Add;
            var mic = vm.FindNode("mic")!;

            Assert.Equal(Strings.Hotkey_NeedModifier, vm.AssignHotkey(HotkeyAction.Mute, mic, new KeyChord(ModifierKeys.None, Key.M)));
            Assert.Contains("Ctrl+Alt+Delete", vm.AssignHotkey(HotkeyAction.Mute, mic, taken)); // занято — сразу видно
            Assert.Null(vm.AssignHotkey(HotkeyAction.Mute, mic, new KeyChord(ModifierKeys.Control, Key.M)));
            Assert.Equal("Ctrl+M", vm.HotkeyOf(HotkeyAction.Mute, "mic"));
            Assert.Equal(1, changed);

            // своё сочетание можно переназначить: прежнее действие снимается, об этом сообщение
            Assert.Null(vm.AssignHotkey(HotkeyAction.VolumeUp, vm.FindNode("game")!, new KeyChord(ModifierKeys.Control, Key.M)));
            Assert.Null(vm.HotkeyOf(HotkeyAction.Mute, "mic"));
            Assert.Contains("Ctrl+M", vm.Toast);

            vm.RunHotkey(new HotkeyConfig { Action = HotkeyAction.Mute, Target = "mic", Keys = "Ctrl+M" });
            Assert.True(mic.Muted);
            Assert.Equal("Микрофон: выключен", fired[^1]);
            vm.RunHotkey(new HotkeyConfig { Action = HotkeyAction.VolumeUp, Target = "game", Keys = "Ctrl+M" });
            vm.RunHotkey(new HotkeyConfig { Action = HotkeyAction.VolumeUp, Target = "game", Keys = "Ctrl+M" });
            Assert.Equal(-4, vm.FindNode("game")!.GainDb);
            Assert.Equal("Игра: −4,0 дБ", fired[^1]);
            vm.UndoCommand.Execute(null); // ±1 дБ подряд — один шаг
            Assert.Equal(-6, vm.FindNode("game")!.GainDb);

            Assert.Null(vm.AssignHotkey(HotkeyAction.VolumeUp, vm.FindNode("game")!, null)); // Backspace — снять
            Assert.Empty(vm.Graph.Hotkeys);
            vm.Dispose();
        });
    }

    [Fact]
    public void Push_to_talk_keeps_the_node_silent_until_the_key_is_held_without_touching_history()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = UiCableTests.Make(Graph());
            using var _ = store;
            var mic = vm.FindNode("mic")!;
            vm.SetMainMic("mic");
            Assert.False(vm.MainMicMuted);
            Assert.Null(vm.AssignHotkey(HotkeyAction.PushToTalk, mic, new KeyChord(ModifierKeys.None, Key.CapsLock)));
            var hk = vm.Graph.Hotkeys.Single();
            Assert.Equal(new KeyChord(ModifierKeys.None, Key.CapsLock), vm.PushHotkeys.Single().Chord);

            Assert.True(mic.PushMuted);
            Assert.False(mic.Muted); // схема не меняется
            Assert.True(vm.Effective(vm.Graph).Nodes.Single(n => n.Id == "mic").Muted); // а хост слышит mute
            Assert.True(vm.MainMicMuted);
            Assert.Equal("push-to-talk · молчит", mic.Subtitle);

            var before = vm.Graph;
            vm.RunPush(hk, down: true);
            Assert.False(vm.Effective(vm.Graph).Nodes.Single(n => n.Id == "mic").Muted);
            Assert.Equal("push-to-talk · в эфире", mic.Subtitle);
            vm.RunPush(hk, down: false);
            Assert.True(vm.Effective(vm.Graph).Nodes.Single(n => n.Id == "mic").Muted);
            Assert.Same(before, vm.Graph); // мимо истории и graph.json

            // push-to-mute: молчит, пока зажата
            Assert.Null(vm.AssignHotkey(HotkeyAction.PushToMute, vm.FindNode("game")!, new KeyChord(ModifierKeys.None, Key.F14)));
            var ptm = vm.Graph.Hotkeys.Single(h => h.Action == HotkeyAction.PushToMute);
            Assert.False(vm.Effective(vm.Graph).Nodes.Single(n => n.Id == "game").Muted);
            vm.RunPush(ptm, down: true);
            Assert.True(vm.Effective(vm.Graph).Nodes.Single(n => n.Id == "game").Muted);
            vm.RunPush(ptm, down: false);
            Assert.False(vm.Effective(vm.Graph).Nodes.Single(n => n.Id == "game").Muted);

            // клавишу сняли — узел снова звучит
            Assert.Null(vm.AssignHotkey(HotkeyAction.PushToTalk, mic, null));
            Assert.Null(mic.PushMuted);
            Assert.False(vm.Effective(vm.Graph).Nodes.Single(n => n.Id == "mic").Muted);

            vm.ToggleMainMic(); // меню трея
            Assert.True(mic.Muted);
            Assert.True(vm.MainMicMuted);
            vm.Dispose();
        });
    }

    // ── трей, автозапуск, настройки ──

    [Fact]
    public void Tray_icon_shows_the_microphone_state()
    {
        WpfThread.Run(() =>
        {
            byte[] on = TrayIcons.Pixels(TrayState.MicOn), off = TrayIcons.Pixels(TrayState.MicOff), plain = TrayIcons.Pixels(TrayState.Normal);
            (byte R, byte G, byte B, byte A) At(byte[] px, int x, int y) => (px[(y * 32 + x) * 4 + 2], px[(y * 32 + x) * 4 + 1], px[(y * 32 + x) * 4], px[(y * 32 + x) * 4 + 3]);
            var g = At(on, 25, 25);
            Assert.True(g.G > 150 && g.R < 120, $"green badge: {g}");
            var r = At(off, 23, 21);
            Assert.True(r.R > 200 && r.G < 140, $"red badge: {r}");
            Assert.Equal(At(plain, 6, 6), At(on, 6, 6)); // логотип тот же
            Assert.Equal(255, At(plain, 16, 16).A);
            Assert.Equal(0, At(plain, 0, 0).A); // скруглённый угол прозрачен
        });
    }

    [Fact]
    public void Startup_switches_and_restart_arguments()
    {
        var o = AppOptions.Parse(["--minimized", "--theme", "daylight", "--graph", "g.json"]);
        Assert.True(o.Minimized);
        Assert.Equal("daylight", o.Theme);
        // после перезапуска (смена темы или языка) окно видно, тема и язык — из настроек
        Assert.Equal(["--graph", "g.json"], Mixer.App.App.RestartArgs(["--minimized", "--theme", "modular", "--graph", "g.json", "--lang", "en"]));
    }

    [Fact]
    public void Preferences_keep_theme_and_tray_flags()
    {
        string path = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        var p = Preferences.Load(path, ru, out _);
        Assert.Equal("graphite", p.Theme);
        Assert.True(p.CloseToTray);
        Assert.True(p.HotkeyToasts);
        Assert.True(p.DeviceNotifications);
        Assert.False(p.TrayHintShown);
        p.SetTheme("modular");
        p.SetCloseToTray(false);
        p.SetHotkeyToasts(false);
        p.MarkTrayHintShown();
        p.MarkSetupShown();
        p.SetLanguage("en");
        var back = Preferences.Load(path, ru, out string? error);
        Assert.Null(error);
        Assert.Equal("modular", back.Theme);
        Assert.False(back.CloseToTray);
        Assert.False(back.HotkeyToasts);
        Assert.True(back.DeviceNotifications);
        Assert.True(back.TrayHintShown);
        Assert.True(back.SetupShown);
        Assert.Equal("en", back.Language);
        Assert.Throws<ArgumentOutOfRangeException>(() => back.SetTheme("neon"));
        File.WriteAllText(path, """{ "language": "ru", "theme": "neon" }""");
        Assert.Equal("graphite", Preferences.Load(path, ru, out _).Theme); // неизвестная тема — по умолчанию
    }

    [Fact]
    public void Theme_choice_is_saved_and_asks_for_a_restart()
    {
        WpfThread.Run(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "settings.json");
            var p = Preferences.Load(path, System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), out _);
            int restarts = 0;
            var t = new ThemeSettings(p, () => restarts++, ThemeManager.Graphite);
            Assert.Equal(["graphite", "daylight", "modular"], t.Options.Select(o => o.Code));
            Assert.True(t.Options[0].IsCurrent);
            Assert.False(t.NeedsRestart);
            t.ChooseCommand.Execute(t.Options[2]);
            Assert.Equal("modular", p.Theme);
            Assert.True(t.NeedsRestart);
            Assert.Equal(Strings.Theme_RestartNote, t.Note);
            t.RestartCommand.Execute(null);
            Assert.Equal(1, restarts);
            t.ChooseCommand.Execute(t.Options[0]);
            Assert.False(t.NeedsRestart); // вернули ту, с которой запущены
        });
    }
}
