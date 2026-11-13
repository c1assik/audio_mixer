using System.Diagnostics;
using Mixer.Audio.Dsp;
using Mixer.Core.Config;
using Mixer.Core.Plugins;
using Mixer.Plugins.Host;
using Xunit.Abstractions;
using static Mixer.Tests.PluginEngineTests;

namespace Mixer.Tests;

/// <summary>
/// VST, этап 5: стресс-тесты и тесты падений управляющего слоя со встроенными плагинами хоста. Полная схема
/// (16 слотов) под звуком и массовое падение, частые правки слотов, падение одного слота среди работающих, падение во
/// время смены частоты, модуль, который падает при загрузке. Без C++-сборки проходят пустыми.
/// </summary>
[Collection(nameof(SerialTiming))] // много процессов хостов: не мешать тестам со временем
public class PluginStressTests(ITestOutputHelper output)
{
    private static PluginConfig Slot(string id, string kind, PluginGroup group = PluginGroup.Pre, bool enabled = true, bool even = false) =>
        new() { Id = id, Format = "test", Path = kind, Name = kind, Group = group, Enabled = enabled, EvenBlocks = even };

    private static GraphConfig Graph(params (string Node, PluginConfig[] Slots)[] nodes) => new()
    {
        Nodes = [.. nodes.Select(n => new NodeConfig { Id = n.Node, Kind = NodeKind.DeviceInput, Name = n.Node, Plugins = [.. n.Slots] })],
    };

    private PluginRack Rack(PluginRackOptions? o = null, Dictionary<string, BuiltinOptions>? options = null) =>
        new((o ?? new PluginRackOptions()) with
        {
            HostExe = TestHost.Path,
            Resolve = o?.Resolve ?? (p => PluginLoad.Test(p.Path!, options?.GetValueOrDefault(p.Path!))),
        }, s => { try { output.WriteLine(s); } catch (InvalidOperationException) { } });

    private static PluginSlotStatus StatusOf(PluginRack rack, string slot) => rack.Status.Single(s => s.SlotId == slot);

    /// <summary>Хосты, которые сейчас есть у слотов.</summary>
    private static IEnumerable<int> Pids(PluginRack rack) =>
        rack.Status.Select(s => rack.HostOf(s.NodeId, s.SlotId)).Where(h => h is not null).Select(h => h!.ProcessId);

    private static async Task<bool> AllExited(IEnumerable<int> pids, double seconds) => await Until(() => pids.All(Exited), seconds);

    private static bool Exited(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    /// <summary>
    /// Потолок схемы: 16 слотов на 4 узлах работают вместе (17-й — «лимит»), почти без просрочек. Все 16 хостов сняты
    /// разом — каждый слот уходит в сухой и возвращается; после выхода не остаётся ни одного процесса.
    /// </summary>
    [Fact]
    public async Task SixteenSlots_RunTogether_AndAllComeBackAfterAMassKill()
    {
        if (!TestHost.Available()) return;
        string[] nodes = ["n0", "n1", "n2", "n3"];
        var graph = Graph([.. nodes.Select(n => (n, new[]
        {
            Slot(n + "a", "gain"), Slot(n + "b", "copy"),
            Slot(n + "c", "gain", PluginGroup.Post), Slot(n + "d", "copy", PluginGroup.Post),
        })), ("n4", [Slot("extra", "gain")])]);
        var rack = Rack();
        var pids = new HashSet<int>();
        try
        {
            rack.Sync(graph);
            Assert.Equal(PluginFault.Limit, StatusOf(rack, "extra").Fault);
            var sixteen = rack.Status.Where(s => s.SlotId != "extra").Select(s => s.SlotId).ToArray();
            Assert.Equal(16, sixteen.Length);
            Assert.True(await Until(() => sixteen.All(id => StatusOf(rack, id).State == PluginSlotState.Running), 30));
            pids.UnionWith(Pids(rack));
            Assert.Equal(16, pids.Count);

            var pumps = nodes.Select(n => new Pump(rack, n)).ToArray();
            try
            {
                foreach (var n in nodes) foreach (var s in rack.Slots(n)) s.TakeMisses();
                await Task.Delay(5000);
                int misses = nodes.SelectMany(rack.Slots).Sum(s => s.TakeMisses());
                output.WriteLine($"16 слотов, 5 с: просрочек {misses} из ~{16 * 500} блоков");
                Assert.True(misses <= 16 * 500 / 100, $"{misses} missed blocks"); // не больше 1%
                var down = rack.Status.Where(s => s.SlotId != "extra" && s.State != PluginSlotState.Running).ToList();
                Assert.True(down.Count == 0, string.Join("; ", down.Select(s => $"{s.SlotId}: {s.State} {s.Fault} {s.Detail}")));
                Assert.Equal(pids.Order(), Pids(rack).Order()); // ни один хост не перезапускался сам

                var sw = Stopwatch.StartNew();
                foreach (int pid in pids) Process.GetProcessById(pid).Kill();
                Assert.True(await Until(() => sixteen.All(id => StatusOf(rack, id).State != PluginSlotState.Running), 3));
                Assert.True(await Until(() => sixteen.All(id => StatusOf(rack, id).State == PluginSlotState.Running), 15));
                output.WriteLine($"все 16 снова работают через {sw.Elapsed.TotalSeconds:F2} с");
                Assert.All(sixteen, id => Assert.Equal(PluginFault.None, StatusOf(rack, id).Fault));
                pids.UnionWith(Pids(rack));
                Assert.Equal(32, pids.Count);
                Assert.All(pumps, p => Assert.True(p.AllFinite));
            }
            finally
            {
                foreach (var p in pumps) p.Dispose();
            }
        }
        finally
        {
            rack.Dispose();
        }
        Assert.True(await AllExited(pids, 5), "plug-in hosts left after the rack was disposed");
    }

    /// <summary>
    /// Частые правки под звуком: слоты появляются, исчезают, меняют группу, включение и «ровные блоки» каждые
    /// 20–150 мс, в том числе пока хост ещё загружается. Ничего не падает, звук остаётся числом, процессов не остаётся.
    /// </summary>
    [Fact]
    public async Task SlotChurn_UnderAudio_LeavesNoHostsBehind()
    {
        if (!TestHost.Available()) return;
        var rnd = new Random(5);
        string[] kinds = ["gain", "copy", "latency"];
        var rack = Rack();
        var pids = new HashSet<int>();
        var pumps = new[] { new Pump(rack, "a"), new Pump(rack, "b") };
        int edits = 0;
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(8))
            {
                PluginConfig[] Pick(string node) => [.. Enumerable.Range(0, rnd.Next(0, 5)).Select(i => Slot(
                    $"{node}{rnd.Next(4)}", kinds[rnd.Next(kinds.Length)], i % 2 == 0 ? PluginGroup.Pre : PluginGroup.Post,
                    enabled: rnd.Next(4) > 0, even: rnd.Next(5) == 0)).DistinctBy(p => p.Id)];
                rack.Sync(Graph(("a", Pick("a")), ("b", Pick("b"))));
                rack.Tick();
                edits++;
                pids.UnionWith(Pids(rack));
                await Task.Delay(rnd.Next(20, 150));
            }
            output.WriteLine($"{edits} правок, {pids.Count} хостов");

            // после правок схема ещё работает: последняя раскладка загружается целиком
            rack.Sync(Graph(("a", [Slot("a0", "gain"), Slot("a1", "latency", PluginGroup.Post)]), ("b", [Slot("b0", "copy")])));
            Assert.True(await Until(() => rack.Status.All(s => s.State == PluginSlotState.Running), 15, rack));
            pids.UnionWith(Pids(rack));
            Assert.All(pumps, p => Assert.True(p.AllFinite));
        }
        finally
        {
            foreach (var p in pumps) p.Dispose();
            rack.Dispose();
        }
        Assert.True(pids.Count > 20, $"only {pids.Count} hosts started");
        Assert.True(await AllExited(pids, 5), "plug-in hosts left after the churn");
    }

    /// <summary>Слот, который падает снова и снова, доходит до «Сбоя»; соседи на его узле и на другом узле всё это время работают.</summary>
    [Fact]
    public async Task CrashingSlot_DoesNotDisturbTheOthers()
    {
        if (!TestHost.Available()) return;
        using var rack = Rack(new PluginRackOptions { RestartDelay = TimeSpan.FromMilliseconds(300) },
            new() { ["crash"] = new BuiltinOptions { After = 50 } });
        rack.Sync(Graph(("a", [Slot("a1", "gain"), Slot("bad", "crash"), Slot("a2", "copy", PluginGroup.Post)]),
                        ("b", [Slot("b1", "gain"), Slot("b2", "latency")])));
        string[] good = ["a1", "a2", "b1", "b2"];
        Assert.True(await Until(() => rack.Status.All(s => s.State == PluginSlotState.Running), 15));
        using var pa = new Pump(rack, "a");
        using var pb = new Pump(rack, "b");
        foreach (var s in rack.Slots("a").Concat(rack.Slots("b"))) s.TakeMisses();

        bool othersStayed = true;
        Assert.True(await Until(() =>
        {
            othersStayed &= good.All(id => StatusOf(rack, id).State == PluginSlotState.Running);
            return StatusOf(rack, "bad").Fault == PluginFault.CrashedRepeatedly;
        }, 15));
        Assert.True(othersStayed);
        int misses = rack.Slots("a").Concat(rack.Slots("b")).Where(s => s.Id != "bad").Sum(s => s.TakeMisses());
        output.WriteLine($"соседи: просрочек {misses}");
        Assert.True(misses <= 5, $"{misses} missed blocks next to the crashing slot");
        Assert.True(pa.AllFinite && pb.AllFinite);
    }

    /// <summary>
    /// Плагин падает на смене частоты (так делает Elgato EQ, см. таблицу совместимости): слот возвращается уже на
    /// частоте узла. Хост снят сразу после запроса setRate — до, во время или после него, итог должен быть один.
    /// </summary>
    [Fact]
    public async Task CrashOnARateChange_ComesBackAtTheNodeRate()
    {
        if (!TestHost.Available()) return;
        using var rack = Rack(new PluginRackOptions { RestartDelay = TimeSpan.FromMilliseconds(200) });
        rack.Sync(Graph(("a", [Slot("p1", "gain")])));
        Assert.True(await Until(() => StatusOf(rack, "p1").State == PluginSlotState.Running, 10));
        Assert.Equal(48000, StatusOf(rack, "p1").Rate);
        var host = rack.HostOf("a", "p1")!;

        using var pump = new Pump(rack, "a", 44100);
        Assert.True(await Until(() => rack.Slots("a")[0].SeenRate == 44100, 2));
        rack.Tick(); // запрос setRate
        Process.GetProcessById(host.ProcessId).Kill();

        Assert.True(await Until(() => rack.HostOf("a", "p1") is { } h && h != host && StatusOf(rack, "p1").State == PluginSlotState.Running, 10, rack));
        Assert.Equal(44100, StatusOf(rack, "p1").Rate);
        Assert.True(pump.AllFinite);
    }

    /// <summary>Модуль, который падает при загрузке: три попытки, потом «Сбой» без новых хостов, пока не нажать «Перезапустить».</summary>
    [Fact]
    public async Task ModuleCrashingOnLoad_StopsAfterThreeAttempts()
    {
        if (!TestHost.Available()) return;
        string crash = Path.Combine(TestHost.RepoRoot, "src", "Mixer.PluginHost", "build", "test-modules", "crash.vst3");
        if (!File.Exists(crash)) return;
        using var rack = Rack(new PluginRackOptions
        {
            RestartDelay = TimeSpan.FromMilliseconds(100),
            Resolve = _ => PluginLoad.Vst3(crash),
        });
        rack.Sync(Graph(("a", [new PluginConfig { Id = "p1", Format = "vst3", Path = crash, Name = "crash" }])));
        Assert.True(await Until(() => StatusOf(rack, "p1").Fault == PluginFault.CrashedRepeatedly, 15));
        output.WriteLine(StatusOf(rack, "p1").Detail);
        await Task.Delay(500);
        Assert.Null(rack.HostOf("a", "p1"));
        Assert.Equal(PluginFault.CrashedRepeatedly, StatusOf(rack, "p1").Fault);
    }
}
