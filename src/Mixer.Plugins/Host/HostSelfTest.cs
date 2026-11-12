using System.Diagnostics;

namespace Mixer.Plugins.Host;

/// <summary>
/// Проверка, что хост плагинов на месте и работает: распаковать (или найти), запустить, загрузить встроенный
/// «усилитель», прогнать блок и сверить побитово. Ключ <c>Mixer.exe --check-plugin-host</c>; publish.ps1 зовёт его
/// на собранном exe, чтобы убедиться, что хост действительно вшит.
/// </summary>
public static class HostSelfTest
{
    /// <summary>null — всё в порядке, иначе причина.</summary>
    public static async Task<string?> RunAsync(Action<string>? log = null)
    {
        string? exe;
        try
        {
            exe = HostBinary.Locate(log);
        }
        catch (Exception e)
        {
            return $"cannot extract the plug-in host: {e.Message}";
        }
        if (exe == null) return "the plug-in host is not embedded in this build";
        log?.Invoke($"Plug-in host check: {exe}");
        try
        {
            using var host = PluginHost.Start(new PluginHostOptions { HostExe = exe, Log = log });
            var info = await host.LoadAsync(PluginLoad.Test("gain"), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var input = new float[480];
            for (int i = 0; i < input.Length; i++) input[i] = MathF.Sin(i * 0.05f) * 0.5f;
            var outL = new float[480];
            var outR = new float[480];
            var outcome = host.Audio.Process(input, input, outL, outR, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            if (outcome != BlockOutcome.Processed) return $"the plug-in host did not process a block: {outcome}";
            if (!input.AsSpan().SequenceEqual(outL) || !input.AsSpan().SequenceEqual(outR)) return "the plug-in host changed the signal";
            log?.Invoke($"Plug-in host check passed: {info.Name}, pid {host.ProcessId}");
            return null;
        }
        catch (Exception e)
        {
            return $"the plug-in host failed: {e.Message}";
        }
    }
}
