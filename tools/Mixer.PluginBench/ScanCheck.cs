using System.Diagnostics;
using Mixer.Plugins.Catalog;

namespace Mixer.PluginBench;

/// <summary>Этап 2: скан стандартных папок VST3 (и --folder) с кэшем микшера и печать каталога и обоих списков.</summary>
internal static class ScanCheck
{
    public static int Run(string hostExe, IReadOnlyList<string> folders, bool full)
    {
        var catalog = new PluginCatalog(new PluginScanner(hostExe, log: Console.WriteLine), log: Console.WriteLine);
        catalog.Progress += p => { if (p.Module != null) Console.WriteLine($"  проверено {p.Checked} из {p.Total}: {p.Module.Path}"); };
        var all = PluginFiles.StandardFolders.Concat(folders).ToArray();
        Console.WriteLine($"папки: {string.Join("; ", all)}\nкэш: {catalog.CachePath}");

        var sw = Stopwatch.StartNew();
        catalog.RefreshAsync(all, full).GetAwaiter().GetResult();
        Console.WriteLine($"скан: {sw.Elapsed.TotalSeconds:0.00} с\n\nэффекты ({catalog.Effects.Count}):");
        foreach (var e in catalog.Effects)
            Console.WriteLine($"  {e.Class.Name} · {e.Class.Vendor} · {string.Join("/", e.Class.Categories)} · {e.Class.ClassId}" +
                $" · {(e.Module.FromModuleInfo ? "moduleinfo" : "фабрика")} · подпись: {e.Module.Signature?.Status} {e.Module.Signature?.Signer}");
        Console.WriteLine($"не загрузились ({catalog.NotLoaded.Count}):");
        foreach (var m in catalog.NotLoaded) Console.WriteLine($"  {m.Path}: {m.Problem} {m.Detail}");
        Console.WriteLine($"не поддерживается ({catalog.NotSupported.Count}):");
        foreach (var p in catalog.NotSupported) Console.WriteLine($"  {p.Name} ({p.Path}): {p.Problem} {p.Detail}");
        return 0;
    }
}
