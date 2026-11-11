using Mixer.App.ViewModels;
using Mixer.Audio.Dsp;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Cables;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Graph;
using Xunit.Abstractions;
using static Mixer.Tests.CableTests;

namespace Mixer.Tests;

/// <summary>
/// Этап 8 (v2): критерии приёмки выпуска 2.1, которые проверяются без звуковой карты. Остальное (игры с античитом,
/// перезагрузка с автозапуском) — на устройстве, см. docs/DEVELOPMENT.md, «v2 stage 8».
/// </summary>
public class AcceptanceV21Tests(ITestOutputHelper output)
{
    private const int Rate = 48000;

    /// <summary>
    /// Критерий: «Помощник на чистой системе с одним VB-Cable собирает рабочую схему «стрим в OBS» менее чем за
    /// 2 минуты и сам назначает OBS микрофон». Здесь — путь по помощнику (четыре нажатия: «Далее», «Стрим», снять
    /// «Только наушники», «Далее», «Собрать схему»), проверка схемы и назначение микрофона OBS на модели
    /// IAudioPolicyConfig.
    /// </summary>
    [Fact]
    public void Setup_builds_a_working_stream_to_obs_graph_and_assigns_the_obs_microphone()
    {
        WpfThread.Run(() =>
        {
            var clean = new List<EndpointInfo> { Hp, Mic, CableIn, CableOut }; // чистая система: одни наушники, микрофон и VB-Cable
            var (vm, store) = UiCableTests.Make(new GraphConfig(), clean);
            using var _ = store;
            vm.OfferSetup(); // первый запуск с пустой схемой
            var s = vm.Setup!;
            int clicks = 0;
            void Click(Action a)
            {
                a();
                clicks++;
            }
            Click(() => s.NextCommand.Execute(null)); // наушники и микрофон выбраны сами
            Click(() => s.Stream = true);
            Click(() => s.HeadphonesOnly = false);
            Assert.False(s.CablesShort);
            Click(() => s.NextCommand.Execute(null));
            Click(() => s.FinishCommand.Execute(null));
            Assert.Equal(5, clicks);

            var g = vm.Graph;
            var stream = g.Nodes.Single(n => n.Cable?.Role == CableRole.VirtualMic);
            Assert.Equal(CableIn.Id, stream.EndpointId);
            Assert.Equal(["obs64.exe"], stream.MicFor!);
            var mic = g.Nodes.Single(n => n.Kind == NodeKind.DeviceInput);
            var sys = g.Nodes.Single(n => n.Kind == NodeKind.System);
            Assert.Contains(g.Wires, w => w.From == mic.Id && w.To == stream.Id);
            Assert.Contains(g.Wires, w => w.From == sys.Id && w.To == stream.Id);

            // рабочая схема: строится без ошибок и петель, микрофон и звук системы идут в кабель
            var build = GraphBuilder.Build(g, clean);
            Assert.DoesNotContain(build.Issues, i => i.Kind is IssueKind.Invalid or IssueKind.Feedback or IssueKind.Unsupported or IssueKind.SinkConflict);
            Assert.Contains(build.Graph.Outputs, o => o.EndpointId == CableIn.Id);

            // назначение микрофона: OBS читает выход того же кабеля (как это делает хост на каждом такте)
            string? target = CableTable.Default.PairOf(stream, clean)?.Output?.Id;
            Assert.Equal(CableOut.Id, target);
            var world = World.Make([new(1, 0, "explorer.exe"), new(500, 1, "obs64.exe"), new(World.Self, 1, "Mixer.exe")]);
            var policy = new FakePolicy(() => world.Processes);
            var journal = new FakeJournal();
            var micManager = new TakeOverManager(policy, journal, checkPlayback: false);
            micManager.Reconcile(stream.MicFor!.ToDictionary(e => e, _ => target), world);
            Assert.True(policy.IsBound("obs64.exe"));
            Assert.Equal(CableOut.Id, policy.Bindings[("obs64.exe", ERole.Console)]);
            Assert.Equal(["obs64.exe"], journal.List);
            Assert.Contains("OBS Studio", vm.Toast); // и подсказка выбрать в OBS «По умолчанию»
            vm.Dispose();
        });
    }

    // ── смена сцены без щелчков ──

    /// <summary>Остаток выше 6 кГц (четыре ФВЧ Баттерворта): скачок уровня даёт широкий спектр, плавная модуляция — нет.</summary>
    private static double ClickDb(float[] stereo, int from)
    {
        var hp = new StereoBiquad[4];
        for (int i = 0; i < hp.Length; i++) hp[i].C = BiquadCoeffs.Design(BiquadKind.HighPass, 6000, 0, 0.70710678, Rate);
        var buf = (float[])stereo.Clone();
        for (int i = 0; i < hp.Length; i++) hp[i].Process(buf, buf.Length / 2);
        float peak = 0;
        for (int i = from * 2; i < buf.Length; i++) peak = MathF.Max(peak, MathF.Abs(buf[i]));
        return 20 * Math.Log10(Math.Max(peak, 1e-12));
    }

    /// <summary>
    /// Синус 1 кГц через усиление узла, как в движке (рампа 10 мс на каждое новое значение, mute — 5 мс), а значения
    /// приходят из плавного перехода сцены с шагом таймера интерфейса 15 мс. Возвращает остаток выше 6 кГц
    /// относительно сигнала и время до последнего значения.
    /// </summary>
    private (double ClickDb, double DoneMs) Switch(NodeConfig from, NodeConfig to)
    {
        var a = new GraphConfig { Nodes = [from] };
        var b = new GraphConfig { Nodes = [to] };
        var input = Signal.Sine(1000, Rate, Rate, amplitude: 0.5);
        var y = (float[])input.Clone();
        var gain = new StereoGain();
        var (l0, r0) = StereoGain.Targets(from.GainDb, 0);
        gain.Init(Rate, l0, r0, from.Muted);
        const int Block = Rate / 100; // период устройства 10 мс
        double startMs = 200, tickMs = 15, done = 0;
        double nextTick = startMs;
        for (int f = 0; f < input.Length / 2; f += Block)
        {
            double now = f * 1000.0 / Rate;
            while (now >= nextTick)
            {
                double t = (nextTick - startMs) / Scenes.FadeMs;
                var n = Scenes.Interpolate(a, b, t).Nodes[0];
                var (l, r) = StereoGain.Targets(n.GainDb, 0);
                gain.SetTargets(l, r, n.Muted);
                if (t >= 1 && done == 0) done = nextTick - startMs;
                nextTick += tickMs;
            }
            gain.Apply(y.AsSpan(f * 2, Block * 2), Block);
        }
        double db = ClickDb(y, Rate / 10) - 20 * Math.Log10(0.5);
        return (db, done);
    }

    [Fact]
    public void Scene_switch_takes_150_ms_without_clicks()
    {
        var n = new NodeConfig { Id = "n", Kind = NodeKind.DeviceInput, Name = "Микрофон" };
        var cases = new (string What, NodeConfig From, NodeConfig To)[]
        {
            ("0 → −20 дБ", n, n with { GainDb = -20 }),
            ("−20 → +6 дБ", n with { GainDb = -20 }, n with { GainDb = 6 }),
            ("mute", n, n with { Muted = true }),
            ("unmute", n with { Muted = true }, n with { GainDb = -3 }),
        };
        foreach (var (what, from, to) in cases)
        {
            var (db, ms) = Switch(from, to);
            output.WriteLine($"{what}: остаток выше 6 кГц {db:F1} дБ, переход {ms:F0} мс");
            Assert.InRange(ms, Scenes.FadeMs, Scenes.FadeMs + 15); // последний шаг — на первом такте после 150 мс
            Assert.True(db < -60, $"{what}: {db:F1} dB");
        }

        // для сравнения: та же правка мгновенно (без плавного перехода) — щелчок виден
        var instant = new StereoGain();
        instant.Init(Rate, 1, 1, false);
        var x = Signal.Sine(1000, Rate, Rate / 2, amplitude: 0.5);
        for (int f = 0; f < x.Length / 2; f += 480)
        {
            if (f == Rate / 4) instant = MakeInstant(0.1f);
            instant.Apply(x.AsSpan(f * 2, 960), 480);
        }
        Assert.True(ClickDb(x, Rate / 10) - 20 * Math.Log10(0.5) > -60);
    }

    private static StereoGain MakeInstant(float g)
    {
        var s = new StereoGain();
        s.Init(Rate, g, g, false); // без рампы
        return s;
    }
}
