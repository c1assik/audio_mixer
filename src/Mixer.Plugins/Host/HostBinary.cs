using System.Reflection;
using System.Security.Cryptography;

namespace Mixer.Plugins.Host;

/// <summary>
/// Mixer.PluginHost.exe вшит в сборку ресурсом. При первом использовании плагинов он распаковывается в
/// <c>%LocalAppData%\Mixer\bin\&lt;версия&gt;\</c> (версия — версия микшера и начало SHA-256 самого exe, так что
/// пересобранный хост не путается со старым), рядом — лицензии сторонних компонентов. Старые папки удаляются.
/// </summary>
public static class HostBinary
{
    public const string ResourceName = "Mixer.PluginHost.exe";
    public const string FileName = "Mixer.PluginHost.exe";
    /// <summary>Переменная окружения с путём к готовому хосту (разработка, тесты): тогда ничего не распаковывается.</summary>
    public const string OverrideVariable = "MIXER_PLUGINHOST";

    private static readonly object s_lock = new();
    private static string? s_extracted;

    public static bool IsEmbedded => typeof(HostBinary).Assembly.GetManifestResourceInfo(ResourceName) != null;

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mixer", "bin");

    /// <summary>Путь к хосту: из <see cref="OverrideVariable"/> или распакованный. null — хоста нет в этой сборке.</summary>
    public static string? Locate(Action<string>? log = null)
    {
        string? over = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrEmpty(over) && File.Exists(over)) return Path.GetFullPath(over);
        return IsEmbedded ? Extract(DefaultRoot, log) : null;
    }

    /// <summary>Распаковать в <paramref name="root"/>\&lt;версия&gt;\ (один раз за процесс) и удалить старые версии.</summary>
    public static string Extract(string root, Action<string>? log = null)
    {
        lock (s_lock)
        {
            if (s_extracted != null && File.Exists(s_extracted) &&
                string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(s_extracted)), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                return s_extracted;

            byte[] exe = ReadResource(ResourceName) ?? throw new InvalidOperationException("plug-in host is not embedded in this build");
            string hash = Convert.ToHexString(SHA256.HashData(exe))[..8].ToLowerInvariant();
            string version = typeof(HostBinary).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            string folderName = $"{version}-{hash}";
            string dir = Path.Combine(root, folderName);
            string path = Path.Combine(dir, FileName);
            Directory.CreateDirectory(dir);

            if (!SameFile(path, exe))
            {
                // через временный файл: недописанный exe никогда не окажется на месте настоящего
                string tmp = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(tmp, exe);
                File.Move(tmp, path, overwrite: true);
                log?.Invoke($"Plug-in host extracted to {path}");
            }
            foreach (var (name, text) in ThirdPartyLicenses.All)
            {
                string licensePath = Path.Combine(dir, "licenses", name);
                if (File.Exists(licensePath)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(licensePath)!);
                File.WriteAllText(licensePath, text);
            }
            RemoveOld(root, folderName, log);
            s_extracted = path;
            return path;
        }
    }

    private static bool SameFile(string path, byte[] bytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != bytes.Length) return false;
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void RemoveOld(string root, string keep, Action<string>? log)
    {
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                log?.Invoke($"Removed old plug-in host {dir}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // ещё запущен (другой экземпляр, зависший хост): удалится в следующий раз
            }
        }
    }

    internal static byte[]? ReadResource(string name)
    {
        using Stream? s = typeof(HostBinary).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }
}

/// <summary>Тексты лицензий сторонних компонентов хоста (папка licenses в репозитории).</summary>
public static class ThirdPartyLicenses
{
    /// <summary>Имя файла и текст, по алфавиту.</summary>
    public static IReadOnlyList<(string Name, string Text)> All { get; } = Load();

    private static IReadOnlyList<(string, string)> Load()
    {
        Assembly asm = typeof(ThirdPartyLicenses).Assembly;
        var list = new List<(string, string)>();
        foreach (string res in asm.GetManifestResourceNames().Where(n => n.StartsWith("licenses/", StringComparison.Ordinal)).Order())
        {
            using Stream s = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(s);
            list.Add((res["licenses/".Length..], reader.ReadToEnd()));
        }
        return list;
    }
}
