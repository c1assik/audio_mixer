using System.Collections.Concurrent;
using System.Text;

namespace Mixer.Core.Sys;

/// <summary>
/// Журнал в %LocalAppData%\Mixer\logs: файл на день («mixer-ГГГГММДД.log»), при росте больше <see cref="MaxFileBytes"/>
/// — следующая часть («mixer-ГГГГММДД-2.log»), хранятся <see cref="KeepFiles"/> последних файлов.
/// <see cref="Write"/> только ставит строку в очередь, на диск пишет свой фоновый поток: вызывающий не ждёт диска.
/// Из аудиопотоков не вызывать (строки аллоцируют) — их состояние журналирует управляющий поток хоста.
/// </summary>
public sealed class FileLog : IDisposable
{
    public const long MaxFileBytes = 2 * 1024 * 1024;
    public const int KeepFiles = 10;
    private const int QueueLimit = 10_000;
    private const string Prefix = "mixer-";

    private readonly BlockingCollection<string> _queue = new(QueueLimit);
    private readonly Func<DateTime> _now;
    private readonly Thread _thread;
    private readonly long _maxBytes;
    private int _dropped;
    private StreamWriter? _file;
    private string? _day;
    private int _part;
    private long _size;

    public string Directory { get; }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mixer", "logs");

    /// <param name="now">Часы (тесты); по умолчанию — местное время.</param>
    /// <param name="maxFileBytes">Размер части (тесты); по умолчанию <see cref="MaxFileBytes"/>.</param>
    public FileLog(string directory, Func<DateTime>? now = null, long maxFileBytes = MaxFileBytes)
    {
        Directory = Path.GetFullPath(directory);
        _now = now ?? (() => DateTime.Now);
        _maxBytes = maxFileBytes;
        _thread = new Thread(WriterMain) { IsBackground = true, Name = "FileLog", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Записать строку. Не блокирует; если очередь переполнена, строка теряется, а потеря отмечается в журнале.</summary>
    public void Write(string source, string message)
    {
        string line = $"{_now():yyyy-MM-dd HH:mm:ss.fff} [{source}] {message}";
        try
        {
            if (!_queue.TryAdd(line)) Interlocked.Increment(ref _dropped);
        }
        catch (InvalidOperationException)
        {
            // журнал уже закрыт
        }
    }

    public void Error(string source, Exception ex) => Write(source, $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    /// <summary>Файлы журнала, самый свежий первым: по дню и номеру части из имени (время записи частей может совпасть).</summary>
    public static IReadOnlyList<string> Files(string directory)
    {
        if (!System.IO.Directory.Exists(directory)) return [];
        return System.IO.Directory.GetFiles(directory, Prefix + "*.log")
            .Select(f => (Path: f, Key: SortKey(Path.GetFileNameWithoutExtension(f))))
            .OrderByDescending(x => x.Key.Day, StringComparer.Ordinal)
            .ThenByDescending(x => x.Key.Part)
            .Select(x => x.Path)
            .ToList();
    }

    /// <summary>«mixer-20261110» → (20261110, 1), «mixer-20261110-3» → (20261110, 3).</summary>
    private static (string Day, int Part) SortKey(string name)
    {
        string rest = name[Prefix.Length..];
        int dash = rest.IndexOf('-');
        if (dash < 0) return (rest, 1);
        return (rest[..dash], int.TryParse(rest[(dash + 1)..], out int part) ? part : 0);
    }

    private void WriterMain()
    {
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                WriteLine(line);
                // пачкой: всё, что накопилось, — потом один Flush
                while (_queue.TryTake(out string? more)) WriteLine(more);
                int dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0) WriteLine($"{_now():yyyy-MM-dd HH:mm:ss.fff} [log] log queue overflow, lines dropped: {dropped}");
                try { _file?.Flush(); } catch (IOException) { }
            }
        }
        finally
        {
            try { _file?.Dispose(); } catch (IOException) { }
        }
    }

    private void WriteLine(string line)
    {
        try
        {
            Open(Encoding.UTF8.GetByteCount(line) + 2);
            if (_file is null) return;
            _file.WriteLine(line);
            _size += Encoding.UTF8.GetByteCount(line) + 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // журнал не должен ронять программу (диск полон, папка недоступна): строка теряется
            try { _file?.Dispose(); } catch (IOException) { }
            _file = null;
        }
    }

    /// <summary>Открыть файл дня; новая часть — если текущая переросла предел.</summary>
    private void Open(int next)
    {
        string day = _now().ToString("yyyyMMdd");
        if (_file is not null && day == _day && _size + next <= _maxBytes) return;
        _file?.Dispose();
        _file = null;
        System.IO.Directory.CreateDirectory(Directory);
        if (day != _day)
        {
            _day = day;
            _part = 1;
        }
        else if (_size + next > _maxBytes)
        {
            _part++;
        }
        while (true)
        {
            string path = Path.Combine(Directory, _part == 1 ? $"{Prefix}{day}.log" : $"{Prefix}{day}-{_part}.log");
            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (size > 0 && size + next > _maxBytes)
            {
                _part++;
                continue;
            }
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _file = new StreamWriter(fs, new UTF8Encoding(false));
            _size = size;
            break;
        }
        Trim();
    }

    private void Trim()
    {
        foreach (string old in Files(Directory).Skip(KeepFiles))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Дописать очередь и закрыть файл.</summary>
    public void Dispose()
    {
        if (_queue.IsAddingCompleted) return;
        _queue.CompleteAdding();
        _thread.Join(3000);
    }
}
