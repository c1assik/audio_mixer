using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mixer.Audio.Dsp;

namespace Mixer.Core.Config;

public enum NodeKind
{
    DeviceInput,
    Loopback,
    App,
    System,
    DeviceOutput,
    /// <summary>Шина: сводит несколько источников (или шин) и раздаёт сумму дальше.</summary>
    Bus,
}

/// <summary>Колонка холста: сигнал идёт слева направо, источники → шины → выходы.</summary>
public enum NodeColumn { Sources, Buses, Outputs }

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
    /// <summary>
    /// Узел «Приложение»: прямой маршрут — если узел ведёт ровно в один выход и ничего со звуком не делает, микшер
    /// не захватывает звук, а назначает приложению этот выход (v2, «Прямые маршруты»). null — включён.
    /// </summary>
    public bool? DirectRoute { get; init; }

    /// <summary>Узел «Весь звук системы»: exe, которые в него не попадают, даже без своего узла.</summary>
    public List<string>? Exclude { get; init; }
    /// <summary>
    /// Узел «Весь звук системы»: не исключать приложения, у которых на схеме свой узел (по умолчанию исключаются,
    /// иначе их слышно дважды). Нужен, когда узел приложения и «Весь звук системы» ведут в разные выходы.
    /// </summary>
    public bool? IncludeOwnNodes { get; init; }

    public float GainDb { get; init; }
    public bool Muted { get; init; }
    /// <summary>Только у источников.</summary>
    public bool? Mono { get; init; }
    /// <summary>Только у источников: −1 … +1.</summary>
    public float? Balance { get; init; }

    /// <summary>Обработка перед фейдером: гейт → EQ → компрессор (null — не настраивалась).</summary>
    public ChainSettings? Chain { get; init; }

    public double X { get; init; }
    public double Y { get; init; }
    public string? Color { get; init; }

    /// <summary>Источник (левая колонка): только выходной порт.</summary>
    [JsonIgnore] public bool IsSource => Kind is not (NodeKind.DeviceOutput or NodeKind.Bus);
    /// <summary>Выход (правая колонка): только входной порт.</summary>
    [JsonIgnore] public bool IsOutput => Kind == NodeKind.DeviceOutput;
    [JsonIgnore] public bool IsBus => Kind == NodeKind.Bus;
    /// <summary>Из узла можно вести провод: источник или шина.</summary>
    [JsonIgnore] public bool HasOutPort => !IsOutput;
    /// <summary>В узел можно вести провод: шина или выход.</summary>
    [JsonIgnore] public bool HasInPort => !IsSource;
    [JsonIgnore] public NodeColumn Column => IsSource ? NodeColumn.Sources : IsBus ? NodeColumn.Buses : NodeColumn.Outputs;
    /// <summary>Включён хотя бы один блок обработки.</summary>
    [JsonIgnore] public bool HasChain => Chain is { IsActive: true };
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
    /// <summary>2 — v2: шины, обработка. Файл v1 читается как есть (новых полей в нём нет).</summary>
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;
    /// <summary>Эндпоинт-заглушка для режима «Забрать себе» (этап 2).</summary>
    public string? SinkEndpoint { get; init; }
    /// <summary>Журнал приложений с привязкой к заглушке (этап 2).</summary>
    public List<string> BoundApps { get; init; } = [];
    /// <summary>
    /// Журнал приложений, которым микшер менял громкость сессии (прямой маршрут). Windows хранит её за приложением и
    /// устройством, поэтому после сбоя микшера громкость возвращается на 100% при следующем запуске.
    /// </summary>
    public List<string> VolumeApps { get; init; } = [];
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
        // значения обработки — в пределах спецификации, сколько бы ни написали в файл руками
        if (g.Nodes.Any(n => n.Chain is not null))
            g = g with { Nodes = g.Nodes.Select(n => n.Chain is null ? n : n with { Chain = n.Chain.Normalized() }).ToList() };
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
