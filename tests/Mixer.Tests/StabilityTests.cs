using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;
using Mixer.Core.Host;
using Mixer.Core.Sys;

namespace Mixer.Tests;

/// <summary>Этап 4: устройства по умолчанию, горячая замена, сон, резервные копии, журнал.</summary>
public class StabilityTests
{
    private static readonly EndpointInfo Headphones = new("{0.0.0.00000000}.{hp}", "Наушники (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo Headset = new("{0.0.0.00000000}.{hs}", "Гарнитура (USB Audio)", EDataFlow.Render, false) { IsDefaultComms = true };
    private static readonly EndpointInfo CableIn = new("{0.0.0.00000000}.{ci}", "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Микрофон (fifine)", EDataFlow.Capture, true) { IsDefaultComms = true };
    private static readonly List<EndpointInfo> Devices = [Headphones, Headset, CableIn, Mic];

    private static NodeConfig Default(string id, NodeKind kind, DefaultRole role = DefaultRole.Console) =>
        new() { Id = id, Kind = kind, Name = id, DefaultRole = role, X = kind == NodeKind.DeviceOutput ? GraphLayout.OutputX : GraphLayout.SourceX };

    private static NodeConfig Fixed(string id, NodeKind kind, EndpointInfo ep) =>
        new() { Id = id, Kind = kind, Name = id, EndpointId = ep.Id, EndpointName = ep.Name, X = kind == NodeKind.DeviceOutput ? GraphLayout.OutputX : GraphLayout.SourceX };

    /// <summary>Тот же список, но устройством по умолчанию стало другое.</summary>
    private static List<EndpointInfo> WithDefault(EndpointInfo render) =>
        Devices.Select(d => d.Flow == EDataFlow.Render ? d with { IsDefault = d.Id == render.Id } : d).ToList();

    // ── устройство по умолчанию ──

    [Fact]
    public void Default_node_resolves_by_role_and_flow()
    {
        Assert.Equal(Headphones, GraphBuilder.Resolve(Default("o", NodeKind.DeviceOutput), Devices));
        Assert.Equal(Headset, GraphBuilder.Resolve(Default("o", NodeKind.DeviceOutput, DefaultRole.Communications), Devices));
        Assert.Equal(Headphones, GraphBuilder.Resolve(Default("lb", NodeKind.Loopback), Devices));
        Assert.Equal(Mic, GraphBuilder.Resolve(Default("m", NodeKind.DeviceInput), Devices));
        Assert.Equal(Mic, GraphBuilder.Resolve(Default("m", NodeKind.DeviceInput, DefaultRole.Communications), Devices));
        // ID и имя у такого узла не используются, даже если остались
        var stale = Default("o", NodeKind.DeviceOutput) with { EndpointId = CableIn.Id, EndpointName = CableIn.Name };
        Assert.Equal(Headphones, GraphBuilder.Resolve(stale, Devices));
    }

    [Fact]
    public void Default_output_follows_windows_and_waits_without_device()
    {
        var g = new GraphConfig
        {
            Nodes = [Fixed("mic", NodeKind.DeviceInput, Mic), Default("out", NodeKind.DeviceOutput)],
            Wires = [new WireConfig { From = "mic", To = "out" }],
        };
        var a = GraphBuilder.Build(g, Devices);
        Assert.Equal(Headphones.Id, a.Graph.Outputs.Single().EndpointId);
        var b = GraphBuilder.Build(g, WithDefault(CableIn));
        Assert.Equal(CableIn.Id, b.Graph.Outputs.Single().EndpointId);
        Assert.Single(b.Graph.Wires);

        var none = GraphBuilder.Build(g, Devices.Where(d => d.Flow == EDataFlow.Capture).ToList());
        Assert.Empty(none.Graph.Outputs);
        var wait = Assert.Single(none.Issues, i => i.Subject == "out");
        Assert.Equal(IssueKind.WaitingDevice, wait.Kind);
        Assert.Contains("устройства воспроизведения по умолчанию", wait.Message);
        Assert.Single(g.Wires); // провод остаётся в схеме
    }

    [Fact]
    public void Default_role_change_is_structure_and_survives_json()
    {
        var a = new GraphConfig { Nodes = [Default("out", NodeKind.DeviceOutput)] };
        var b = GraphEdits.UpdateNode(a, "out", n => n with { DefaultRole = DefaultRole.Communications });
        Assert.Equal(GraphChangeKind.Structure, GraphChange.Classify(a, b));

        string json = GraphSerializer.Serialize(b);
        Assert.Contains("\"defaultRole\": \"communications\"", json);
        Assert.DoesNotContain("endpointId", json);
        Assert.Equal(DefaultRole.Communications, GraphSerializer.Deserialize(json).Nodes[0].DefaultRole);
        Assert.Null(GraphSerializer.Deserialize(GraphSerializer.Serialize(new GraphConfig { Nodes = [Fixed("hp", NodeKind.DeviceOutput, Headphones)] })).Nodes[0].DefaultRole);
    }

    [Fact]
    public void Loopback_of_default_into_default_output_is_always_a_loop()
    {
        var g = new GraphConfig
        {
            Nodes =
            [
                Default("lb", NodeKind.Loopback), Default("lbc", NodeKind.Loopback, DefaultRole.Communications),
                Default("out", NodeKind.DeviceOutput), Fixed("hp", NodeKind.DeviceOutput, Headphones), Fixed("cable", NodeKind.DeviceOutput, CableIn),
            ],
        };
        // та же роль — петля при любом устройстве, даже когда устройств нет вовсе
        Assert.Equal(ConnectVerdict.Feedback, ConnectionRules.Check(g, "lb", "out", Devices).Verdict);
        Assert.Equal(ConnectVerdict.Feedback, ConnectionRules.Check(g, "lb", "out", []).Verdict);
        // явный выход, который сейчас по умолчанию, — петля; другой — нет
        Assert.Equal(ConnectVerdict.Feedback, ConnectionRules.Check(g, "lb", "hp", Devices).Verdict);
        Assert.Equal(ConnectVerdict.Ok, ConnectionRules.Check(g, "lb", "cable", Devices).Verdict);
        // связь — другое устройство, петли нет
        Assert.Equal(ConnectVerdict.Ok, ConnectionRules.Check(g, "lbc", "out", Devices).Verdict);

        // провод, ставший петлёй после смены устройства по умолчанию, в движок не идёт
        var wired = g with { Wires = [new WireConfig { From = "lb", To = "cable" }] };
        Assert.Single(GraphBuilder.Build(wired, Devices).Graph.Wires);
        var after = GraphBuilder.Build(wired, WithDefault(CableIn));
        Assert.Empty(after.Graph.Wires);
        Assert.Contains(after.Issues, i => i.Kind == IssueKind.Feedback);
    }

    // ── горячая замена ──

    [Fact]
    public void Debounce_waits_for_quiet_but_not_forever()
    {
        var d = new Debounce(0.5, 2.0);
        Assert.Null(d.Due);
        Assert.False(d.TryFire(100));
        d.Hit(0);
        d.Hit(0.3);
        Assert.Equal(0.8, d.Due!.Value, 6);
        Assert.False(d.TryFire(0.79));
        Assert.True(d.TryFire(0.8));
        Assert.False(d.Pending);

        // поток событий каждые 0,3 с: срабатывает не позже 2 с после первого
        double t = 10;
        d.Hit(t);
        bool fired = false;
        while (t < 15 && !fired)
        {
            t += 0.3;
            fired = d.TryFire(t);
            if (!fired) d.Hit(t);
        }
        Assert.True(fired);
        Assert.InRange(t, 12.0, 12.3);
    }

    [Fact]
    public void Device_diff_describes_plug_unplug_rename_and_default()
    {
        Assert.Null(DeviceDiff.Describe(Devices, Devices.Select(d => d with { }).ToList()));

        var unplugged = Devices.Where(d => d != Headset).ToList();
        string? gone = DeviceDiff.Describe(Devices, unplugged);
        // журнал пишется по-английски на любом языке интерфейса
        Assert.Contains("disconnected \"Гарнитура (USB Audio)\"", gone);
        Assert.Contains("default communications playback device: none", gone);
        Assert.Contains("connected \"Гарнитура (USB Audio)\"", DeviceDiff.Describe(unplugged, Devices));

        var renamed = Devices.Select(d => d == CableIn ? d with { Name = "Стрим (VB-Audio Virtual Cable)" } : d).ToList();
        Assert.Contains("\"CABLE Input (VB-Audio Virtual Cable)\" is now called \"Стрим (VB-Audio Virtual Cable)\"", DeviceDiff.Describe(Devices, renamed));

        string? def = DeviceDiff.Describe(Devices, WithDefault(CableIn));
        Assert.Equal("default playback device: \"CABLE Input (VB-Audio Virtual Cable)\"", def);
        var touched = DeviceDiff.Touched(Devices, WithDefault(CableIn));
        Assert.Equal(new[] { CableIn.Id, Headphones.Id }.OrderBy(x => x), touched.OrderBy(x => x));
        Assert.Equal(new[] { Headset.Id }, DeviceDiff.Touched(Devices, unplugged));
    }

    // ── сон ──

    [Fact]
    public void Sleep_detector_sees_only_time_spent_asleep()
    {
        double withSleep = 1000, withoutSleep = 400;
        var d = new SleepDetector(() => withSleep, () => withoutSleep);
        Assert.Equal(0, d.SleptSeconds());
        withSleep += 60;
        withoutSleep += 60; // минута работы
        Assert.Equal(0, d.SleptSeconds());
        withSleep += 3600.2; // час сна: часы без сна стоят
        withoutSleep += 0.2;
        Assert.Equal(3600, d.SleptSeconds(), 6);
        Assert.Equal(0, d.SleptSeconds());
        withSleep += 0.5; // дрожание часов — не сон
        Assert.Equal(0, d.SleptSeconds());
    }

    // ── резервные копии ──

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "graph.json");

    private static GraphConfig Named(string name) => new() { Nodes = [Fixed("hp", NodeKind.DeviceOutput, Headphones) with { Name = name }] };

    [Fact]
    public void Store_keeps_five_backups_at_most_every_ten_minutes()
    {
        string path = TempPath();
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        using var store = new GraphStore(path, new GraphConfig(), () => now);
        store.Changed(Named("v0"));
        store.Flush();
        Assert.Empty(GraphStore.Backups(path)); // копировать было нечего

        now = now.AddSeconds(5);
        store.Changed(Named("v1"));
        store.Flush();
        var first = Assert.Single(GraphStore.Backups(path)); // первая запись за запуск
        Assert.Equal("v0", GraphSerializer.Load(first).Nodes[0].Name);
        Assert.EndsWith("graph.json.bak-20261110-120005", first);

        now = now.AddMinutes(3);
        store.Changed(Named("v2"));
        store.Flush();
        Assert.Single(GraphStore.Backups(path)); // меньше 10 минут — без копии

        for (int i = 3; i < 10; i++)
        {
            now = now.AddMinutes(11);
            store.Changed(Named("v" + i));
            store.Flush();
        }
        var backups = GraphStore.Backups(path);
        Assert.Equal(GraphStore.BackupCount, backups.Count);
        Assert.Equal("v8", GraphSerializer.Load(backups[0]).Nodes[0].Name); // самая свежая — состояние до последней записи
        Assert.Equal("v9", GraphSerializer.Load(path).Nodes[0].Name);
    }

    [Fact]
    public void Broken_graph_is_restored_from_newest_readable_backup()
    {
        string path = TempPath();
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, "{ \"nodes\": [");
        GraphSerializer.Save(path + ".bak-20261110-100000", Named("старая"));
        GraphSerializer.Save(path + ".bak-20261110-110000", Named("свежая"));
        File.WriteAllText(path + ".bak-20261110-120000", "мусор"); // самая новая копия тоже битая

        var g = GraphStore.LoadOrEmpty(path, out string? error);
        Assert.Equal("свежая", g.Nodes[0].Name);
        Assert.Contains("Восстановлена резервная копия", error);
        Assert.Single(Directory.GetFiles(dir, "graph.json.broken-*"));

        string bare = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        File.WriteAllText(bare, "{");
        Assert.Empty(GraphStore.LoadOrEmpty(bare, out string? none).Nodes);
        Assert.Contains("резервных копий нет", none);
    }

    // ── журнал ──

    [Fact]
    public void Log_writes_lines_rotates_by_size_and_day_and_keeps_ten_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "logs");
        var now = new DateTime(2026, 11, 10, 9, 0, 0);
        using (var log = new FileLog(dir, () => now, maxFileBytes: 400))
        {
            for (int i = 0; i < 20; i++) log.Write("host", $"строка {i:00} — устройство «Наушники» подключено");
        }
        var files = FileLog.Files(dir);
        Assert.True(files.Count > 1, "по размеру журнал делится на части");
        Assert.All(files, f => Assert.StartsWith("mixer-20261110", Path.GetFileName(f)));
        var lines = files.Reverse().SelectMany(File.ReadAllLines).ToList();
        Assert.Equal(20, lines.Count);
        Assert.Equal("2026-11-10 09:00:00.000 [host] строка 00 — устройство «Наушники» подключено", lines[0]);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 400));

        for (int day = 11; day <= 25; day++)
        {
            now = new DateTime(2026, 11, day, 9, 0, 0);
            using var log = new FileLog(dir, () => now);
            log.Write("app", "запуск");
        }
        files = FileLog.Files(dir);
        Assert.Equal(FileLog.KeepFiles, files.Count);
        Assert.Equal("mixer-20261125.log", Path.GetFileName(files[0]));
    }

    // ── интерфейс ──

    [Fact]
    public void Palette_offers_default_devices_first()
    {
        WpfThread.Run(() =>
        {
            string path = TempPath();
            var g = new GraphConfig();
            using var store = new GraphStore(path, g);
            var vm = new MainViewModel(g, store, host: null, offlineDevices: Devices);
            var catalog = new SourceCatalog(Devices, [], null, VoicemeeterRunning: false);

            var src = new PaletteViewModel(vm, PaletteMode.Sources, null, Task.FromResult(catalog));
            int inputs = src.Rows.ToList().FindIndex(r => r.Header == "ВХОДЫ");
            var mic = src.Rows[inputs + 1];
            Assert.Equal("Микрофон по умолчанию", mic.Title);
            Assert.Contains("сейчас «Микрофон»", mic.Subtitle);
            Assert.Equal(DefaultRole.Console, mic.Template!.DefaultRole);
            Assert.Equal("Микрофон", mic.Template.Name); // в карточке короче: роль видна в подписи
            // микрофон для связи — то же устройство: второй строки нет
            Assert.DoesNotContain(src.Rows, r => r.Title == "Микрофон для связи по умолчанию");
            int lb = src.Rows.ToList().FindIndex(r => r.Header == "ЗВУК ВЫХОДОВ (LOOPBACK)");
            Assert.Equal(NodeKind.Loopback, src.Rows[lb + 1].Template!.Kind);
            Assert.Equal(DefaultRole.Console, src.Rows[lb + 1].Template!.DefaultRole);

            var outs = new PaletteViewModel(vm, PaletteMode.Outputs, null, Task.FromResult(catalog));
            Assert.Equal("Выход по умолчанию", outs.Rows[1].Title);
            Assert.Equal("Связь по умолчанию", outs.Rows[2].Title); // гарнитура для связи — другое устройство
            Assert.Contains("Гарнитура", outs.Rows[2].Subtitle);
            outs.Query = "умолчанию"; // обе строки «по умолчанию» и сами наушники с подписью «устройство по умолчанию»
            Assert.Equal(4, outs.Rows.Count);

            outs.Commit(outs.Rows[1]);
            var added = vm.Nodes.Single();
            Assert.True(added.FollowsDefault);
            Assert.Equal("Выход по умолчанию", added.Name);
            Assert.Null(added.Config.EndpointId);
            vm.Dispose();
        });
    }

    [Fact]
    public void Switching_between_default_and_fixed_device_keeps_user_names()
    {
        WpfThread.Run(() =>
        {
            string path = TempPath();
            var g = new GraphConfig { Nodes = [Fixed("hp", NodeKind.DeviceOutput, Headphones) with { Name = "Наушники" }, Fixed("mic", NodeKind.DeviceInput, Mic) with { Name = "Голос" }] };
            using var store = new GraphStore(path, g);
            var vm = new MainViewModel(g, store, host: null, offlineDevices: Devices);
            var hp = vm.FindNode("hp")!;

            vm.FollowDefault(hp, DefaultRole.Communications);
            Assert.Equal(DefaultRole.Communications, hp.Config.DefaultRole);
            Assert.Null(hp.Config.EndpointId);
            Assert.Equal("Связь по умолчанию", hp.Name); // имя было по устройству — сменилось
            vm.ChangeDevice(hp, CableIn);
            Assert.Null(hp.Config.DefaultRole);
            Assert.Equal(CableIn.Id, hp.Config.EndpointId);
            Assert.Equal("CABLE Input", hp.Name);

            var mic = vm.FindNode("mic")!;
            vm.FollowDefault(mic, DefaultRole.Console);
            Assert.Equal("Голос", mic.Name); // своё имя пользователя не трогаем
            vm.UndoCommand.Execute(null);
            Assert.Null(mic.Config.DefaultRole);
            Assert.Equal(Mic.Id, mic.Config.EndpointId);
            vm.Dispose();
        });
    }
}
