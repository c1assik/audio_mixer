using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Mixer.Plugins.Host;
using Xunit.Abstractions;

namespace Mixer.Tests;

/// <summary>
/// Хост плагинов: где взять Mixer.PluginHost.exe для тестов. Порядок: переменная MIXER_PLUGINHOST, сборка
/// src/Mixer.PluginHost/build/bin, вшитый в Mixer.Plugins (распаковка во временную папку).
/// Без C++-сборки тесты хоста ничего не проверяют, если только не задано MIXER_REQUIRE_PLUGINHOST=1 (CI):
/// тогда отсутствие хоста — провал.
/// </summary>
internal static class TestHost
{
    private static readonly Lazy<string?> s_path = new(Find);

    public static string? Path => s_path.Value;

    /// <summary>Корень репозитория: от папки сборки тестов вверх, иначе — от исходника этого файла (сборка в чужую папку).</summary>
    public static string RepoRoot { get; } = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (string start in new[] { AppContext.BaseDirectory, System.IO.Path.GetDirectoryName(source) ?? "" })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Mixer.sln"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>Пример AGain из SDK, если хост собирали вместе с ним.</summary>
    public static string? AGain
    {
        get
        {
            foreach (string cfg in new[] { "Release", "Debug" })
            {
                string p = System.IO.Path.Combine(RepoRoot, "src", "Mixer.PluginHost", "build", "VST3", cfg, "again.vst3");
                if (Directory.Exists(p)) return p;
            }
            return null;
        }
    }

    private static string? Find()
    {
        string? over = Environment.GetEnvironmentVariable(HostBinary.OverrideVariable);
        if (!string.IsNullOrEmpty(over) && File.Exists(over)) return over;
        string built = System.IO.Path.Combine(RepoRoot, "src", "Mixer.PluginHost", "build", "bin", HostBinary.FileName);
        if (File.Exists(built)) return built;
        if (HostBinary.IsEmbedded)
            return HostBinary.Extract(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mixer-tests-host"));
        return null;
    }

    /// <summary>false — хоста нет и это допустимо (тест молча проходит).</summary>
    public static bool Available()
    {
        if (Path != null) return true;
        if (Environment.GetEnvironmentVariable("MIXER_REQUIRE_PLUGINHOST") == "1")
            throw new InvalidOperationException("Mixer.PluginHost.exe is required (MIXER_REQUIRE_PLUGINHOST=1) but not built");
        return false;
    }

    public static PluginHost Start(ITestOutputHelper? output = null, PluginHostOptions? options = null)
    {
        var o = (options ?? new PluginHostOptions()) with { HostExe = Path };
        if (output != null && o.Log == null)
            o = o with { Log = s => { try { output.WriteLine(s); } catch (InvalidOperationException) { } } };
        return PluginHost.Start(o);
    }

    public static long Deadline(double ms) => Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);

    /// <summary>Дождаться события (через TaskCompletionSource) не дольше timeout.</summary>
    public static async Task<T> Within<T>(Task<T> task, double seconds = 5)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) != task)
            throw new TimeoutException($"no result within {seconds} s");
        return await task;
    }
}

/// <summary>VST, этап 1: хост плагинов — протокол, параметры, состояние, задержка, ровные блоки, падения.</summary>
public class PluginHostTests(ITestOutputHelper output)
{
    private const int Frames = 480;

    private static (float[] L, float[] R) Sine(int frames, int offset = 0)
    {
        var l = new float[frames];
        var r = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            l[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * (i + offset) / 48000f);
            r[i] = 0.25f * MathF.Sin(2 * MathF.PI * 660 * (i + offset) / 48000f);
        }
        return (l, r);
    }

    private static BlockOutcome Run(PluginHost host, float[] inL, float[] inR, float[] outL, float[] outR, double deadlineMs = 1000) =>
        host.Audio.Process(inL, inR, outL, outR, TestHost.Deadline(deadlineMs));

    [Fact]
    public async Task Gain_LoadsProcessesAndMirrorsParameters()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        var info = await host.LoadAsync(PluginLoad.Test("gain"));

        Assert.Equal("Gain", info.Name);
        Assert.Equal(0, info.LatencySamples);
        Assert.Equal(2, info.InChannels);
        Assert.False(info.HasEditor);
        Assert.Equal(2, info.Params.Count);
        Assert.Equal("Gain", info.Params[0].Title);
        Assert.Equal(0.5, info.Params[0].Value);
        Assert.Equal("0.0", info.Params[0].Text);
        Assert.Equal(1u, info.BypassParam?.Id);

        var (l, r) = Sine(Frames);
        var ol = new float[Frames];
        var or = new float[Frames];
        Assert.Equal(BlockOutcome.Processed, Run(host, l, r, ol, or));
        Assert.Equal(l, ol);
        Assert.Equal(r, or);

        var echo = new TaskCompletionSource<PluginParamValue>();
        host.ParamChanged += v => echo.TrySetResult(v);
        Assert.True(host.SetParam(0, 0.25));
        var v = await TestHost.Within(echo.Task);
        Assert.Equal(0u, v.Id);
        Assert.Equal(0.25, v.Value);
        Assert.Equal("-6.0", v.Text);

        Assert.Equal(BlockOutcome.Processed, Run(host, l, r, ol, or));
        for (int i = 0; i < Frames; i++)
        {
            Assert.Equal(l[i] * 0.5f, ol[i]);
            Assert.Equal(r[i] * 0.5f, or[i]);
        }

        // обход силами плагина (kIsBypass): сигнал как есть
        host.SetParam(1, 1);
        await Task.Delay(50);
        Assert.Equal(BlockOutcome.Processed, Run(host, l, r, ol, or));
        Assert.Equal(l, ol);
    }

    [Fact]
    public async Task MonoPlugin_GetsDownmixAndFeedsBothChannels()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        var info = await host.LoadAsync(PluginLoad.Test("gain", new BuiltinOptions { Mono = true }));
        Assert.Equal(1, info.InChannels);
        Assert.Equal(1, info.OutChannels);
        Assert.Equal(1, host.Audio.PluginInChannels);

        var l = Enumerable.Repeat(1.0f, Frames).ToArray();
        var r = Enumerable.Repeat(0.5f, Frames).ToArray();
        var ol = new float[Frames];
        var or = new float[Frames];
        Assert.Equal(BlockOutcome.Processed, Run(host, l, r, ol, or));
        Assert.All(ol, x => Assert.Equal(0.75f, x));
        Assert.All(or, x => Assert.Equal(0.75f, x));
    }

    [Fact]
    public async Task State_RoundTripsBitForBitThroughANewHost()
    {
        if (!TestHost.Available()) return;
        byte[] saved;
        using (var host = TestHost.Start(output))
        {
            await host.LoadAsync(PluginLoad.Test("gain"));
            host.SetParam(0, 0.25);
            saved = await host.GetStateAsync();
        }
        Assert.True(PluginState.TryUnpack(saved, out var component, out var controller));
        Assert.Equal(16, component.Length);
        Assert.Equal(0.25, BitConverter.ToDouble(component, 0));
        Assert.Equal("gain-ui:1", Encoding.ASCII.GetString(controller));

        using var again = TestHost.Start(output);
        var info = await again.LoadAsync(PluginLoad.Test("gain") with { State = saved });
        Assert.Equal(0.25, info.Params[0].Value);
        var (l, r) = Sine(Frames);
        var ol = new float[Frames];
        var or = new float[Frames];
        Assert.Equal(BlockOutcome.Processed, Run(again, l, r, ol, or));
        Assert.Equal(l[100] * 0.5f, ol[100]);

        // состояние компонента то же самое байт в байт; контроллер считает сохранения
        byte[] next = await again.GetStateAsync();
        Assert.True(PluginState.TryUnpack(next, out var component2, out var controller2));
        Assert.Equal(component, component2);
        Assert.Equal("gain-ui:2", Encoding.ASCII.GetString(controller2));
    }

    [Fact]
    public async Task SetState_AtRuntimeReplacesValuesAndReportsThem()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        await host.LoadAsync(PluginLoad.Test("gain"));
        var preset = PluginState.Pack(BitConverter.GetBytes(0.125).Concat(BitConverter.GetBytes(0.0)).ToArray(), "gain-ui:7"u8);
        var list = await host.SetStateAsync(preset);
        Assert.Equal(0.125, list[0].Value);

        var (l, r) = Sine(Frames);
        var ol = new float[Frames];
        var or = new float[Frames];
        Assert.Equal(BlockOutcome.Processed, Run(host, l, r, ol, or));
        Assert.Equal(l[50] * 0.25f, ol[50]);

        await Assert.ThrowsAsync<PluginHostException>(() => host.SetStateAsync(new byte[] { 1, 2, 3 }));
        Assert.True(host.IsRunning);
    }

    [Fact]
    public async Task Latency_IsReportedAndRealAndChangesThroughRestartComponent()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        var info = await host.LoadAsync(PluginLoad.Test("latency"));
        Assert.Equal(512, info.LatencySamples);
        Assert.Equal(512, host.Audio.LatencySamples);

        // импульс в начале: на выходе — ровно через 512 сэмплов
        var output_ = RunImpulse(host, 3 * Frames);
        Assert.Equal(512, Array.IndexOf(output_, 1.0f));

        var changed = new TaskCompletionSource<PluginLayout>();
        host.LayoutChanged += l => changed.TrySetResult(l);
        host.SetParam(0, 256 / 8192.0);
        var layout = await TestHost.Within(changed.Task);
        Assert.Equal(256, layout.LatencySamples);
        Assert.Equal(256, host.Audio.LatencySamples);
        Assert.Equal(256, Array.IndexOf(RunImpulse(host, 2 * Frames), 1.0f));
    }

    /// <summary>Импульс в первом сэмпле, дальше тишина; левый канал выхода.</summary>
    private static float[] RunImpulse(PluginHost host, int total, int[]? sizes = null)
    {
        var outAll = new float[total];
        int pos = 0, k = 0;
        while (pos < total)
        {
            int n = Math.Min(sizes == null ? Frames : sizes[k++ % sizes.Length], total - pos);
            var l = new float[n];
            if (pos == 0) l[0] = 1;
            var ol = new float[n];
            var or = new float[n];
            Assert.Equal(BlockOutcome.Processed, host.Audio.Process(l, l, ol, or, TestHost.Deadline(1000)));
            ol.CopyTo(outAll, pos);
            pos += n;
        }
        return outAll;
    }

    [Fact]
    public async Task EvenBlocks_DelayByExactly256WithRaggedBlocks()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        var info = await host.LoadAsync(PluginLoad.Test("copy") with { EvenBlocks = true });
        Assert.True(info.EvenBlocks);
        Assert.Equal(256, info.LatencySamples);

        // захват приложений отдаёт пакеты разного размера
        int[] sizes = { 100, 333, 7, 480, 1000, 1, 256, 255, 257 };
        int total = 6000;
        var input = new float[total];
        for (int i = 0; i < total; i++) input[i] = (i % 97) / 97f - 0.5f;
        var got = new float[total];
        int pos = 0, k = 0;
        while (pos < total)
        {
            int n = Math.Min(sizes[k++ % sizes.Length], total - pos);
            var l = input.AsSpan(pos, n).ToArray();
            var ol = new float[n];
            var or = new float[n];
            Assert.Equal(BlockOutcome.Processed, host.Audio.Process(l, l, ol, or, TestHost.Deadline(1000)));
            ol.CopyTo(got, pos);
            pos += n;
        }
        for (int i = 0; i < 256; i++) Assert.Equal(0f, got[i]);
        for (int i = 256; i < total; i++) Assert.Equal(input[i - 256], got[i]);

        var layout = await host.SetEvenBlocksAsync(false);
        Assert.Equal(0, layout.LatencySamples);
        Assert.Equal(0, host.Audio.LatencySamples);
    }

    [Fact]
    public async Task SetRate_ReactivatesAndKeepsParameters()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        await host.LoadAsync(PluginLoad.Test("gain"));
        host.SetParam(0, 0.25);
        Assert.Equal(0, await host.SetRateAsync(44100));
        var list = await host.GetParamsAsync();
        Assert.Equal(0.25, list[0].Value);

        var (l, r) = Sine(441);
        var ol = new float[441];
        var or = new float[441];
        Assert.Equal(BlockOutcome.Processed, Run(host, l, r, ol, or));
        Assert.Equal(l[10] * 0.5f, ol[10]);
    }

    [Fact]
    public async Task EditInPluginWindow_ReachesProcessorAndMarksDirty()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        await host.LoadAsync(PluginLoad.Test("gain"));
        var edit = new TaskCompletionSource<PluginParamValue>();
        var dirty = new TaskCompletionSource();
        host.ParamChanged += v => edit.TrySetResult(v);
        host.Dirty += () => dirty.TrySetResult();

        host.SendDebug(new JsonObject { ["op"] = "edit", ["id"] = 0, ["value"] = 0.0 });
        Assert.Equal(0.0, (await TestHost.Within(edit.Task)).Value);
        await TestHost.Within(dirty.Task.ContinueWith(_ => true));

        var (l, r) = Sine(Frames);
        var ol = new float[Frames];
        var or = new float[Frames];
        Assert.Equal(BlockOutcome.Processed, Run(host, l, r, ol, or));
        Assert.All(ol, x => Assert.Equal(0f, x));
    }

    [Fact]
    public async Task Nan_PassesThroughUnchanged()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        await host.LoadAsync(PluginLoad.Test("nan", new BuiltinOptions { After = 0 }));
        var (l, r) = Sine(Frames);
        var ol = new float[Frames];
        var or = new float[Frames];
        Assert.Equal(BlockOutcome.Processed, Run(host, l, r, ol, or));
        Assert.True(float.IsNaN(ol[0]));
    }

    [Fact]
    public async Task LoadFailure_IsReportedAndHostStaysUp()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        var e = await Assert.ThrowsAsync<PluginHostException>(() => host.LoadAsync(PluginLoad.Test("nonsense")));
        Assert.Contains("unknown builtin", e.Message);
        var missing = await Assert.ThrowsAsync<PluginHostException>(() =>
            host.LoadAsync(PluginLoad.Vst3(Path.Combine(Path.GetTempPath(), "no-such-plugin.vst3"))));
        Assert.Contains("cannot load module", missing.Message);
        Assert.True(host.IsRunning);
    }

    [Fact]
    public async Task Crash_InAudioThread_EndsHostWithReason()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        // разогрев — 8 блоков; падает на 13-м
        await host.LoadAsync(PluginLoad.Test("crash", new BuiltinOptions { After = 12 }));
        var (l, r) = Sine(Frames);
        var ol = new float[Frames];
        var or = new float[Frames];
        var outcomes = new List<BlockOutcome>();
        for (int i = 0; i < 6; i++) outcomes.Add(Run(host, l, r, ol, or, 200));
        Assert.Equal(new[] { BlockOutcome.Processed, BlockOutcome.Processed, BlockOutcome.Processed, BlockOutcome.Processed },
            outcomes.Take(4));
        Assert.NotEqual(BlockOutcome.Processed, outcomes[4]);

        var exit = await TestHost.Within(host.Exited);
        Assert.Equal(HostExitReason.Crashed, exit.Reason);
        Assert.Equal(5, exit.ExitCode);
        Assert.StartsWith("crashed: exception 0xC0000005", exit.Message);
        await Assert.ThrowsAsync<PluginHostException>(() => host.GetStateAsync());
    }

    [Fact]
    public async Task Quit_EndsHostCleanly()
    {
        if (!TestHost.Available()) return;
        var host = TestHost.Start(output);
        await host.LoadAsync(PluginLoad.Test("copy"));
        int pid = host.ProcessId;
        host.Dispose();
        var exit = await TestHost.Within(host.Exited);
        Assert.Equal(HostExitReason.Quit, exit.Reason);
        Assert.Equal(0, exit.ExitCode);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public async Task ClosedPipe_MakesHostExitOnItsOwn()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        await host.LoadAsync(PluginLoad.Test("copy"));
        host.AbandonPipeForTest();
        var exit = await TestHost.Within(host.Exited);
        Assert.Equal(0, exit.ExitCode);
    }

    [Fact]
    public void StateFormat_PacksAndRejectsGarbage()
    {
        var blob = PluginState.Pack(new byte[] { 1, 2, 3 }, new byte[] { 9 });
        Assert.Equal(new byte[] { 3, 0, 0, 0, 1, 2, 3, 1, 0, 0, 0, 9 }, blob);
        Assert.True(PluginState.TryUnpack(blob, out var c, out var e));
        Assert.Equal(new byte[] { 1, 2, 3 }, c);
        Assert.Equal(new byte[] { 9 }, e);
        Assert.False(PluginState.TryUnpack(new byte[] { 3, 0, 0, 0, 1 }, out _, out _));
        Assert.False(PluginState.TryUnpack(blob.Concat(new byte[] { 0 }).ToArray(), out _, out _));
    }

    [Fact]
    public void Licenses_IncludeTheVst3SdkMitText()
    {
        var vst = ThirdPartyLicenses.All.Single(l => l.Name == "VST3_SDK.txt");
        Assert.StartsWith("MIT License", vst.Text);
        Assert.Contains("Steinberg Media Technologies GmbH", vst.Text);
        Assert.Contains(ThirdPartyLicenses.All, l => l.Name == "nlohmann_json.txt");
    }

    [Fact]
    public void EmbeddedHost_ExtractsOnceIntoAVersionFolderWithLicenses()
    {
        if (!HostBinary.IsEmbedded)
        {
            if (Environment.GetEnvironmentVariable("MIXER_REQUIRE_PLUGINHOST") == "1")
                throw new InvalidOperationException("Mixer.Plugins was built without the host");
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "mixer-host-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "1.0.0-old"));
            string exe = HostBinary.Extract(root);
            Assert.True(File.Exists(exe));
            Assert.Equal(HostBinary.FileName, Path.GetFileName(exe));
            string dir = Path.GetDirectoryName(exe)!;
            Assert.Matches(@"^\d+\.\d+\.\d+-[0-9a-f]{8}$", Path.GetFileName(dir));
            Assert.StartsWith("MIT License", File.ReadAllText(Path.Combine(dir, "licenses", "VST3_SDK.txt")));
            Assert.False(Directory.Exists(Path.Combine(root, "1.0.0-old")));
            var stamp = File.GetLastWriteTimeUtc(exe);
            Assert.Equal(exe, HostBinary.Extract(root));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(exe));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task AGain_RealVst3_LoadsWithParametersAndState()
    {
        if (!TestHost.Available() || TestHost.AGain is not { } again) return;
        using var host = TestHost.Start(output);
        var info = await host.LoadAsync(PluginLoad.Vst3(again));
        output.WriteLine($"{info.Name} / {info.Vendor} / {info.Version} / {info.ClassId}");
        Assert.Contains("AGain", info.Name);
        Assert.True(info.HasEditor);
        Assert.NotNull(info.ClassId);
        Assert.Contains(info.Params, p => p.Title == "Gain");
        Assert.NotNull(info.BypassParam);

        var (l, r) = Sine(Frames);
        var ol = new float[Frames];
        var or = new float[Frames];
        Assert.Equal(BlockOutcome.Processed, Run(host, l, r, ol, or));
        Assert.Equal(l, ol); // усиление по умолчанию 1,0

        var gain = info.Params.First(p => p.Title == "Gain");
        host.SetParam(gain.Id, 0.25);
        await Task.Delay(100);
        byte[] state = await host.GetStateAsync();

        // тот же класс по GUID, с сохранённым состоянием
        using var second = TestHost.Start(output);
        var info2 = await second.LoadAsync(PluginLoad.Vst3(again, info.ClassId) with { State = state });
        Assert.Equal(0.25, info2.Params.First(p => p.Id == gain.Id).Value, 6);
        Assert.Equal(state, await second.GetStateAsync());
    }
}

/// <summary>Хост плагинов: дедлайн и зависание. Отдельно от остальных: чувствительны ко времени.</summary>
[Collection(nameof(SerialTiming))]
public class PluginHostTimingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SlowPlugin_MissesDeadlineThenIsBusyAndLateAnswersAreDropped()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output);
        // 8 блоков разогрева по 20 мс укладываются в секундный дедлайн
        await host.LoadAsync(PluginLoad.Test("sleep", new BuiltinOptions { SleepMs = 20 }));
        var l = new float[480];
        var ol = new float[480];
        var or = new float[480];
        Assert.Equal(BlockOutcome.Missed, host.Audio.Process(l, l, ol, or, TestHost.Deadline(4)));
        Assert.Equal(BlockOutcome.Busy, host.Audio.Process(l, l, ol, or, TestHost.Deadline(4)));
        await Task.Delay(60);
        // поздний ответ выброшен, новый блок считается заново
        Assert.Equal(BlockOutcome.Processed, host.Audio.Process(l, l, ol, or, TestHost.Deadline(500)));
    }

    [Fact]
    public async Task FrozenMainThread_IsDetectedAsHung()
    {
        if (!TestHost.Available()) return;
        using var host = TestHost.Start(output, new PluginHostOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(250),
            // ответ на ping читается через пул потоков: на загруженной машине он запаздывает до секунды
            HangTimeout = TimeSpan.FromSeconds(2),
        });
        await host.LoadAsync(PluginLoad.Test("copy"));
        await Task.Delay(2500);
        Assert.True(host.IsRunning); // молчать без причины — не зависание: ping отвечается
        var sw = Stopwatch.StartNew();
        host.SendDebug(new JsonObject { ["op"] = "freeze", ["ms"] = 20000 });
        var exit = await TestHost.Within(host.Exited, 6);
        Assert.Equal(HostExitReason.Hung, exit.Reason);
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.0, 4.5); // последний ответ мог прийти с опозданием
    }
}

/// <summary>VST, этап 1: «О программе» показывает лицензии сторонних компонентов.</summary>
public class AboutTests
{
    [Fact]
    public void About_ListsVst3SdkJsonAndOswaldLicenses()
    {
        var rows = Mixer.App.ViewModels.AboutInfo.Licenses;
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Title.StartsWith("VST 3 SDK", StringComparison.Ordinal) && r.Text.StartsWith("MIT License", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Title.StartsWith("JSON for Modern C++", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Title.StartsWith("Oswald", StringComparison.Ordinal) && r.Text.Contains("SIL Open Font License"));
        Assert.Matches(@"^\d+\.\d+\.\d+$", Mixer.App.ViewModels.AboutInfo.Version);
    }
}
