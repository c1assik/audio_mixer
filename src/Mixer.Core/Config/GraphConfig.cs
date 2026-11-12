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

    /// <summary>
    /// Слоты плагинов VST3 (до двух «до» и двух «после» встроенной обработки). Не в <see cref="Chain"/>: цепочку
    /// пересобирают пресеты и нормализация, слоты при этом терять нельзя.
    /// </summary>
    public List<PluginConfig>? Plugins { get; init; }
    /// <summary>Шина с плагинами: по часам какого выхода её считать («Считать по часам…»; null — выбирает движок).</summary>
    public string? ClockOutput { get; init; }

    /// <summary>
    /// «Низкая задержка»: поток устройства через IAudioClient3 с наименьшим периодом, который даёт драйвер
    /// (v2, «Низкая задержка»). null — по умолчанию: у выходов включена, у входов выключена (там она экспериментальная),
    /// у выхода в виртуальный кабель выключена (см. <see cref="LowLatencyFor"/>). Только у входа и выхода устройства.
    /// </summary>
    public bool? LowLatency { get; init; }

    /// <summary>
    /// Связь узла с виртуальным кабелем и его роль (v2, «Виртуальные кабели»). null — по таблице сопоставления, если
    /// устройство узла — конец известного кабеля (<see cref="Cables.CableTable.LinkOf"/>).
    /// </summary>
    public CableLink? Cable { get; init; }
    /// <summary>
    /// Узел-кабель с ролью «виртуальный микрофон»: каким приложениям микшер назначил выход кабеля устройством записи
    /// (IAudioPolicyConfig). Назначение снимается, когда exe убран из списка или узел удалён.
    /// </summary>
    public List<string>? MicFor { get; init; }

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
    [JsonIgnore] public bool HasPlugins => Plugins is { Count: > 0 };
    [JsonIgnore] public bool HasDevice => Kind is NodeKind.DeviceInput or NodeKind.Loopback or NodeKind.DeviceOutput;
    [JsonIgnore] public bool FollowsDefault => DefaultRole is not null && HasDevice;
    /// <summary>У узла есть режим «Низкая задержка»: вход или выход устройства (у loopback период задаёт чужой поток).</summary>
    [JsonIgnore] public bool SupportsLowLatency => Kind is NodeKind.DeviceInput or NodeKind.DeviceOutput;
    /// <summary>«Низкая задержка» с учётом умолчаний для устройства из конфигурации (EndpointName).</summary>
    [JsonIgnore] public bool LowLatencyOn => LowLatencyFor(EndpointName);

    /// <summary>
    /// «Низкая задержка» с учётом умолчаний на этом устройстве: включена у выходов, кроме виртуальных кабелей, выключена у входов.
    /// VB-Cable даёт 128 кадров, но на этапе 1 сам забирал звук на 1% медленнее реального времени и оставлял буфер
    /// пустым тысячи раз в час уже при 10 мс; с очередью в 5 мс такой выход откатывался бы со щелчками на каждом запуске.
    /// </summary>
    public bool LowLatencyFor(string? deviceName) => SupportsLowLatency
        && (LowLatency ?? (Kind == NodeKind.DeviceOutput && !(deviceName is not null && Graph.FeedbackRules.IsVirtualCable(deviceName))));
}

public enum PluginGroup
{
    /// <summary>До встроенной обработки (шумоподавление перед гейтом и компрессором).</summary>
    Pre,
    /// <summary>После встроенной обработки (реверберация на очищенный сигнал).</summary>
    Post,
}

/// <summary>Слот плагина в graph.json. Плагин ищется по <see cref="ClassId"/>, путь — подсказка.</summary>
public sealed record PluginConfig
{
    /// <summary>ID слота внутри узла (p1, p2…).</summary>
    public required string Id { get; init; }
    public PluginGroup Group { get; init; }
    public bool Enabled { get; init; } = true;
    public string Format { get; init; } = "vst3";
    /// <summary>GUID класса (32 hex-цифры).</summary>
    public string? ClassId { get; init; }
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string? Path { get; init; }
    /// <summary>Файл состояния (формат <c>PluginState</c>); относительный путь — от папки graph.json.</summary>
    public string? State { get; init; }
    public bool EvenBlocks { get; init; }
}

/// <summary>Роль виртуального кабеля.</summary>
public enum CableRole
{
    /// <summary>«Виртуальный микрофон»: микшер пишет в Input кабеля, другое приложение (OBS, Discord) читает Output.</summary>
    VirtualMic,
    /// <summary>«Виртуальный вход»: приложение играет в Input кабеля, микшер читает Output.</summary>
    VirtualInput,
}

/// <summary>Связь узла с виртуальным кабелем: ID пары из таблицы сопоставления или связанной вручную.</summary>
public sealed record CableLink
{
    public required string PairId { get; init; }
    public CableRole Role { get; init; }
    /// <summary>Кабель связан вручную (его нет в таблице): ID второго конца.</summary>
    public string? PartnerId { get; init; }
    /// <summary>Имя второго конца (поиск, если ID сменился после переустановки драйвера).</summary>
    public string? PartnerName { get; init; }
}

/// <summary>
/// Сцена — снимок уровней (v2, «Сцены»): фейдеры и mute узлов, усиление и включение проводов, включение блоков
/// обработки. Структуру схемы сцена не меняет: узел или провод, которого в ней нет, при её применении не трогается.
/// </summary>
public sealed record SceneConfig
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Глобальная клавиша сцены («Ctrl+1»).</summary>
    public string? Hotkey { get; init; }
    /// <summary>Уровни узлов по ID.</summary>
    public Dictionary<string, SceneNode> Nodes { get; init; } = new();
    public List<SceneWire> Wires { get; init; } = [];
}

public sealed record SceneNode
{
    public float GainDb { get; init; }
    public bool Muted { get; init; }
    /// <summary>Включение блоков обработки (null — у узла нет цепочки, блок не трогается).</summary>
    public bool? Gate { get; init; }
    public bool? Eq { get; init; }
    public bool? Comp { get; init; }
}

public sealed record SceneWire
{
    public required string From { get; init; }
    public required string To { get; init; }
    public float GainDb { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>Что делает горячая клавиша с узлом.</summary>
public enum HotkeyAction
{
    /// <summary>mute / unmute узла.</summary>
    Mute,
    /// <summary>Уровень узла +1 дБ.</summary>
    VolumeUp,
    /// <summary>Уровень узла −1 дБ.</summary>
    VolumeDown,
    /// <summary>Узел звучит, пока клавиша зажата.</summary>
    PushToTalk,
    /// <summary>Узел молчит, пока клавиша зажата.</summary>
    PushToMute,
}

public sealed record HotkeyConfig
{
    public HotkeyAction Action { get; init; }
    /// <summary>ID узла.</summary>
    public required string Target { get; init; }
    /// <summary>Сочетание: «Ctrl+Shift+M», «F13», «Ctrl+Alt+Numpad1».</summary>
    public required string Keys { get; init; }
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
    /// <summary>
    /// 2 — v2: шины, обработка; 3 — слоты плагинов. Старый файл читается как есть (новых полей в нём нет), схему
    /// версии 3 микшер 2.1 не откроет.
    /// </summary>
    public const int CurrentVersion = 3;

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
    /// <summary>
    /// Журнал приложений, которым микшер назначил кабель микрофоном (micFor). Назначение хранится в Windows за exe,
    /// поэтому exe попадает сюда до первого Set и вычёркивается после сброса — так назначение снимается и после
    /// удаления узла, и после сбоя.
    /// </summary>
    public List<string> MicApps { get; init; } = [];
    /// <summary>Сцены: именованные снимки уровней (v2, «Сцены»).</summary>
    public List<SceneConfig> Scenes { get; init; } = [];
    /// <summary>Текущая сцена (ID): её имя видно в шапке, правка руками помечает её точкой.</summary>
    public string? ActiveScene { get; init; }
    /// <summary>Глобальные горячие клавиши узлов (сцены держат свою клавишу в <see cref="SceneConfig.Hotkey"/>).</summary>
    public List<HotkeyConfig> Hotkeys { get; init; } = [];
    /// <summary>Главный микрофон (ID узла): его состояние показывает значок в трее, его глушит меню трея.</summary>
    public string? MainMic { get; init; }
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
