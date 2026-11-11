using Mixer.Audio.Dsp;

namespace Mixer.Audio.Engine;

/// <summary>
/// Экземпляр шины для одного выхода. Шина своих часов не имеет: её считает поток выхода в своём периоде.
/// Если шина идёт в два выхода, у каждого выхода свой экземпляр со своим состоянием обработки.
/// Всё, кроме параметров, трогает только поток выхода.
/// </summary>
public sealed class BusInstance
{
    private readonly float[] _buffer;
    private volatile float _gainL = 1f, _gainR = 1f;
    private volatile bool _muted;
    private StereoGain _gain;
    private int _rate;

    public string BusId { get; }
    /// <summary>Выход, по часам которого считается этот экземпляр.</summary>
    public string OutputId { get; }
    public PeakMeter Peak { get; } = new();
    public ProcessingChain Chain { get; } = new();

    public BusInstance(string busId, string outputId, int maxFrames)
    {
        BusId = busId;
        OutputId = outputId;
        _buffer = new float[Math.Max(1, maxFrames) * 2];
    }

    public void SetGain(float gainDb, float balance, bool muted)
    {
        var (l, r) = StereoGain.Targets(gainDb, balance);
        _gainL = l;
        _gainR = r;
        _muted = muted;
    }

    /// <summary>Сумма шины за текущий период (после обработки и фейдера).</summary>
    internal ReadOnlySpan<float> Output(int frames) => _buffer.AsSpan(0, frames * 2);

    internal int Capacity => _buffer.Length / 2;

    /// <summary>Поток выхода: свести входы шины, обработать, применить фейдер.</summary>
    /// <returns>Сколько проводов с пониженной целью дали underrun в этом блоке (см. <see cref="Wire.Render"/>).</returns>
    internal int Render(MixStep step, Span<float> scratch, int frames, int rate, long nowTicks, double periodSeconds = 0)
    {
        int glitches = 0;
        if (rate != _rate)
        {
            _rate = rate;
            _gain.Init(rate, _gainL, _gainR, _muted);
        }
        var buf = _buffer.AsSpan(0, frames * 2);
        buf.Clear();
        var wires = step.Wires;
        for (int i = 0; i < wires.Length; i++)
            if (wires[i].Render(buf, scratch, frames, rate, nowTicks, periodSeconds)) glitches++;
        var links = step.Links;
        for (int i = 0; i < links.Length; i++) links[i].MixInto(buf, frames, rate);
        Chain.Process(buf, frames, rate);
        _gain.SetTargets(_gainL, _gainR, _muted);
        _gain.Apply(buf, frames);
        Peak.Update(buf, frames);
        return glitches;
    }
}

/// <summary>
/// Провод из шины (в другую шину или в выход) внутри одного выхода: кольца и ресемплера нет, часы те же.
/// Своё усиление с рампой и свой индикатор.
/// </summary>
public sealed class BusLink
{
    private volatile float _target = 1f;
    private volatile bool _enabled = true;
    private StereoGain _gain;
    private int _rate;

    public BusInstance From { get; }
    public string FromId => From.BusId;
    public string ToId { get; }
    public string OutputId => From.OutputId;
    public PeakMeter Peak { get; } = new();

    public BusLink(BusInstance from, string toId)
    {
        From = from;
        ToId = toId;
    }

    public float GainDb { set => _target = Db.ToGain(value); }
    public bool Enabled { get => _enabled; set => _enabled = value; }

    internal void MixInto(Span<float> dst, int frames, int rate)
    {
        float g = _target;
        if (rate != _rate)
        {
            _rate = rate;
            _gain.Init(rate, g, g, !_enabled);
        }
        _gain.SetTargets(g, g, !_enabled);
        if (_gain.IsSilent) return;
        var src = From.Output(frames);
        _gain.MixInto(src, dst, frames);
        Peak.UpdateScaled(src, frames, g * (_enabled ? 1f : 0f));
    }
}

/// <summary>Шаг плана: одна шина и всё, что в неё входит в этом выходе.</summary>
public sealed class MixStep
{
    public required BusInstance Bus { get; init; }
    /// <summary>Провода источник → шина (свои кольца по часам этого выхода).</summary>
    public required Wire[] Wires { get; init; }
    /// <summary>Провода шина → шина: их шины посчитаны на предыдущих шагах.</summary>
    public required BusLink[] Links { get; init; }
}

/// <summary>
/// Что выход сводит в каждом периоде: шины в топологическом порядке, затем прямые провода и провода из шин.
/// Неизменяемый: движок публикует новый план одной ссылкой.
/// </summary>
public sealed class MixPlan
{
    public static readonly MixPlan Empty = new() { Steps = [], Wires = [], Links = [] };

    public required MixStep[] Steps { get; init; }
    /// <summary>Провода источник → выход.</summary>
    public required Wire[] Wires { get; init; }
    /// <summary>Провода шина → выход.</summary>
    public required BusLink[] Links { get; init; }

    /// <summary>Все кольца плана (у прямых проводов и у проводов в шины).</summary>
    public IEnumerable<Wire> AllWires => Wires.Concat(Steps.SelectMany(s => s.Wires));

    /// <summary>Поток выхода: добавить в mix всё, что сводит выход. scratch — рабочий буфер не меньше frames·2.</summary>
    /// <param name="periodSeconds">Период выхода (0 — обычный): от него зависит цель заполнения колец.</param>
    /// <returns>Сколько проводов с пониженной целью дали underrun в этом блоке: сбои «Низкой задержки» выхода.</returns>
    public int Render(Span<float> mix, Span<float> scratch, int frames, int rate, long nowTicks, double periodSeconds = 0)
    {
        int glitches = 0;
        var steps = Steps;
        for (int i = 0; i < steps.Length; i++)
        {
            // блок выхода больше буфера шины не бывает (буфер — MaxBlockFrames), но лучше тишина, чем выход за массив
            if (frames > steps[i].Bus.Capacity) return glitches;
            glitches += steps[i].Bus.Render(steps[i], scratch, frames, rate, nowTicks, periodSeconds);
        }
        var wires = Wires;
        for (int i = 0; i < wires.Length; i++)
            if (wires[i].Render(mix, scratch, frames, rate, nowTicks, periodSeconds)) glitches++;
        var links = Links;
        for (int i = 0; i < links.Length; i++) links[i].MixInto(mix, frames, rate);
        return glitches;
    }
}

/// <summary>Что считает один выход: его шины по порядку и провода, которые до него доходят.</summary>
public sealed record OutputRoute(string OutputId, IReadOnlyList<string> Buses, IReadOnlyList<WireSpec> SourceWires, IReadOnlyList<WireSpec> BusWires);

/// <summary>
/// Раскладка снимка по выходам (без устройств и потоков). Для каждого выхода — шины, из которых до него есть путь,
/// в топологическом порядке, провода от источников в эти шины и в сам выход, провода из этих шин.
/// Шины в цикле и всё, что от них зависит, не считаются (сборка графа циклов не пропускает; это защита).
/// </summary>
public static class MixPlanner
{
    public static IReadOnlyList<OutputRoute> Plan(EngineGraph graph)
    {
        var sources = graph.Sources.Select(s => s.Id).ToHashSet();
        var buses = graph.Buses.Select(b => b.Id).ToHashSet();
        var wires = new List<WireSpec>();
        var seen = new HashSet<(string, string)>();
        foreach (var w in graph.Wires)
            if (w.From != w.To && seen.Add((w.From, w.To))) wires.Add(w);

        var into = new Dictionary<string, List<WireSpec>>();
        foreach (var w in wires)
        {
            if (!into.TryGetValue(w.To, out var list)) into[w.To] = list = [];
            list.Add(w);
        }

        var result = new List<OutputRoute>();
        foreach (var o in graph.Outputs)
        {
            // шины, из которых есть путь в выход (обход назад)
            var reach = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(o.Id);
            while (stack.Count > 0)
            {
                string n = stack.Pop();
                foreach (var w in into.GetValueOrDefault(n, []))
                    if (buses.Contains(w.From) && reach.Add(w.From)) stack.Push(w.From);
            }

            // Кан: шина готова, когда посчитаны все шины, идущие в неё
            var indeg = reach.ToDictionary(b => b, b => into.GetValueOrDefault(b, []).Count(w => reach.Contains(w.From)));
            var order = new List<string>();
            var ready = new Queue<string>(graph.Buses.Select(b => b.Id).Where(b => reach.Contains(b) && indeg[b] == 0));
            while (ready.Count > 0)
            {
                string b = ready.Dequeue();
                order.Add(b);
                foreach (var w in wires)
                {
                    if (w.From != b || !reach.Contains(w.To)) continue;
                    if (--indeg[w.To] == 0) ready.Enqueue(w.To);
                }
            }
            var ok = order.ToHashSet();
            bool Target(string id) => id == o.Id || ok.Contains(id);

            var srcWires = wires.Where(w => sources.Contains(w.From) && Target(w.To)).ToList();
            var busWires = wires.Where(w => ok.Contains(w.From) && Target(w.To)).ToList();
            result.Add(new OutputRoute(o.Id, order, srcWires, busWires));
        }
        return result;
    }
}
