using System.Diagnostics;
using Mixer.Plugins.Host;

namespace Mixer.PluginBench;

public sealed record HostStartInfo(
    string HostExe,
    string? PluginPath,
    int SampleRate,
    int MaxFrames,
    int StallEvery = 0,
    int StallMs = 0,
    TimeSpan? ReadyTimeout = null);

/// <summary>
/// Хост для замера: <see cref="PluginHost"/> из Mixer.Plugins (тот же канал, что у микшера) с плагином
/// или встроенным копированием. Без аллокаций на блоке — <see cref="AudioLink.Process"/>.
/// </summary>
internal sealed class HostChannel : IDisposable
{
    private readonly PluginHost _host;

    public Process HostProcess { get; }
    public TimeSpan StartupTime { get; }
    public int LatencySamples => _host.Audio.LatencySamples;
    public int PluginInChannels => _host.Audio.PluginInChannels;
    public int PluginOutChannels => _host.Audio.PluginOutChannels;

    private HostChannel(HostStartInfo info, double spinMicroseconds)
    {
        var extra = new List<string>();
        if (info.StallEvery > 0)
            extra.AddRange(new[] { "--stall-every", info.StallEvery.ToString(), "--stall-ms", info.StallMs.ToString() });
        var started = Stopwatch.StartNew();
        _host = PluginHost.Start(new PluginHostOptions
        {
            HostExe = info.HostExe,
            SampleRate = info.SampleRate,
            MaxFrames = info.MaxFrames,
            SpinMicroseconds = spinMicroseconds,
            ExtraArgs = extra,
        });
        try
        {
            var load = info.PluginPath is null
                ? PluginLoad.Test("copy", sampleRate: info.SampleRate)
                : PluginLoad.Vst3(info.PluginPath, sampleRate: info.SampleRate);
            _host.LoadAsync(load, info.ReadyTimeout ?? TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch
        {
            _host.Dispose();
            throw;
        }
        StartupTime = started.Elapsed;
        HostProcess = System.Diagnostics.Process.GetProcessById(_host.ProcessId);
    }

    public static HostChannel Start(HostStartInfo info, double spinMicroseconds = 50) => new(info, spinMicroseconds);

    public string Stderr() => _host.StderrTail();

    public BlockOutcome Process(ReadOnlySpan<float> inL, ReadOnlySpan<float> inR, Span<float> outL, Span<float> outR,
        long deadlineQpc, ref BlockTiming t) => _host.Audio.Process(inL, inR, outL, outR, deadlineQpc, ref t);

    public void Dispose()
    {
        _host.Dispose();
        HostProcess.Dispose();
    }
}
