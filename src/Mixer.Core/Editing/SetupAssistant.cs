using Mixer.Audio.Interop;
using Mixer.Core.Cables;
using Mixer.Core.Config;

namespace Mixer.Core.Editing;

/// <summary>Приложение, которое помощник держит отдельно: свой узел и своя громкость в наушниках, мимо стрима.</summary>
public sealed record SetupApp(string Exe, string Name);

/// <summary>Ответы помощника настройки.</summary>
public sealed record SetupChoices
{
    /// <summary>Наушники (или колонки): туда идут приложения, которые держат отдельно.</summary>
    public EndpointInfo? Headphones { get; init; }
    /// <summary>Микрофон: идёт в стрим и в звонок с обработкой «Голос — чистый».</summary>
    public EndpointInfo? Microphone { get; init; }
    /// <summary>«Только наушники»: отдельные громкости приложений.</summary>
    public bool HeadphonesOnly { get; init; }
    /// <summary>«Стрим (OBS)»: голос и звук системы одним микрофоном в OBS.</summary>
    public bool Stream { get; init; }
    /// <summary>«Звонок (Discord, Zoom)»: обработанный голос в приложение звонка.</summary>
    public bool Call { get; init; }
    public IReadOnlyList<SetupApp> SeparateApps { get; init; } = [];
    /// <summary>Кому назначить кабель стрима микрофоном (OBS).</summary>
    public IReadOnlyList<string> StreamApps { get; init; } = AppNames.StreamApps.Take(1).ToList();
    /// <summary>Кому назначить кабель звонка микрофоном (Discord и запущенные приложения для звонков).</summary>
    public IReadOnlyList<string> CallApps { get; init; } = ["discord.exe"];
}

/// <summary>
/// Помощник настройки (v2, «Помощник настройки»): по трём ответам собирает готовую схему — узлы, провода, кабели с
/// назначенными микрофонами. Схему потом можно править руками.
/// <list type="bullet">
/// <item>Стрим: «Весь звук системы» и микрофон → кабель «Стрим» (виртуальный микрофон для OBS). В наушниках приложения и так
/// звучат сами, поэтому «Весь звук системы» в наушники не ведётся — иначе было бы слышно дважды.</item>
/// <item>Звонок: микрофон → кабель «Звонок» (виртуальный микрофон для Discord); звонок слышен в наушниках сам.</item>
/// <item>Отдельные приложения: свой узел → наушники. Это прямой маршрут (одна громкость, ноль задержки, без заглушки), а
/// «Весь звук системы» их исключает — в стрим они не попадают.</item>
/// </list>
/// </summary>
public static class SetupAssistant
{
    /// <summary>Сколько кабелей нужно для выбранного: по одному на стрим и на звонок.</summary>
    public static int CablesNeeded(SetupChoices c) => (c.Stream ? 1 : 0) + (c.Call ? 1 : 0);

    /// <summary>Кабели, которыми можно пользоваться (оба конца на месте), в порядке таблицы.</summary>
    public static IReadOnlyList<CablePair> UsableCables(IReadOnlyList<EndpointInfo> devices, CableTable? table = null) =>
        (table ?? CableTable.Default).Pairs(devices).Where(p => p.Complete).ToList();

    /// <summary>
    /// Собрать схему. Узлы, провода и сцены текущей схемы заменяются; заглушка «Забрать себе» и журналы остаются.
    /// Не хватает кабелей — назначения, на которые их не хватило, пропускаются (Missing).
    /// </summary>
    /// <param name="palette">Цвета источников по порядку.</param>
    public static SetupResult Build(GraphConfig current, SetupChoices c, IReadOnlyList<CablePair> cables, IReadOnlyList<string> palette)
    {
        var nodes = new List<NodeConfig>();
        var wires = new List<WireConfig>();
        int sourceRow = 0, outputRow = 0, id = 0;
        string NextId() => "n" + ++id;
        double Row(int i) => GraphLayout.Top + i * (GraphLayout.CardHeight + GraphLayout.Gap);
        string Color() => palette.Count == 0 ? "#4FA3FF" : palette[nodes.Count(n => n.IsSource) % palette.Count];

        NodeConfig AddSource(NodeConfig n)
        {
            n = n with { Id = NextId(), X = GraphLayout.SourceX, Y = Row(sourceRow++), Color = Color() };
            nodes.Add(n);
            return n;
        }

        NodeConfig AddOutput(NodeConfig n)
        {
            n = n with { Id = NextId(), X = GraphLayout.OutputX, Y = Row(outputRow++) };
            nodes.Add(n);
            return n;
        }

        var free = cables.Where(p => p.Complete).ToList();
        int missing = 0;
        CablePair? TakeCable()
        {
            if (free.Count == 0)
            {
                missing++;
                return null;
            }
            var p = free[0];
            free.RemoveAt(0);
            return p;
        }

        bool voice = (c.Stream || c.Call) && c.Microphone is not null;
        NodeConfig? mic = voice
            ? AddSource(new NodeConfig
            {
                Id = "", Kind = NodeKind.DeviceInput, Name = CoreStrings.Setup_NodeMic, EndpointId = c.Microphone!.Id, EndpointName = c.Microphone.Name,
                Chain = ChainPresets.BuiltIn.First(p => p.Id == ChainPresets.VoiceClean).Chain,
            })
            : null;
        NodeConfig? system = c.Stream
            ? AddSource(new NodeConfig { Id = "", Kind = NodeKind.System, Name = CoreStrings.Setup_NodeSystem })
            : null;

        var apps = c.SeparateApps.DistinctBy(a => Sys.ProcessSnapshot.NormalizeExe(a.Exe)).ToList();
        var appNodes = apps.Select(a => AddSource(new NodeConfig
        {
            Id = "", Kind = NodeKind.App, Name = a.Name, Exe = Sys.ProcessSnapshot.NormalizeExe(a.Exe), TakeOver = true,
        })).ToList();

        NodeConfig? headphones = c.Headphones is not null && (c.HeadphonesOnly || appNodes.Count > 0)
            ? AddOutput(new NodeConfig { Id = "", Kind = NodeKind.DeviceOutput, Name = CableTable.Split(c.Headphones.Name).Description, EndpointId = c.Headphones.Id, EndpointName = c.Headphones.Name })
            : null;
        if (headphones is not null)
            foreach (var a in appNodes) wires.Add(new WireConfig { From = a.Id, To = headphones.Id });

        NodeConfig? CableMic(string name, IReadOnlyList<string> micFor)
        {
            if (TakeCable() is not { } pair) return null;
            return AddOutput(new NodeConfig
            {
                Id = "", Kind = NodeKind.DeviceOutput, Name = name, EndpointId = pair.Input!.Id, EndpointName = pair.Input.Name,
                Cable = new CableLink { PairId = pair.Id, Role = CableRole.VirtualMic },
                MicFor = micFor.Select(Sys.ProcessSnapshot.NormalizeExe).Distinct().ToList(),
            });
        }

        if (c.Stream && CableMic(CoreStrings.Setup_NodeStream, c.StreamApps) is { } stream)
        {
            if (mic is not null) wires.Add(new WireConfig { From = mic.Id, To = stream.Id });
            if (system is not null) wires.Add(new WireConfig { From = system.Id, To = stream.Id });
        }
        if (c.Call && CableMic(CoreStrings.Setup_NodeCall, c.CallApps) is { } call && mic is not null)
            wires.Add(new WireConfig { From = mic.Id, To = call.Id });

        var g = current with { Nodes = nodes, Wires = wires, Scenes = [], ActiveScene = null, Hotkeys = [], MainMic = mic?.Id };
        return new SetupResult(g, missing);
    }
}

/// <param name="MissingCables">Сколько кабелей не хватило (назначения без кабеля пропущены).</param>
public sealed record SetupResult(GraphConfig Graph, int MissingCables);
