using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixer.Plugins.Catalog;

namespace Mixer.Tests;

/// <summary>
/// Сканирование и каталог (VST, этап 2). Тесты без хоста — moduleinfo.json, предпроверки, подпись, кэш; с хостом
/// (C++-сборка) — скан AGain через процесс, падающий и зависающий модули, папка из 100 модулей.
/// </summary>
public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mixer-scan-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string TestModules => Path.Combine(TestHost.RepoRoot, "src", "Mixer.PluginHost", "build", "test-modules");

    // нативная x64-DLL с подписью Microsoft: всегда есть рядом с рантаймом
    private static string CoreClr => Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");

    private static string ModuleInfo(params (string Cid, string Category, string Sub)[] classes) =>
        "// moduleinfo.json — JSON5\n{ \"Name\": \"Fake\", \"Factory Info\": { \"Vendor\": \"Example Audio\" },\n\"Classes\": [" +
        string.Join(",", classes.Select(c =>
            $"{{ \"CID\": \"{c.Cid}\", \"Category\": \"{c.Category}\", \"Name\": \"Fake {c.Cid[..4]}\", \"Version\": \"1.0\", " +
            $"\"SDKVersion\": \"VST 3.7.8\", \"Sub Categories\": [{string.Join(",", c.Sub.Split('|').Select(s => $"\"{s}\""))}] }}")) +
        ",]\n}";

    /// <summary>Бандл, где DLL названа не как бандл (как у Elgato), с пустым «модулем»: его никто не загружает.</summary>
    private string FakeBundle(string folder, string name, string? moduleInfo)
    {
        string bundle = Path.Combine(folder, name + ".vst3");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-win"));
        File.WriteAllBytes(Path.Combine(bundle, "Contents", "x86_64-win", name + "_v1.0.vst3"), []);
        if (moduleInfo != null)
        {
            Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
            File.WriteAllText(PluginFiles.ModuleInfoPath(bundle), moduleInfo);
        }
        return bundle;
    }

    private static string Fx(int i) => $"{i:X8}000000000000000000000000";

    [Fact]
    public async Task ModuleInfo_IsReadWithoutLoading()
    {
        string bundle = FakeBundle(_dir, "Fake", ModuleInfo(
            (Fx(1), PluginClass.AudioModuleClass, "Fx|Restoration"),
            (Fx(2), PluginClass.AudioModuleClass, "Instrument|Synth"),
            (Fx(3), "Component Controller Class", "Fx")));
        var scanner = new PluginScanner(hostExe: @"Z:\no-host.exe");
        var catalog = new PluginCatalog(scanner, Path.Combine(_dir, "plugins.json"));

        await catalog.RefreshAsync([_dir]);

        Assert.Equal(0, scanner.ProcessCount);
        var m = Assert.Single(catalog.Modules);
        Assert.Equal(bundle, m.Path, ignoreCase: true);
        Assert.True(m.FromModuleInfo);
        Assert.Equal(2, m.Classes.Count); // контроллер не аудиомодуль
        var fx = Assert.Single(catalog.Effects);
        Assert.Equal("Example Audio", fx.Class.Vendor);
        Assert.Equal(["Restoration"], fx.Class.Categories);
        Assert.Same(fx.Class, catalog.Find(Fx(1).ToLowerInvariant())?.Class);
        var instrument = Assert.Single(catalog.NotSupported);
        Assert.Equal(ScanProblem.Instrument, instrument.Problem);
        Assert.Empty(catalog.NotLoaded);
    }

    [Fact]
    public async Task Precheck_FindsBits32_NoX64_AndSkipsOtherDlls()
    {
        string bits32 = Path.Combine(_dir, "Old.vst3");
        Directory.CreateDirectory(Path.Combine(bits32, "Contents", "x86-win"));
        string arm = Path.Combine(_dir, "Arm.vst3");
        Directory.CreateDirectory(Path.Combine(arm, "Contents", "arm64-win"));
        // однофайловый .vst3 с машиной I386 в заголовке PE
        string single32 = Path.Combine(_dir, "Single32.vst3");
        File.Copy(CoreClr, single32);
        using (var f = File.Open(single32, FileMode.Open))
        {
            var b = new byte[4];
            f.Position = 0x3C;
            f.ReadExactly(b);
            f.Position = BitConverter.ToInt32(b) + 4;
            f.Write([0x4C, 0x01]);
        }
        File.Copy(CoreClr, Path.Combine(_dir, "helper.dll")); // не плагин: ни в каталог, ни в списки

        var catalog = new PluginCatalog(new PluginScanner(hostExe: @"Z:\no-host.exe"), Path.Combine(_dir, "plugins.json"));
        await catalog.RefreshAsync([_dir]);

        var problems = catalog.NotSupported.ToDictionary(p => Path.GetFileName(p.Path), p => p.Problem);
        Assert.Equal(new Dictionary<string, ScanProblem>
        {
            ["Arm.vst3"] = ScanProblem.NoX64Binary,
            ["Old.vst3"] = ScanProblem.Bits32,
            ["Single32.vst3"] = ScanProblem.Bits32,
        }, problems);
        Assert.Empty(catalog.Effects);
    }

    [Fact]
    public void Signature_ValidInvalidAndMissing()
    {
        var ms = Authenticode.Check(CoreClr);
        Assert.Equal(SignatureStatus.Valid, ms.Status);
        Assert.Equal(".NET", ms.Signer); // CN сертификата, как «Имя подписавшего» в свойствах файла

        string patched = Path.Combine(_dir, "patched.dll");
        File.Copy(CoreClr, patched);
        using (var f = File.Open(patched, FileMode.Open)) { f.Position = 0x40; f.WriteByte(0x55); } // заглушка DOS: хэш не сходится
        Assert.Equal(SignatureStatus.Invalid, Authenticode.Check(patched).Status);

        string empty = Path.Combine(_dir, "empty.vst3");
        File.WriteAllBytes(empty, []);
        Assert.Equal(new FileSignature(SignatureStatus.NotSigned, null), Authenticode.Check(empty));
    }

    [Fact]
    public async Task Host_ScansAGain_ThroughTheFactory()
    {
        if (!TestHost.Available() || TestHost.AGain is not { } again) return;
        var scanner = new PluginScanner(TestHost.Path);

        var m = await scanner.ScanAsync(again);

        Assert.NotNull(m);
        Assert.Equal(ScanProblem.None, m.Problem);
        Assert.False(m.FromModuleInfo);
        Assert.Equal(1, scanner.ProcessCount);
        Assert.Contains(m.Classes, c => c.IsEffect && c.Name.Contains("AGain"));
        Assert.Equal(SignatureStatus.NotSigned, m.Signature?.Status);
    }

    /// <summary>DLL в бандле названа не как бандл (так у Elgato): её находят и скан, и загрузка в хост.</summary>
    [Fact]
    public async Task Host_LoadsBundleWithDifferentlyNamedDll()
    {
        if (!TestHost.Available() || TestHost.AGain is not { } again) return;
        string bundle = Path.Combine(_dir, "Renamed.vst3");
        foreach (string f in Directory.GetFiles(again, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(bundle, Path.GetRelativePath(again, f));
            if (Path.GetFileName(f).Equals("again.vst3", StringComparison.OrdinalIgnoreCase))
                to = Path.Combine(Path.GetDirectoryName(to)!, "again_v1.2.3.vst3");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }

        var m = await new PluginScanner(TestHost.Path).ScanAsync(bundle);
        Assert.Equal(ScanProblem.None, m?.Problem);
        using var host = TestHost.Start();
        Assert.Contains("AGain", (await host.LoadAsync(Mixer.Plugins.Host.PluginLoad.Vst3(bundle))).Name);
    }

    [Fact]
    public async Task Host_CrashHangAndVst2_AreReported()
    {
        if (!TestHost.Available()) return;
        var scanner = new PluginScanner(TestHost.Path, TimeSpan.FromSeconds(1));

        var crash = await scanner.ScanAsync(Path.Combine(TestModules, "crash.vst3"));
        Assert.Equal(ScanProblem.Crashed, crash?.Problem);
        Assert.Contains("0xC0000005", crash?.Detail);

        var sw = Stopwatch.StartNew();
        var hang = await scanner.ScanAsync(Path.Combine(TestModules, "hang.vst3"));
        Assert.Equal(ScanProblem.Timeout, hang?.Problem);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 5);

        int before = scanner.ProcessCount;
        var vst2 = await scanner.ScanAsync(Path.Combine(TestModules, "vst2.dll"));
        Assert.Equal(ScanProblem.Vst2, vst2?.Problem);
        Assert.Equal(before, scanner.ProcessCount); // без процесса
    }

    /// <summary>Критерий приёмки: папка из 100 модулей с зависающим и падающим; повторный запуск по кэшу — меньше 1 с.</summary>
    [Fact]
    public async Task Catalog_100Modules_ThenFromCache()
    {
        if (!TestHost.Available()) return;
        string folder = Path.Combine(_dir, "VST3");
        for (int i = 0; i < 98; i++) FakeBundle(Path.Combine(folder, $"Vendor{i % 7}"), $"Fx{i}", ModuleInfo((Fx(i), PluginClass.AudioModuleClass, "Fx|Dynamics")));
        File.Copy(Path.Combine(TestModules, "hang.vst3"), Path.Combine(folder, "hang.vst3"));
        File.Copy(Path.Combine(TestModules, "crash.vst3"), Path.Combine(folder, "crash.vst3"));
        string cache = Path.Combine(_dir, "plugins.json");

        var catalog = new PluginCatalog(new PluginScanner(TestHost.Path, TimeSpan.FromSeconds(2)), cache);
        var progress = new List<ScanProgress>();
        catalog.Progress += p => { lock (progress) progress.Add(p); };
        var refresh = catalog.RefreshAsync([folder]);
        Assert.False(refresh.IsCompleted); // идёт в фоне: вызывающий поток не ждёт скана
        await refresh.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(98, catalog.Effects.Count);
        Assert.Equal([ScanProblem.Crashed, ScanProblem.Timeout], catalog.NotLoaded.Select(m => m.Problem).Order());
        Assert.Equal(new ScanProgress(100, 100, null), progress[^1] with { Module = null });

        // второй запуск: всё из кэша
        var scanner = new PluginScanner(TestHost.Path, TimeSpan.FromSeconds(2));
        var again = new PluginCatalog(scanner, cache);
        var sw = Stopwatch.StartNew();
        await again.RefreshAsync([folder]);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0, 1);
        Assert.Equal(0, scanner.ProcessCount);
        Assert.Equal(98, again.Effects.Count);
        Assert.Equal(2, again.NotLoaded.Count);

        // изменился один модуль, другой удалён: проверяется только изменившийся
        string changed = Directory.GetFiles(Path.Combine(folder, "Vendor0", "Fx0.vst3", "Contents", "x86_64-win"))[0];
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
        Directory.Delete(Path.Combine(folder, "Vendor1", "Fx1.vst3"), recursive: true);
        int total = -1;
        again.Progress += p => total = p.Total;
        await again.RefreshAsync([folder]);
        Assert.Equal(1, total);
        Assert.Equal(97, again.Effects.Count);
        Assert.Null(again.Find(Fx(1)));
    }

    [Fact]
    public async Task BrokenCache_IsScannedAgain()
    {
        FakeBundle(_dir, "Fake", ModuleInfo((Fx(1), PluginClass.AudioModuleClass, "Fx")));
        string cache = Path.Combine(_dir, "plugins.json");
        File.WriteAllText(cache, "{ not json");
        var log = new List<string>();

        var catalog = new PluginCatalog(new PluginScanner(hostExe: @"Z:\no-host.exe"), cache, log.Add);
        await catalog.RefreshAsync([_dir]);

        Assert.Single(catalog.Effects);
        Assert.Contains(log, l => l.Contains("unreadable"));
        Assert.Single(new PluginCatalog(new PluginScanner(hostExe: @"Z:\no-host.exe"), cache).Effects);
    }
}
