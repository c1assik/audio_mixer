using System.Text.Json;

namespace Mixer.Plugins.Catalog;

/// <summary>
/// Каталог плагинов с кэшем <c>%LocalAppData%\Mixer\plugins.json</c>. <see cref="RefreshAsync"/> сканирует в фоне, по
/// одному, только новые и изменившиеся модули (ключ — путь, размер и время изменения файла модуля); пропавшие
/// убираются. Когда звать обновление (только если в схеме есть плагины или открыт выбор плагина), решает вызывающий.
/// События — из потока скана.
/// </summary>
public sealed class PluginCatalog
{
    private const int CacheVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record CacheFile(int Version, List<ModuleScan> Modules);

    private readonly PluginScanner _scanner;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ModuleScan> _modules = new(StringComparer.OrdinalIgnoreCase);

    public PluginCatalog(PluginScanner scanner, string? cachePath = null, Action<string>? log = null)
    {
        _scanner = scanner;
        _log = log;
        CachePath = cachePath ?? DefaultCachePath;
        foreach (var m in LoadCache()) _modules[m.Path] = m;
    }

    public static string DefaultCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mixer", "plugins.json");

    public string CachePath { get; }

    /// <summary>Проверен очередной модуль: «проверено N из M», найденные плагины появляются по мере готовности.</summary>
    public event Action<ScanProgress>? Progress;

    public IReadOnlyList<ModuleScan> Modules
    {
        get { lock (_modules) return _modules.Values.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public IReadOnlyList<CatalogEntry> Effects =>
        Modules.Where(m => m.Problem == ScanProblem.None)
            .SelectMany(m => m.Classes.Where(c => c.IsEffect).Select(c => new CatalogEntry(c, m)))
            .OrderBy(e => e.Class.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    /// <summary>«Не загрузились»: упал, не ответил за таймаут, не загрузился.</summary>
    public IReadOnlyList<ModuleScan> NotLoaded => Modules.Where(m => m.NotLoaded).ToArray();

    /// <summary>«Не поддерживается»: VST2, 32 бита, без x64, без эффектов — и инструменты из модулей с эффектами.</summary>
    public IReadOnlyList<CatalogProblem> NotSupported =>
        Modules.SelectMany(m => m.NotSupported
                ? [new CatalogProblem(m.Path, m.FileName, m.Problem, m.Detail)]
                : m.Classes.Where(c => !c.IsEffect).Select(c => new CatalogProblem(m.Path, c.Name,
                    c.IsInstrument ? ScanProblem.Instrument : ScanProblem.NoEffects, null)))
            .ToArray();

    /// <summary>Эффект по GUID класса: так слот находит плагин, переустановленный в другую папку.</summary>
    public CatalogEntry? Find(string classId) =>
        Effects.FirstOrDefault(e => string.Equals(e.Class.ClassId, classId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Обойти <paramref name="folders"/> (обычно <see cref="PluginFiles.StandardFolders"/> и папки пользователя),
    /// просканировать новое и изменившееся. <paramref name="full"/> — полный перескан, без кэша.
    /// Два обновления сразу не идут: второе ждёт первое.
    /// </summary>
    public async Task RefreshAsync(IEnumerable<string> folders, bool full = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            folders = folders.ToArray();
            var todo = await Task.Run(() =>
            {
                var found = PluginFiles.Enumerate(folders).ToList();
                var pending = new List<string>();
                lock (_modules)
                {
                    var present = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
                    foreach (string gone in _modules.Keys.Where(p => !present.Contains(p)).ToArray()) _modules.Remove(gone);
                    foreach (string p in found)
                    {
                        long size;
                        DateTime modified;
                        try { (_, size, modified) = PluginFiles.Stamp(p); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                        if (full || !_modules.TryGetValue(p, out var m) || m.Size != size || m.ModifiedUtc != modified)
                            pending.Add(p);
                    }
                }
                return pending;
            }, ct).ConfigureAwait(false);

            Progress?.Invoke(new ScanProgress(0, todo.Count, null));
            for (int i = 0; i < todo.Count; i++)
            {
                ModuleScan? m;
                try
                {
                    m = await _scanner.ScanAsync(todo[i], ct: ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    m = null; // удалили или закрыли доступ, пока шёл скан: в следующий раз
                    _log?.Invoke($"Plug-in scan: {todo[i]}: {e.Message}");
                }
                lock (_modules)
                {
                    if (m != null) _modules[m.Path] = m;
                    else _modules.Remove(todo[i]);
                }
                Progress?.Invoke(new ScanProgress(i + 1, todo.Count, m));
            }
            if (todo.Count > 0) _log?.Invoke($"Plug-in scan: {todo.Count} checked, {Effects.Count} effects in the catalog");
        }
        finally
        {
            SaveCache();
            _gate.Release();
        }
    }

    /// <summary>Пересканировать один модуль; <paramref name="withWindow"/> — «Просканировать с окном» (без таймаута).</summary>
    public async Task<ModuleScan?> RescanAsync(string path, bool withWindow = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var m = await _scanner.ScanAsync(path, withWindow, ct).ConfigureAwait(false);
            lock (_modules)
            {
                if (m != null) _modules[m.Path] = m;
                else _modules.Remove(path);
            }
            Progress?.Invoke(new ScanProgress(1, 1, m));
            return m;
        }
        finally
        {
            SaveCache();
            _gate.Release();
        }
    }

    private List<ModuleScan> LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return [];
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath), Json);
            if (file?.Version == CacheVersion && file.Modules != null) return file.Modules.Where(m => m?.Path != null).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Invoke($"Plug-in cache {CachePath} is unreadable, scanning again: {e.Message}");
        }
        return [];
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(CachePath))!);
            string tmp = CachePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheFile(CacheVersion, Modules.ToList()), Json));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Plug-in cache {CachePath} not saved: {e.Message}");
        }
    }
}
