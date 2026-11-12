using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mixer.Audio.Interop;
using Mixer.Core.Config;

namespace Mixer.Core.Cables;

/// <summary>
/// Модель виртуального кабеля из таблицы сопоставления: по имени драйвера и описанию устройства связывает концы
/// кабеля — устройство воспроизведения (Input, в него играют) и устройство записи (Output, из него читают).
/// </summary>
public sealed record CableModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Vendor { get; init; } = "";
    /// <summary>Имя драйвера в скобках после описания устройства («VB-Audio Virtual Cable»); * — любые символы.</summary>
    public required string Driver { get; init; }
    /// <summary>Описания устройств воспроизведения этого кабеля, основное первым («CABLE Input», «CABLE In 16ch»).</summary>
    public List<string> Inputs { get; init; } = [];
    /// <summary>Описания устройств записи («CABLE Output»).</summary>
    public List<string> Outputs { get; init; } = [];
    /// <summary>Концы кабеля называются одинаково, а кабелей у драйвера несколько (Virtual Audio Cable: «Line 1», «Line 2»).</summary>
    public bool PairByName { get; init; }
    /// <summary>Официальный сайт: помощник даёт на него ссылку, сам кабель в сборку не входит.</summary>
    public string? Url { get; init; }
}

/// <summary>Кабель, найденный среди устройств: два конца (у VB-Cable входов два — «CABLE Input» и «CABLE In 16ch»).</summary>
/// <param name="Id">ID пары: ID модели, у кабелей «по имени» — ещё и описание («vac:line 1»), у связанных вручную — «manual:…».</param>
/// <param name="Name">Короткое имя кабеля: «CABLE-A», «Line 1».</param>
/// <param name="Inputs">Устройства воспроизведения кабеля, основное первым.</param>
/// <param name="Output">Устройство записи кабеля.</param>
public sealed record CablePair(string Id, string Name, CableModel? Model, IReadOnlyList<EndpointInfo> Inputs, EndpointInfo? Output)
{
    /// <summary>Основной вход кабеля: в него микшер пишет, когда кабель — виртуальный микрофон.</summary>
    public EndpointInfo? Input => Inputs.Count > 0 ? Inputs[0] : null;

    /// <summary>Оба конца на месте: кабелем можно пользоваться.</summary>
    public bool Complete => Input is not null && Output is not null;

    /// <summary>«CABLE (VB-Audio)».</summary>
    public string Label => Model is { Vendor.Length: > 0 } m ? $"{Name} ({m.Vendor})" : Name;
}

/// <summary>
/// Таблица сопоставления кабелей (v2, «Виртуальные кабели»). Встроенная таблица — ресурс сборки (cables.json),
/// обновляется вместе с программой. Файл cables.json рядом с Mixer.exe добавляет свои записи, они проверяются раньше
/// встроенных.
/// </summary>
public sealed class CableTable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record FileData(List<CableModel>? Cables);

    private readonly List<(CableModel Model, Regex Driver, Regex[] Inputs, Regex[] Outputs)> _models;

    public CableTable(IEnumerable<CableModel> models)
    {
        _models = models.Where(m => !string.IsNullOrWhiteSpace(m.Id) && !string.IsNullOrWhiteSpace(m.Driver))
            .Select(m => (m, Glob(m.Driver), m.Inputs.Select(Glob).ToArray(), m.Outputs.Select(Glob).ToArray()))
            .ToList();
    }

    public IReadOnlyList<CableModel> Models => _models.Select(m => m.Model).ToList();

    /// <summary>Встроенная таблица.</summary>
    public static CableTable Builtin { get; } = new(ReadBuiltin());

    private static CableTable? _default;

    /// <summary>Встроенная таблица плюс cables.json рядом с программой (если есть и читается).</summary>
    public static CableTable Default
    {
        get => _default ??= LoadWithExtra(Path.Combine(AppContext.BaseDirectory, "cables.json"), out _);
        set => _default = value;
    }

    /// <summary>Свои записи из файла (раньше встроенных) + встроенные. Файла нет или он битый — только встроенные, error объясняет.</summary>
    public static CableTable LoadWithExtra(string path, out string? error)
    {
        error = null;
        var extra = new List<CableModel>();
        try
        {
            if (File.Exists(path)) extra = Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
        }
        return new CableTable(extra.Concat(Builtin.Models));
    }

    public static List<CableModel> Parse(string json) => JsonSerializer.Deserialize<FileData>(json, Json)?.Cables ?? [];

    private static List<CableModel> ReadBuiltin()
    {
        using var s = typeof(CableTable).Assembly.GetManifestResourceStream("Mixer.Core.Cables.cables.json")
                      ?? throw new InvalidOperationException("cables.json resource missing");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>«CABLE Input (VB-Audio Virtual Cable)» → («CABLE Input», «VB-Audio Virtual Cable»).</summary>
    public static (string Description, string? Driver) Split(string endpointName)
    {
        string? driver = Graph.FeedbackRules.DriverName(endpointName);
        if (driver is null) return (endpointName.Trim(), null);
        int open = endpointName.LastIndexOf('(');
        return (endpointName[..open].Trim(), driver);
    }

    /// <summary>Чей это конец кабеля: модель, вход ли это (устройство воспроизведения) и описание устройства.</summary>
    public (CableModel Model, bool IsInput, string Description, int Rank)? Match(string endpointName, EDataFlow flow)
    {
        var (desc, driver) = Split(endpointName);
        if (driver is null) return null;
        bool input = flow == EDataFlow.Render;
        foreach (var (model, drv, inputs, outputs) in _models)
        {
            if (!drv.IsMatch(driver)) continue;
            var patterns = input ? inputs : outputs;
            for (int i = 0; i < patterns.Length; i++)
                if (patterns[i].IsMatch(desc)) return (model, input, desc, i);
        }
        return null;
    }

    public (CableModel Model, bool IsInput, string Description, int Rank)? Match(EndpointInfo d) => Match(d.Name, d.Flow);

    /// <summary>ID пары для устройства этого кабеля.</summary>
    public string? PairIdOf(string endpointName, EDataFlow flow) =>
        Match(endpointName, flow) is { } m ? PairId(m.Model, m.Description) : null;

    private static string PairId(CableModel model, string description) =>
        model.PairByName ? $"{model.Id}:{description.ToLowerInvariant()}" : model.Id;

    /// <summary>Кабели среди устройств, в порядке таблицы. Кабель попадает в список, даже если найден один его конец.</summary>
    public IReadOnlyList<CablePair> Pairs(IReadOnlyList<EndpointInfo> devices)
    {
        var found = new Dictionary<string, (CableModel Model, string Name, List<(EndpointInfo D, int Rank)> Inputs, EndpointInfo? Output, int Order)>();
        int order = 0;
        foreach (var d in devices)
        {
            if (Match(d) is not { } m) continue;
            string id = PairId(m.Model, m.Description);
            if (!found.TryGetValue(id, out var p))
            {
                int modelOrder = _models.FindIndex(x => x.Model == m.Model);
                p = (m.Model, m.Model.PairByName ? m.Description : m.Model.Name, [], null, modelOrder * 1000 + order++);
            }
            if (m.IsInput) p.Inputs.Add((d, m.Rank));
            else p.Output ??= d;
            found[id] = p;
        }
        return found.OrderBy(kv => kv.Value.Order)
            .Select(kv => new CablePair(kv.Key, kv.Value.Name, kv.Value.Model,
                kv.Value.Inputs.OrderBy(x => x.Rank).ThenBy(x => x.D.Name, StringComparer.Ordinal).Select(x => x.D).ToList(), kv.Value.Output))
            .ToList();
    }

    /// <summary>
    /// Кабель узла: связанный вручную (у узла записан второй конец) или по таблице. null — узел не на кабеле.
    /// </summary>
    public CablePair? PairOf(NodeConfig node, IReadOnlyList<EndpointInfo> devices)
    {
        if (!node.HasDevice || node.FollowsDefault) return null;
        var flow = node.Kind == NodeKind.DeviceInput ? EDataFlow.Capture : EDataFlow.Render;
        var self = Graph.GraphBuilder.Resolve(node, devices);
        if (node.Cable is { PartnerId: { Length: > 0 } partnerId } manual)
        {
            var partner = devices.FirstOrDefault(d => d.Flow != flow && string.Equals(d.Id, partnerId, StringComparison.OrdinalIgnoreCase))
                          ?? devices.FirstOrDefault(d => d.Flow != flow && d.Name == manual.PartnerName);
            string name = self is not null ? Split(self.Name).Description : node.Name;
            var inputs = new List<EndpointInfo>();
            var input = flow == EDataFlow.Render ? self : partner;
            if (input is not null) inputs.Add(input);
            return new CablePair(manual.PairId, name, null, inputs, flow == EDataFlow.Capture ? self : partner);
        }
        string? endpointName = self?.Name ?? node.EndpointName;
        if (endpointName is null || PairIdOf(endpointName, flow) is not { } id) return null;
        return Pairs(devices).FirstOrDefault(p => p.Id == id)
               ?? new CablePair(id, Match(endpointName, flow)!.Value.Model.Name, Match(endpointName, flow)!.Value.Model, [], null);
    }

    /// <summary>
    /// Связь с кабелем, которую узел получает сам по таблице: выход в Input кабеля — виртуальный микрофон (микшер пишет,
    /// приложение читает), вход из Output кабеля — виртуальный вход (приложение играет, микшер читает).
    /// </summary>
    public CableLink? Detect(NodeConfig node)
    {
        if (node.FollowsDefault || node.EndpointName is not { Length: > 0 } name) return null;
        return node.Kind switch
        {
            NodeKind.DeviceOutput when PairIdOf(name, EDataFlow.Render) is { } id => new CableLink { PairId = id, Role = CableRole.VirtualMic },
            NodeKind.DeviceInput when PairIdOf(name, EDataFlow.Capture) is { } id => new CableLink { PairId = id, Role = CableRole.VirtualInput },
            _ => null,
        };
    }

    /// <summary>Связь узла с кабелем: записанная в узле или найденная по таблице.</summary>
    public CableLink? LinkOf(NodeConfig node) => node.HasDevice && !node.FollowsDefault ? node.Cable ?? Detect(node) : null;
}
