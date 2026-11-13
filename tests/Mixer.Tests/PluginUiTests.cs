using System.Diagnostics;
using System.Text.Json.Nodes;
using Mixer.App;
using Mixer.App.ViewModels;
using Mixer.Audio.Dsp;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Plugins;
using Mixer.Plugins.Catalog;
using Mixer.Plugins.Host;

namespace Mixer.Tests;

/// <summary>VST, этап 4: правки слотов, сцены, стойка Modular, выбор плагина, файлы состояния и пресеты.</summary>
[Collection(nameof(SerialTiming))] // процессы хостов: не добавлять нагрузки параллельным тестам хоста
public sealed class PluginUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mixer-tests", "vst4-" + Guid.NewGuid().ToString("N"));

    public PluginUiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    private static PluginConfig Slot(string id, PluginGroup g = PluginGroup.Pre, bool on = true) =>
        new() { Id = id, Group = g, Enabled = on, ClassId = "C" + id, Name = "Fx " + id };

    private static GraphConfig Graph(params PluginConfig[] slots) => new()
    {
        Nodes =
        [
            new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, Name = "Микрофон", Plugins = slots.Length == 0 ? null : [.. slots] },
            new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput, Name = "Наушники", X = 600 },
        ],
        Wires = [new WireConfig { From = "mic", To = "hp" }],
    };

    [Fact]
    public void Edits_KeepTwoPerGroup_AndMoveBetweenGroups()
    {
        var g = Graph(Slot("p1"), Slot("p2"), Slot("p3", PluginGroup.Post));
        var mic = GraphEdits.Find(g, "mic")!;
        Assert.False(PluginEdits.HasRoom(g, mic, PluginGroup.Pre));
        Assert.True(PluginEdits.HasRoom(g, mic, PluginGroup.Post));
        // ID свободен в узле, но от прежнего слота остался файл состояния — берётся следующий
        Assert.Equal("p5", PluginEdits.NewSlotId(mic, id => id == "p4"));

        g = PluginEdits.Move(g, "mic", "p2", PluginGroup.Post, 0); // в начало «после»
        Assert.Equal(["p1", "p2", "p3"], g.Nodes[0].Plugins!.Select(p => p.Id));
        Assert.Equal(["p2", "p3"], PluginEdits.Group(g.Nodes[0], PluginGroup.Post).Select(p => p.Id));
        g = PluginEdits.Move(g, "mic", "p2", PluginGroup.Post, int.MaxValue); // в конец
        Assert.Equal(["p3", "p2"], PluginEdits.Group(g.Nodes[0], PluginGroup.Post).Select(p => p.Id));
        g = PluginEdits.Move(g, "mic", "p1", PluginGroup.Pre, 0);
        Assert.Equal(PluginGroup.Pre, g.Nodes[0].Plugins!.Single(p => p.Id == "p1").Group);

        g = PluginEdits.Replace(g, "mic", "p3", Slot("p9")); // заменённый остаётся в своей группе и на своём месте
        Assert.Equal(["p9", "p2"], PluginEdits.Group(g.Nodes[0], PluginGroup.Post).Select(p => p.Id));
        foreach (string id in new[] { "p1", "p9", "p2" }) g = PluginEdits.Remove(g, "mic", id);
        Assert.Null(g.Nodes[0].Plugins);
        Assert.False(g.Nodes[0].HasPlugins);
    }

    [Fact]
    public void Scenes_RememberOnlyWhetherSlotsAreOn()
    {
        var g = Graph(Slot("p1"), Slot("p2", PluginGroup.Post, on: false));
        var scene = Scenes.Capture(g, "s1", "Стрим");
        var off = PluginEdits.Update(g, "mic", "p1", p => p with { Enabled = false, State = "plugins/other.state" });
        Assert.False(Scenes.Matches(off, scene));
        var back = Scenes.Apply(off, scene);
        Assert.True(back.Nodes[0].Plugins![0].Enabled);
        Assert.False(back.Nodes[0].Plugins![1].Enabled);
        Assert.Equal("plugins/other.state", back.Nodes[0].Plugins![0].State); // состояние плагина сцена не трогает
        Assert.True(Scenes.Matches(back, scene));
        // в файле сцены — только включение
        var json = GraphSerializer.Serialize(Scenes.Add(back, scene));
        Assert.Contains("\"p2\": false", json);
    }

    [Fact]
    public void Rack_IsWiderBy4HpPerSlot()
    {
        var without = RackLayout.Compute(Graph(), 2000).Modules["mic"];
        var with = RackLayout.Compute(Graph(Slot("p1"), Slot("p2", PluginGroup.Post)), 2000).Modules["mic"];
        Assert.Equal(without.Width + 2 * RackLayout.PluginWidth, with.Width);
        Assert.Equal(without.Output!.X, with.Output!.X); // гнездо OUT — посередине панели узла, не всей стойки
    }

    private string FakeBundle(string name, string cid, string sub)
    {
        string bundle = Path.Combine(_dir, "vst", name + ".vst3");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-win"));
        File.WriteAllBytes(Path.Combine(bundle, "Contents", "x86_64-win", name + ".vst3"), []);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        File.WriteAllText(PluginFiles.ModuleInfoPath(bundle),
            $"{{ \"Name\": \"{name}\", \"Factory Info\": {{ \"Vendor\": \"Example Audio\" }}, \"Classes\": [ {{ \"CID\": \"{cid}\", " +
            $"\"Category\": \"Audio Module Class\", \"Name\": \"{name}\", \"Version\": \"1.0\", \"SDKVersion\": \"VST 3.8\", \"Sub Categories\": [\"Fx\", \"{sub}\"] }} ] }}");
        return bundle;
    }

    /// <summary>
    /// Ошибка этапа 4, найдена на этапе 5: StateFolder не передавался (строка закомментирована), и слот читал
    /// plugins\ от текущей папки. Выбор ищет свободный ID рядом с graph.json, файла там нет — новый Graillon получал
    /// ID и файл, где лежало состояние Supermassive («IComponent::setState failed»). Выбор и слот — один файл.
    /// </summary>
    [Fact]
    public void Rack_AndPicker_UseTheSameStateFile_NextToTheGraph()
    {
        string graph = Path.Combine(_dir, "graph.json");
        var cfg = new PluginConfig { Id = "p1", State = PluginEdits.StatePath("mic", "p1") };
        using var rack = new PluginRack(AppOptions.HostOptions(graph, () => []).Plugins);
        Assert.Equal(Path.Combine(_dir, "plugins", "mic-p1.state"), rack.StateFile(cfg));

        WpfThread.Run(() =>
        {
            using var store = new GraphStore(graph, Graph());
            var vm = new MainViewModel(Graph(), store, host: null, offlineDevices: []);
            Assert.Equal(rack.StateFile(cfg), Path.GetFullPath(vm.StateFile("mic", "p1")));
        });
    }

    [Fact]
    public async Task Picker_AddsASlot_ThePanelAndChipShowIt()
    {
        FakeBundle("Denoiser", "5A3B0000000000000000000000000001", "Restoration");
        FakeBundle("Verb", "5A3B0000000000000000000000000002", "Reverb");
        var catalog = new PluginCatalog(new PluginScanner(hostExe: @"Z:\no-host.exe"), Path.Combine(_dir, "plugins.json"));
        await catalog.RefreshAsync([Path.Combine(_dir, "vst")]);

        WpfThread.Run(() =>
        {
            var store = new GraphStore(Path.Combine(_dir, "graph.json"), Graph());
            using var _ = store;
            var vm = new MainViewModel(Graph(), store, host: null, offlineDevices: [])
            {
                PluginCatalogOverride = catalog,
                PluginFoldersOverride = () => [Path.Combine(_dir, "vst")],
            };
            var mic = vm.FindNode("mic")!;
            Assert.True(mic.ShowFx);

            vm.OpenPluginPicker(new PluginTarget(mic, PluginGroup.Pre));
            var picker = vm.Palette!;
            Assert.Equal(PaletteMode.Plugins, picker.Mode);
            Assert.Equal(["RESTORATION", "REVERB"], picker.Rows.Where(r => r.IsHeader).Select(r => r.Header));
            picker.Query = "denoi";
            var row = Assert.Single(picker.Rows, r => !r.IsHeader);
            Assert.Contains("Example Audio", row.Subtitle);
            picker.Commit();

            Assert.Null(vm.Palette);
            var slot = Assert.Single(GraphEdits.Find(vm.Graph, "mic")!.Plugins!);
            Assert.Equal(("p1", "Denoiser", "plugins/mic-p1.state"), (slot.Id, slot.Name, slot.State));
            Assert.Equal("5A3B0000000000000000000000000001", slot.ClassId);
            Assert.Equal("FX 1", mic.FxText);
            Assert.False(mic.ShowFx);
            var row1 = Assert.Single(vm.Processing!.PreSlots);
            Assert.Empty(vm.Processing.PostSlots);

            row1.Enabled = false; // переключатель — правка схемы через историю
            Assert.False(GraphEdits.Find(vm.Graph, "mic")!.Plugins![0].Enabled);
            Assert.Equal("FX 0", mic.FxText);
            vm.UndoCommand.Execute(null);
            Assert.True(row1.Enabled);

            vm.MovePlugin(mic, "p1", PluginGroup.Post, int.MaxValue);
            Assert.Empty(vm.Processing.PreSlots);
            Assert.Same(row1, Assert.Single(vm.Processing.PostSlots)); // та же модель слота: подписки на окно не теряются
            vm.RemovePlugin(mic, "p1");
            Assert.True(mic.ShowFx);
            vm.Dispose();
        });
    }

    /// <summary>Слот удалили, пока плагин грелся после загрузки: раньше прогрев читал закрытую общую память (AccessViolation).</summary>
    [Fact]
    public async Task Host_DisposedDuringWarmUp_DoesNotTouchFreedMemory()
    {
        if (!TestHost.Available()) return;
        var host = PluginHost.Start(new PluginHostOptions { HostExe = TestHost.Path });
        var load = host.LoadAsync(PluginLoad.Test("sleep", new BuiltinOptions { SleepMs = 200 })); // 8 блоков прогрева по 200 мс
        Assert.True(await Until(() => host.Info is not null, 10));
        host.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => load);
    }

    /// <summary>
    /// Хост не читает канал. Отправка не ждёт записи: раньше синхронная запись ждала завершения на потоке пула, пинги
    /// из пула вставали за ней, пул голодал, и запись не могла завершиться (на CI — несработавший таймер автосохранения,
    /// «control pipe is closed», зависший прогон).
    /// </summary>
    [Fact]
    public async Task ControlPipe_SendNeverWaitsForTheHost()
    {
        string name = "Mixer.Test." + Guid.NewGuid().ToString("N");
        var pipe = new ControlPipe(name);
        var connected = pipe.WaitForConnectionAsync(CancellationToken.None);
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut);
        client.Connect(5000);
        await connected;

        var sw = Stopwatch.StartNew();
        Assert.True(pipe.Send(new JsonObject { ["blob"] = new string('x', 4 << 20) })); // больше буфера канала: запись встанет
        for (int i = 0; i < 100; i++) Assert.True(pipe.Send(new JsonObject { ["type"] = "ping" }));
        Assert.True(sw.Elapsed.TotalSeconds < 1, $"{sw.Elapsed.TotalSeconds:0.00} s");

        pipe.Dispose(); // как после завершения зависшего хоста: застрявшая запись заканчивается без исключений
        Assert.False(pipe.Send(new JsonObject { ["type"] = "ping" }));

        // порядок и целостность: что ставили в очередь, приходит кадрами по порядку
        string name2 = "Mixer.Test." + Guid.NewGuid().ToString("N");
        using var pipe2 = new ControlPipe(name2);
        var c2 = pipe2.WaitForConnectionAsync(CancellationToken.None);
        using var reader = new System.IO.Pipes.NamedPipeClientStream(".", name2, System.IO.Pipes.PipeDirection.InOut);
        reader.Connect(5000);
        await c2;
        for (int i = 0; i < 50; i++) Assert.True(pipe2.Send(new JsonObject { ["n"] = i }));
        var header = new byte[4];
        for (int i = 0; i < 50; i++)
        {
            await reader.ReadExactlyAsync(header);
            var body = new byte[BitConverter.ToInt32(header)];
            await reader.ReadExactlyAsync(body);
            Assert.Equal(i, (int)JsonNode.Parse(body)!["n"]!);
        }
    }

    // ── файлы состояния и пресеты (настоящий хост со встроенными плагинами) ──

    private static PluginConfig Gain(string state) =>
        new() { Id = "p1", Format = "test", Path = "gain", Name = "gain", State = state };

    private PluginRack Rack(PluginRackOptions? o = null) => new((o ?? new PluginRackOptions()) with
    {
        HostExe = TestHost.Path,
        StateFolder = _dir,
        Resolve = p => PluginLoad.Test(p.Path!),
    });

    /// <summary>Состояние встроенного gain: компонент (значение) и контроллер «gain-ui:N», N — сколько раз его сохраняли.</summary>
    private static (byte[] Component, int Saves) Parts(byte[] blob)
    {
        Assert.True(PluginState.TryUnpack(blob, out var component, out var controller));
        return (component, int.Parse(System.Text.Encoding.ASCII.GetString(controller)["gain-ui:".Length..]));
    }

    private static GraphConfig One(PluginConfig p) => new() { Nodes = [new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, Plugins = [p] }] };

    private static async Task<bool> Until(Func<bool> ok, double seconds, PluginRack? tick = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (ok()) return true;
            tick?.Tick();
            await Task.Delay(50);
        }
        return ok();
    }

    /// <summary>Критерий: после перезапуска микшера настройки из окна плагина на месте — состояние совпадает побитово.</summary>
    [Fact]
    public async Task State_SurvivesARestart_BitForBit()
    {
        if (!TestHost.Available()) return;
        var cfg = Gain("plugins/mic-p1.state");
        string file = Path.Combine(_dir, "plugins", "mic-p1.state");
        byte[] before;
        using (var rack = Rack(new PluginRackOptions { StateDelay = TimeSpan.FromMilliseconds(200) }))
        {
            rack.Sync(One(cfg));
            Assert.True(await Until(() => rack.Status.Single().State == PluginSlotState.Running, 10));
            rack.HostOf("mic", "p1")!.SendDebug(new JsonObject { ["op"] = "edit", ["id"] = 0, ["value"] = 0.25 });
            Assert.True(await Until(() => File.Exists(file), 5, rack)); // через 2 с (здесь 0,2 с) после dirty — в файл
            Assert.Equal(rack.StateOf("mic", "p1"), File.ReadAllBytes(file));
        }
        before = File.ReadAllBytes(file); // с чем перезапустится (при выходе записан ещё раз)
        using (var rack = Rack())
        {
            rack.Sync(One(cfg));
            Assert.True(await Until(() => rack.Status.Single().State == PluginSlotState.Running, 10));
            var (component, saves) = Parts(await rack.HostOf("mic", "p1")!.GetStateAsync());
            Assert.Equal(Parts(before).Component, component); // побитово
            Assert.Equal(Parts(before).Saves + 1, saves);     // и контроллер получил своё состояние
        }
    }

    [Fact]
    public async Task State_IsWrittenOnExit_AndPresetsLoadIntoTheSlot()
    {
        if (!TestHost.Available()) return;
        var cfg = Gain("plugins/mic-p1.state");
        string file = Path.Combine(_dir, "plugins", "mic-p1.state");
        byte[] edited, original;
        using (var rack = Rack())
        {
            rack.Sync(One(cfg));
            Assert.True(await Until(() => rack.Status.Single().State == PluginSlotState.Running, 10));
            var host = rack.HostOf("mic", "p1")!;
            original = (await rack.SaveStateNowAsync("mic", "p1"))!; // как «Сохранить пресет»
            host.SendDebug(new JsonObject { ["op"] = "edit", ["id"] = 0, ["value"] = 0.25 });
            await Task.Delay(300);
            edited = await host.GetStateAsync();
            Assert.NotEqual(original, edited);
        } // выход: dirty ещё не отработал (StateDelay 2 с), состояние забирается при остановке
        Assert.Equal(Parts(edited).Component, Parts(File.ReadAllBytes(file)).Component);

        using (var rack = Rack())
        {
            rack.Sync(One(cfg));
            Assert.True(await Until(() => rack.Status.Single().State == PluginSlotState.Running, 10));
            Assert.True(await rack.SetStateAsync("mic", "p1", original)); // «Загрузить пресет»
            Assert.Equal(Parts(original).Component, Parts(await rack.HostOf("mic", "p1")!.GetStateAsync()).Component);
            Assert.Equal(original, File.ReadAllBytes(file));
        }
    }
}
