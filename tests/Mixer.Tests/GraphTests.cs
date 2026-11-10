using Mixer.Audio.Engine;
using Mixer.Audio.Interop;
using Mixer.Core.Config;
using Mixer.Core.Graph;

namespace Mixer.Tests;

public class GraphTests
{
    private static readonly EndpointInfo Headphones = new("{0.0.0.00000000}.{hp}", "Headphones (Realtek)", EDataFlow.Render, true);
    private static readonly EndpointInfo CableIn = new("{0.0.0.00000000}.{ci}", "CABLE Input (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo CableIn16 = new("{0.0.0.00000000}.{c16}", "CABLE In 16ch (VB-Audio Virtual Cable)", EDataFlow.Render, false);
    private static readonly EndpointInfo CableAIn = new("{0.0.0.00000000}.{cai}", "CABLE-A Input (VB-Audio Cable A)", EDataFlow.Render, false);
    private static readonly EndpointInfo CableOut = new("{0.0.1.00000000}.{co}", "CABLE Output (VB-Audio Virtual Cable)", EDataFlow.Capture, false);
    private static readonly EndpointInfo Mic = new("{0.0.1.00000000}.{mic}", "Shure MV7", EDataFlow.Capture, true);
    private static readonly EndpointInfo VmOut = new("{0.0.1.00000000}.{vm}", "Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)", EDataFlow.Capture, false);
    private static readonly EndpointInfo VmIn = new("{0.0.0.00000000}.{vmi}", "Voicemeeter Input (VB-Audio Voicemeeter VAIO)", EDataFlow.Render, false);

    private static readonly List<EndpointInfo> Devices = [Headphones, CableIn, CableIn16, CableAIn, CableOut, Mic, VmOut, VmIn];

    private static NodeConfig In(string id, EndpointInfo ep, NodeKind kind = NodeKind.DeviceInput) =>
        new() { Id = id, Kind = kind, Name = id, EndpointId = ep.Id, EndpointName = ep.Name };

    private static NodeConfig Out(string id, EndpointInfo ep) =>
        new() { Id = id, Kind = NodeKind.DeviceOutput, Name = id, EndpointId = ep.Id, EndpointName = ep.Name };

    private static string? Why(NodeConfig src, EndpointInfo srcEp, EndpointInfo outEp) =>
        FeedbackRules.WhyForbidden(src, srcEp, outEp, Devices);

    [Fact]
    public void Loopback_of_output_into_same_output_is_forbidden()
    {
        Assert.NotNull(Why(In("lb", Headphones, NodeKind.Loopback), Headphones, Headphones));
        Assert.Null(Why(In("lb", Headphones, NodeKind.Loopback), Headphones, CableIn));
    }

    [Fact]
    public void Cable_output_into_its_cable_inputs_is_forbidden()
    {
        var src = In("cable", CableOut);
        Assert.NotNull(Why(src, CableOut, CableIn));
        Assert.NotNull(Why(src, CableOut, CableIn16)); // «CABLE In 16ch» идёт в тот же CABLE Output
        Assert.Null(Why(src, CableOut, CableAIn));     // другой кабель
        Assert.Null(Why(src, CableOut, Headphones));
    }

    [Fact]
    public void Microphone_and_voicemeeter_have_no_taps()
    {
        Assert.Null(Why(In("mic", Mic), Mic, CableIn));
        Assert.False(FeedbackRules.IsVirtualCable(VmOut.Name));
        Assert.Null(Why(In("vm", VmOut), VmOut, VmIn));
        Assert.True(FeedbackRules.IsVirtualCable(CableOut.Name));
        Assert.True(FeedbackRules.IsVirtualCable(CableAIn.Name));
    }

    [Fact]
    public void Builder_resolves_by_id_then_by_name()
    {
        var g = new GraphConfig
        {
            Nodes =
            [
                In("mic", Mic) with { EndpointId = "{stale-id}" },  // ID сменился, имя то же
                Out("hp", Headphones),
                new NodeConfig { Id = "gone", Kind = NodeKind.DeviceOutput, Name = "Старые наушники", EndpointId = "{x}", EndpointName = "Нет такого" },
            ],
            Wires = [new() { From = "mic", To = "hp", GainDb = -3 }, new() { From = "mic", To = "gone" }],
        };
        var r = GraphBuilder.Build(g, Devices);
        Assert.Equal(Mic.Id, r.Graph.Sources.Single().EndpointId);
        Assert.Equal(Headphones.Id, r.Graph.Outputs.Single().EndpointId);
        Assert.Equal(new WireSpec("mic", "hp", -3, true), r.Graph.Wires.Single());
        Assert.Contains(r.Issues, i => i.Kind == IssueKind.WaitingDevice && i.Subject == "gone");
    }

    [Fact]
    public void Builder_drops_feedback_and_invalid_wires()
    {
        var g = new GraphConfig
        {
            Nodes = [In("cable", CableOut), In("lb", Headphones, NodeKind.Loopback), Out("ci", CableIn), Out("hp", Headphones)],
            Wires =
            [
                new() { From = "cable", To = "ci" },
                new() { From = "lb", To = "hp" },
                new() { From = "cable", To = "hp" },
                new() { From = "cable", To = "hp" },
                new() { From = "hp", To = "ci" },
            ],
        };
        var r = GraphBuilder.Build(g, Devices);
        Assert.Equal([new WireSpec("cable", "hp")], r.Graph.Wires);
        Assert.Equal(2, r.Issues.Count(i => i.Kind == IssueKind.Feedback));
        Assert.Equal(2, r.Issues.Count(i => i.Kind == IssueKind.Invalid));

        var allowed = GraphBuilder.Build(g, Devices, allowFeedback: true);
        Assert.Equal(3, allowed.Graph.Wires.Count);
    }

    [Fact]
    public void App_node_without_running_process_waits()
    {
        var g = new GraphConfig
        {
            Nodes = [new() { Id = "d", Kind = NodeKind.App, Name = "Discord", Exe = "discord.exe" }, Out("hp", Headphones)],
            Wires = [new() { From = "d", To = "hp" }],
        };
        var r = GraphBuilder.Build(g, Devices);
        Assert.Empty(r.Graph.Sources);
        Assert.Empty(r.Graph.Wires);
        Assert.Contains(r.Issues, i => i.Kind == IssueKind.WaitingProcess && i.Subject == "d");
    }
}

public class ConfigTests
{
    /// <summary>Пример из спецификации, раздел «Конфигурация и устройства».</summary>
    private const string SpecExample = """
    {
      "version": 1,
      "sinkEndpoint": "{0.0.0.00000000}.{guid-cable-b}",
      "boundApps": ["discord.exe"],
      "nodes": [
        { "id": "n1", "kind": "deviceInput", "name": "Микрофон",
          "endpointId": "{0.0.1.00000000}.{guid}", "endpointName": "Shure MV7",
          "gainDb": 0, "muted": false, "mono": true, "x": 40, "y": 60, "color": "#4FC3F7" },
        { "id": "n2", "kind": "app", "name": "Discord", "exe": "discord.exe",
          "takeOver": true, "gainDb": -3, "muted": false, "x": 40, "y": 200 },
        { "id": "o1", "kind": "deviceOutput", "name": "Наушники",
          "endpointId": "{0.0.0.00000000}.{guid}", "endpointName": "Headphones (Realtek)",
          "gainDb": 0, "muted": false, "x": 620, "y": 60 }
      ],
      "wires": [
        { "from": "n2", "to": "o1", "gainDb": 0, "enabled": true }
      ]
    }
    """;

    [Fact]
    public void Spec_example_parses()
    {
        var g = GraphSerializer.Deserialize(SpecExample);
        Assert.Equal(1, g.Version);
        Assert.Equal(["discord.exe"], g.BoundApps);
        Assert.Equal(3, g.Nodes.Count);
        var mic = g.Nodes[0];
        Assert.Equal(NodeKind.DeviceInput, mic.Kind);
        Assert.Equal("Микрофон", mic.Name);
        Assert.True(mic.Mono);
        Assert.Equal("#4FC3F7", mic.Color);
        Assert.Equal(NodeKind.App, g.Nodes[1].Kind);
        Assert.Equal(-3f, g.Nodes[1].GainDb);
        Assert.True(g.Nodes[1].TakeOver);
        Assert.Equal(620, g.Nodes[2].X);
        Assert.Equal("o1", g.Wires.Single().To);
    }

    [Fact]
    public void Roundtrip_is_lossless()
    {
        var g = GraphSerializer.Deserialize(SpecExample);
        string json = GraphSerializer.Serialize(g);
        var back = GraphSerializer.Deserialize(json);
        Assert.Equal(json, GraphSerializer.Serialize(back));
        Assert.Equal(g.Nodes, back.Nodes);
        Assert.Equal(g.Wires, back.Wires);
        Assert.Contains("\"kind\": \"deviceInput\"", json);
        Assert.Contains("Микрофон", json); // без \u-экранирования
    }

    [Fact]
    public void Save_is_atomic_and_replaces()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mixer-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "graph.json");
            var g = GraphSerializer.Deserialize(SpecExample);
            GraphSerializer.Save(path, g);
            GraphSerializer.Save(path, g with { BoundApps = [] });
            Assert.Empty(GraphSerializer.Load(path).BoundApps);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Newer_version_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => GraphSerializer.Deserialize("""{ "version": 3, "nodes": [], "wires": [] }"""));
    }
}
