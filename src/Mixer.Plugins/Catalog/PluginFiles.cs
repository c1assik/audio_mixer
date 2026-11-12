using System.Reflection.PortableExecutable;
using System.Text;

namespace Mixer.Plugins.Catalog;

/// <summary>
/// Файлы плагинов без их загрузки: обход папок, файл модуля внутри бандла, заголовок PE (разрядность и точки входа).
/// </summary>
public static class PluginFiles
{
    /// <summary>Стандартные папки VST3: Common Files\VST3 и %LOCALAPPDATA%\Programs\Common\VST3.</summary>
    public static IReadOnlyList<string> StandardFolders { get; } =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3"),
    ];

    private const int MaxDepth = 8;

    /// <summary>Кандидаты: бандлы *.vst3 (внутрь не заходим), однофайловые *.vst3 и *.dll (возможно, VST2).</summary>
    public static IEnumerable<string> Enumerate(IEnumerable<string> folders)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
            if (Directory.Exists(folder))
                foreach (string p in Walk(Path.GetFullPath(folder), 0))
                    if (seen.Add(p)) yield return p;
    }

    private static IEnumerable<string> Walk(string dir, int depth)
    {
        string[] dirs, files;
        try
        {
            dirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string f in files)
            if (HasExtension(f, ".vst3") || HasExtension(f, ".dll")) yield return f;
        foreach (string d in dirs)
        {
            if (HasExtension(d, ".vst3")) { yield return d; continue; }
            // ссылки на папки не обходим: так не бывает циклов
            if (depth >= MaxDepth || new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (string p in Walk(d, depth + 1)) yield return p;
        }
    }

    private static bool HasExtension(string path, string ext) => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase);

    public static bool IsBundle(string path) => Directory.Exists(path);

    /// <summary>
    /// Файл модуля: у бандла Contents\x86_64-win\&lt;имя бандла&gt;, иначе единственный .vst3 там же (у Elgato DLL названа
    /// по-своему; так же ищет хост); у файла — он сам. null — в бандле нет x64-версии.
    /// </summary>
    public static string? Binary(string path)
    {
        if (!IsBundle(path)) return File.Exists(path) ? path : null;
        string arch = Path.Combine(path, "Contents", "x86_64-win");
        string named = Path.Combine(arch, Path.GetFileName(path));
        if (File.Exists(named)) return named;
        if (!Directory.Exists(arch)) return null;
        string[] all = Directory.GetFiles(arch, "*.vst3");
        return all.Length == 1 ? all[0] : null;
    }

    public static string ModuleInfoPath(string bundle) => Path.Combine(bundle, "Contents", "Resources", "moduleinfo.json");

    /// <summary>Ключ кэша: размер и время изменения файла модуля (у бандла без x64-версии — папки).</summary>
    public static (string? Binary, long Size, DateTime ModifiedUtc) Stamp(string path)
    {
        string? binary = Binary(path);
        if (binary == null) return (null, 0, Directory.GetLastWriteTimeUtc(path));
        var info = new FileInfo(binary);
        return (binary, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Машина и имена экспортов из заголовка PE. null — не PE или не читается.</summary>
    public static (Machine Machine, HashSet<string> Exports)? ReadPe(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            var exports = new HashSet<string>(StringComparer.Ordinal);
            var dir = headers.PEHeader?.ExportTableDirectory ?? default;
            if (dir.Size > 0)
            {
                var table = pe.GetSectionData(dir.RelativeVirtualAddress).GetReader();
                table.Offset = 24;
                int count = table.ReadInt32();
                table.Offset = 32;
                int namesRva = table.ReadInt32();
                var names = pe.GetSectionData(namesRva).GetReader();
                for (int i = 0; i < Math.Min(count, 100_000); i++)
                {
                    var s = pe.GetSectionData(names.ReadInt32()).GetReader();
                    int len = s.IndexOf(0);
                    if (len > 0) exports.Add(s.ReadUTF8(len));
                }
            }
            return (headers.CoffHeader.Machine, exports);
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Что видно без загрузки: null — можно сканировать дальше; <see cref="ScanProblem.None"/> не возвращается.
    /// <paramref name="notPlugin"/> — .dll без точек входа плагина: это не плагин, в списки не попадает.
    /// </summary>
    public static ScanProblem? Precheck(string path, string? binary, out bool notPlugin)
    {
        notPlugin = false;
        if (binary == null)
            return Directory.Exists(Path.Combine(path, "Contents", "x86-win")) ? ScanProblem.Bits32 : ScanProblem.NoX64Binary;
        var pe = ReadPe(binary);
        if (HasExtension(path, ".dll"))
        {
            if (pe is { } d && !d.Exports.Contains("GetPluginFactory") &&
                (d.Exports.Contains("VSTPluginMain") || d.Exports.Contains("main")))
                return ScanProblem.Vst2;
            if (pe is not { } v || !v.Exports.Contains("GetPluginFactory"))
            {
                notPlugin = true;
                return null;
            }
        }
        return pe?.Machine switch
        {
            Machine.I386 => ScanProblem.Bits32,
            Machine.Amd64 or null => null, // не PE — пусть хост скажет, что не так
            _ => ScanProblem.NoX64Binary,
        };
    }
}
