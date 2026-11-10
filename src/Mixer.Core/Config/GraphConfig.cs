using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mixer.Core.Config;

public enum NodeKind
{
    DeviceInput,
    Loopback,
    App,
    System,
    DeviceOutput,
}

/// <summary>Роль Windows, за устройством по умолчанию которой следует узел.</summary>
public enum DefaultRole
{
    /// <summary>Обычное устройство по умолчанию (Console и Multimedia).</summary>
    Console,
    /// <summary>Устройство связи по умолчанию (Communications): его берут Discord, Teams, звонки.</summary>
    Communications,
}

/// <summary>Узел графа в graph.json. Поля, не относящиеся к типу узла, пустые и не пишутся.</summary>
public sealed record NodeConfig
{
    public required string Id { get; init; }
    public required NodeKind Kind { get; init; }
    public string Name { get; init; } = "";

    public string? EndpointId { get; init; }
    public string? EndpointName { get; init; }
    /// <summary>
    /// Узел «Устройство по умолчанию» (вход, loopback или выход): устройство берётся у Windows по роли и меняется
    /// вслед за ней. EndpointId и EndpointName у такого узла пустые.
    /// </summary>
    public DefaultRole? DefaultRole { get; init; }

    /// <summary>Для узла «Приложение» (этап 2).</summary>
    public string? Exe { get; init; }
    public bool? TakeOver { get; init; }

    public float GainDb { get; init; }
    public bool Muted { get; init; }
    /// <summary>Только у источников.</summary>
    public bool? Mono { get; init; }
    /// <summary>Только у источников: −1 … +1.</summary>
    public float? Balance { get; init; }

    public double X { get; init; }
    public double Y { get; init; }
    public string? Color { get; init; }

    [JsonIgnore] public bool IsSource => Kind != NodeKind.DeviceOutput;
    [JsonIgnore] public bool IsOutput => Kind == NodeKind.DeviceOutput;
    [JsonIgnore] public bool HasDevice => Kind is NodeKind.DeviceInput or NodeKind.Loopback or NodeKind.DeviceOutput;
    [JsonIgnore] public bool FollowsDefault => DefaultRole is not null && HasDevice;
}

public sealed record WireConfig
{
    public required string From { get; init; }
    public required string To { get; init; }
    public float GainDb { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record GraphConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    /// <summary>Эндпоинт-заглушка для режима «Забрать себе» (этап 2).</summary>
    public string? SinkEndpoint { get; init; }
    /// <summary>Журнал приложений с привязкой к заглушке (этап 2).</summary>
    public List<string> BoundApps { get; init; } = [];
    public List<NodeConfig> Nodes { get; init; } = [];
    public List<WireConfig> Wires { get; init; } = [];
}

public static class GraphSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // кириллица в именах узлов остаётся читаемой
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static string Serialize(GraphConfig graph) => JsonSerializer.Serialize(graph, Options);

    public static GraphConfig Deserialize(string json)
    {
        var g = JsonSerializer.Deserialize<GraphConfig>(json, Options) ?? throw new InvalidDataException(CoreStrings.Config_Empty);
        if (g.Version > GraphConfig.CurrentVersion)
            throw new InvalidDataException(Loc.F(CoreStrings.Config_NewerVersionFormat, g.Version, GraphConfig.CurrentVersion));
        return g;
    }

    public static GraphConfig Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>
    /// Атомарная запись: во временный файл рядом, затем File.Replace (или Move для нового файла).
    /// backupPath — куда тем же File.Replace переложить прежний файл (резервная копия без лишнего копирования).
    /// </summary>
    public static void Save(string path, GraphConfig graph, string? backupPath = null)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string tmp = full + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            w.Write(Serialize(graph));
            w.Flush();
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(full)) File.Replace(tmp, full, destinationBackupFileName: backupPath);
        else File.Move(tmp, full);
    }
}
