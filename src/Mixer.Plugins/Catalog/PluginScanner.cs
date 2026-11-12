using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mixer.Plugins.Host;

namespace Mixer.Plugins.Catalog;

/// <summary>
/// Скан одного модуля. Сначала то, что видно без загрузки (VST2, 32 бита, нет x64-версии), затем moduleinfo.json
/// бандла, и только без него — отдельный короткоживущий процесс <c>Mixer.PluginHost.exe --scan</c> с таймаутом:
/// зависший или падающий плагин ломает только этот процесс.
/// </summary>
public sealed class PluginScanner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private const int ExitCrashed = 5;

    private readonly string? _hostExe;
    private readonly Action<string>? _log;

    /// <param name="hostExe">Mixer.PluginHost.exe; null — <see cref="HostBinary.Locate"/> при первом запуске процесса.</param>
    /// <param name="timeout">Сколько ждать процесс скана; по умолчанию <see cref="DefaultTimeout"/>.</param>
    public PluginScanner(string? hostExe = null, TimeSpan? timeout = null, Action<string>? log = null)
    {
        _hostExe = hostExe;
        Timeout = timeout ?? DefaultTimeout;
        _log = log;
    }

    public TimeSpan Timeout { get; }

    /// <summary>Сколько раз запускался процесс скана (для тестов и журнала).</summary>
    public int ProcessCount => _processCount;
    private int _processCount;

    /// <summary>
    /// Просканировать модуль. null — это .dll, которая не плагин. <paramref name="withWindow"/> — «Просканировать с окном»:
    /// без таймаута, плагину можно показать окно активации.
    /// </summary>
    public async Task<ModuleScan?> ScanAsync(string path, bool withWindow = false, CancellationToken ct = default)
    {
        var (binary, size, modified) = PluginFiles.Stamp(path);
        ModuleScan Result(ScanProblem problem, string? detail = null, IReadOnlyList<PluginClass>? classes = null, bool fromInfo = false) =>
            new(path, binary, size, modified, problem, detail, classes ?? [], fromInfo,
                binary != null ? Authenticode.Check(binary) : null);

        if (PluginFiles.Precheck(path, binary, out bool notPlugin) is { } early) return Result(early);
        if (notPlugin) return null;

        if (PluginFiles.IsBundle(path) && ReadModuleInfo(PluginFiles.ModuleInfoPath(path)) is { } fromInfo)
            return Result(fromInfo.Count > 0 ? ScanProblem.None : ScanProblem.NoEffects, classes: fromInfo, fromInfo: true);

        var (problem, detail, classes) = await RunHostAsync(path, withWindow, ct).ConfigureAwait(false);
        if (problem == ScanProblem.None && classes.Count == 0) problem = ScanProblem.NoEffects;
        if (problem != ScanProblem.None) _log?.Invoke($"Plug-in scan: {path}: {problem} {detail}".TrimEnd());
        return Result(problem, detail, classes);
    }

    /// <summary>Аудиомодули из moduleinfo.json. null — файла нет или он не читается (тогда грузим модуль).</summary>
    internal IReadOnlyList<PluginClass>? ReadModuleInfo(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, // moduleinfo.json — JSON5: комментарии и висячие запятые
                AllowTrailingCommas = true,
            });
            string vendor = (string?)root?["Factory Info"]?["Vendor"] ?? "";
            if (root?["Classes"] is not JsonArray list) return null;
            return AudioModules(list.Select(c => (
                (string?)c?["CID"], (string?)c?["Category"], (string?)c?["Name"], (string?)c?["Vendor"],
                (string?)c?["Version"], (string?)c?["SDKVersion"], c?["Sub Categories"])), vendor);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.Invoke($"Plug-in scan: unreadable {file}: {e.Message}; loading the module instead");
            return null;
        }
    }

    private static IReadOnlyList<PluginClass> AudioModules(
        IEnumerable<(string? Cid, string? Category, string? Name, string? Vendor, string? Version, string? Sdk, JsonNode? Sub)> classes,
        string factoryVendor) =>
        classes
            .Where(c => c.Category == PluginClass.AudioModuleClass && !string.IsNullOrEmpty(c.Cid))
            .Select(c => new PluginClass(c.Cid!.ToUpperInvariant(), c.Category!, c.Name ?? "",
                string.IsNullOrEmpty(c.Vendor) ? factoryVendor : c.Vendor, c.Version ?? "", c.Sdk ?? "", SubCategories(c.Sub)))
            .ToArray();

    // массив (moduleinfotool) или строка "Fx|Dynamics" (как в PClassInfo2)
    private static IReadOnlyList<string> SubCategories(JsonNode? n) => n switch
    {
        JsonArray a => a.Select(x => (string?)x).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToArray(),
        JsonValue v when v.TryGetValue(out string? s) => s.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    private async Task<(ScanProblem, string?, IReadOnlyList<PluginClass>)> RunHostAsync(string path, bool withWindow, CancellationToken ct)
    {
        string exe = _hostExe ?? HostBinary.Locate(_log)
            ?? throw new InvalidOperationException("Mixer.PluginHost.exe is not available in this build");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.ArgumentList.Add("--scan");
        psi.ArgumentList.Add(path);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("scan process did not start");
        Interlocked.Increment(ref _processCount);
        HostJob.Assign(process);
        if (withWindow) Native.AllowSetForegroundWindow(process.Id);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!withWindow) cts.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            return (ScanProblem.Timeout, $"{Timeout.TotalSeconds:0.#} s", []);
        }

        string output = await stdout.ConfigureAwait(false);
        string errors = (await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode == ExitCrashed)
            return (ScanProblem.Crashed, errors.Split('\n').FirstOrDefault(l => l.StartsWith("crashed:"))?.Trim() ?? errors, []);
        try
        {
            if (JsonNode.Parse(output) is JsonObject o)
            {
                if ((string?)o["error"] is { } error) return (ScanProblem.LoadFailed, error, []);
                if (o["classes"] is JsonArray list)
                    return (ScanProblem.None, null, AudioModules(list.Select(c => (
                        (string?)c?["cid"], (string?)c?["category"], (string?)c?["name"], (string?)c?["vendor"],
                        (string?)c?["version"], (string?)c?["sdkVersion"], c?["subCategories"])), (string?)o["vendor"] ?? ""));
            }
        }
        catch (JsonException)
        {
        }
        return (ScanProblem.LoadFailed, $"exit code {process.ExitCode}{(errors.Length > 0 ? ": " + errors : "")}", []);
    }
}
