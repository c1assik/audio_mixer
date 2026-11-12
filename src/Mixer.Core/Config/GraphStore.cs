namespace Mixer.Core.Config;

/// <summary>
/// graph.json приложения. Рядом лежат пять последних резервных копий «graph.json.bak-ГГГГММДД-ччммсс»: копия
/// делается при первой записи за запуск и потом не чаще раза в <see cref="BackupInterval"/> (иначе пять копий
/// покрывали бы пять секунд работы с фейдером). Битый graph.json восстанавливается из самой свежей читаемой копии.
/// Пишут двое: интерфейс (граф, с задержкой 1 с после последней правки) и хост (журнал
/// boundApps — сразу и до вызова Set, ошибка записи блокирует привязку). Файл всегда собирается из последнего графа
/// интерфейса и последнего журнала хоста под одной блокировкой, поэтому ни одна запись не затирает другую.
/// </summary>
public sealed class GraphStore : IDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan BackupInterval = TimeSpan.FromMinutes(10);
    public const int BackupCount = 5;
    private const string BackupMarker = ".bak-";

    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private DateTime? _lastBackup;
    private readonly Timer _timer;
    private GraphConfig _ui;
    private List<string> _journal;
    private List<string> _volumeJournal;
    private List<string> _micJournal;
    private bool _dirty;
    private bool _disposed;

    public string Path { get; }

    /// <summary>Сохранение прошло (null) или упало (исключение). Вызывается из потока таймера или хоста.</summary>
    public event Action<Exception?>? Saved;

    /// <param name="now">Часы для резервных копий (тесты); по умолчанию — местное время.</param>
    public GraphStore(string path, GraphConfig loaded, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.Now);
        Path = System.IO.Path.GetFullPath(path);
        _ui = loaded;
        _journal = loaded.BoundApps.ToList();
        _volumeJournal = loaded.VolumeApps.ToList();
        _micJournal = loaded.MicApps.ToList();
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsDirty { get { lock (_gate) return _dirty; } }

    /// <summary>
    /// Прочитать граф. Нет файла — пустой граф. Файл битый — он откладывается в «graph.json.broken-…» (иначе
    /// автосохранение затёрло бы его), а граф берётся из самой свежей читаемой резервной копии; копий нет — пустой граф.
    /// error объясняет, что случилось.
    /// </summary>
    public static GraphConfig LoadOrEmpty(string path, out string? error) => LoadOrEmpty(path, out error, out _);

    /// <param name="upgraded">Файл был прежней версии и перенесён (оригинал отложен в «graph.v1.json»).</param>
    public static GraphConfig LoadOrEmpty(string path, out string? error, out bool upgraded)
    {
        error = null;
        upgraded = false;
        if (!File.Exists(path)) return new GraphConfig();
        try
        {
            var loaded = GraphSerializer.Load(path);
            upgraded = loaded.Version < GraphConfig.CurrentVersion;
            return Upgrade(path, loaded);
        }
        catch (Exception ex) when (IsCorrupt(ex))
        {
            string aside = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            string what;
            try
            {
                File.Copy(path, aside, overwrite: true);
                what = Loc.F(CoreStrings.Store_UnreadableMovedFormat, ex.Message, System.IO.Path.GetFileName(aside));
            }
            catch (IOException copyError)
            {
                what = Loc.F(CoreStrings.Store_UnreadableNotMovedFormat, ex.Message, copyError.Message);
            }
            foreach (string backup in Backups(path))
            {
                try
                {
                    var g = GraphSerializer.Load(backup);
                    error = Loc.F(CoreStrings.Store_BackupRestoredFormat, what, File.GetLastWriteTime(backup));
                    return g;
                }
                catch (Exception bad) when (IsCorrupt(bad) || bad is IOException or UnauthorizedAccessException)
                {
                    // копия тоже битая — следующая
                }
            }
            error = Loc.F(CoreStrings.Store_NoBackupsFormat, what);
            return new GraphConfig();
        }
    }

    /// <summary>
    /// Файл прежней версии: оригинал откладывается рядом («graph.v1.json», если такого ещё нет), чтобы откат на v1
    /// ничего не терял — v1 не читает узлы-шины и обработку. Сам граф переносится как есть: поля v1 не изменились.
    /// </summary>
    public static GraphConfig Upgrade(string path, GraphConfig g)
    {
        if (g.Version >= GraphConfig.CurrentVersion) return g;
        string old = System.IO.Path.ChangeExtension(path, $".v{g.Version}.json");
        try
        {
            if (!File.Exists(old)) File.Copy(path, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // без копии граф всё равно читается; запись v2 поверх v1 остаётся читаемой для v2
        }
        return LinkCables(g) with { Version = GraphConfig.CurrentVersion };
    }

    /// <summary>
    /// Перенос с v1: узлы CABLE Input / Output связываются в кабели по таблице сопоставления. Роль — по тому, как кабель
    /// использовался: микшер писал в Input — «виртуальный микрофон», читал Output — «виртуальный вход».
    /// </summary>
    public static GraphConfig LinkCables(GraphConfig g, Cables.CableTable? table = null)
    {
        table ??= Cables.CableTable.Default;
        if (!g.Nodes.Any(n => n.Cable is null && table.Detect(n) is not null)) return g;
        return g with { Nodes = g.Nodes.Select(n => n.Cable is null && table.Detect(n) is { } link ? n with { Cable = link } : n).ToList() };
    }

    /// <summary>Файл прочитан, но испорчен. Ошибка доступа — не порча: такой файл нельзя подменять копией.</summary>
    private static bool IsCorrupt(Exception ex) => ex is System.Text.Json.JsonException or InvalidDataException or NotSupportedException;

    /// <summary>Резервные копии рядом с графом, самая свежая первой.</summary>
    public static IReadOnlyList<string> Backups(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string? dir = System.IO.Path.GetDirectoryName(full);
        if (dir is null || !Directory.Exists(dir)) return [];
        string prefix = System.IO.Path.GetFileName(full) + BackupMarker;
        return Directory.GetFiles(dir, prefix + "*")
            .Where(f => System.IO.Path.GetFileName(f).Length == prefix.Length + 15) // ГГГГММДД-ччммсс
            .OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Граф от интерфейса: запишется через <see cref="SaveDelay"/> после последней правки.</summary>
    public void Changed(GraphConfig ui)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _ui = ui;
            _dirty = true;
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Журналы хоста (привязки и громкости сессий): пишутся сразу, ошибка уходит вызывающему (привязка или громкость
    /// тогда не ставится).
    /// </summary>
    public void SaveJournal(GraphConfig hostGraph)
    {
        lock (_gate)
        {
            var before = (_journal, _volumeJournal, _micJournal);
            _journal = hostGraph.BoundApps.ToList();
            _volumeJournal = hostGraph.VolumeApps.ToList();
            _micJournal = hostGraph.MicApps.ToList();
            try
            {
                Write();
            }
            catch
            {
                (_journal, _volumeJournal, _micJournal) = before; // хост не примет журнал — и на диске его быть не должно
                throw;
            }
        }
    }

    /// <summary>Записать отложенное сейчас (выход из программы).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            try
            {
                Write();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Saved?.Invoke(ex);
            }
        }
    }

    /// <summary>Под _gate.</summary>
    private void Write()
    {
        var now = _now();
        string? backup = null;
        if (File.Exists(Path) && (_lastBackup is not { } last || now - last >= BackupInterval || now < last))
            backup = $"{Path}{BackupMarker}{now:yyyyMMdd-HHmmss}";
        GraphSerializer.Save(Path, _ui with { BoundApps = _journal, VolumeApps = _volumeJournal, MicApps = _micJournal }, backup);
        _dirty = false;
        if (backup is not null)
        {
            _lastBackup = now;
            TrimBackups();
        }
        Saved?.Invoke(null);
    }

    private void TrimBackups()
    {
        foreach (string old in Backups(Path).Skip(BackupCount))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // лишняя копия не мешает; удалится в следующий раз
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
        Flush();
    }
}
