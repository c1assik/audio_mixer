using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixer.Audio.Engine;
using Mixer.Audio.Resources;

namespace Mixer.Audio.Interop;

public enum ProbeOutcome { Ok, Failed, Timeout }

public sealed record ProbeResult(ProbeOutcome Outcome, string? Message, double Seconds)
{
    public bool Ok => Outcome == ProbeOutcome.Ok;
}

/// <summary>
/// Пробный поток воспроизведения: принимает ли эндпоинт новые потоки. Нужен перед тем, как назначить устройство заглушкой:
/// на этапе 0 «Voicemeeter In 5» без запущенного Voicemeeter вешал IAudioClient.Initialize, и служба Windows Audio зависала.
/// Проверка идёт в отдельном потоке с таймаутом; зависший поток бросается (он фоновый), и пока он висит,
/// повторная проверка того же устройства сразу отвечает «не отвечает», не открывая второй поток.
/// </summary>
public static class StreamProbe
{
    private static readonly ConcurrentDictionary<string, Thread> Stuck = new(StringComparer.OrdinalIgnoreCase);

    public static ProbeResult TryRender(string endpointId, TimeSpan timeout)
    {
        if (Stuck.TryGetValue(endpointId, out var prev))
        {
            if (prev.IsAlive)
                return new ProbeResult(ProbeOutcome.Timeout, AudioStrings.Probe_StillRunning, 0);
            Stuck.TryRemove(endpointId, out _);
        }

        var sw = Stopwatch.StartNew();
        ProbeResult? result = null;
        string phase = "IMMDevice.Activate";
        var t = new Thread(() =>
        {
            try
            {
                result = Run(endpointId, p => phase = p);
            }
            catch (DeviceUnavailableException ex)
            {
                result = new ProbeResult(ProbeOutcome.Failed, ex.Message, 0);
            }
            catch (Exception ex) when (ex is AudioException or COMException or InvalidCastException)
            {
                result = new ProbeResult(ProbeOutcome.Failed, ex.Message, 0);
            }
        })
        { IsBackground = true, Name = "sink probe" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        if (!t.Join(timeout))
        {
            Stuck[endpointId] = t;
            return new ProbeResult(ProbeOutcome.Timeout,
                Loc.F(AudioStrings.Probe_TimeoutFormat, phase, timeout.TotalSeconds), sw.Elapsed.TotalSeconds);
        }
        return result! with { Seconds = sw.Elapsed.TotalSeconds };
    }

    private static ProbeResult Run(string endpointId, Action<string> phase)
    {
        string What = AudioStrings.Stream_SinkCheck;
        IAudioClient? client = null;
        IAudioRenderClient? render = null;
        bool started = false;
        try
        {
            client = AudioNode.ActivateClient(endpointId);
            phase("IAudioClient.Initialize");
            AudioNode.InitializeShared(client, 0, 100, What);
            AudioException.Check(client.GetBufferSize(out uint frames), What, "GetBufferSize");
            render = AudioNode.GetService<IAudioRenderClient>(client, What);
            phase("IAudioRenderClient.GetBuffer");
            AudioException.Check(render.GetBuffer(frames, out _), What, "GetBuffer");
            AudioException.Check(render.ReleaseBuffer(frames, AudioConst.AUDCLNT_BUFFERFLAGS_SILENT), What, "ReleaseBuffer");
            phase("IAudioClient.Start");
            AudioException.Check(client.Start(), What, "Start");
            started = true;
            // Живой эндпоинт забирает звук каждый период; мёртвый принимает поток, но буфер так и остаётся полным.
            uint padding = frames;
            for (int i = 0; i < 10 && padding >= frames; i++)
            {
                Thread.Sleep(30);
                AudioException.Check(client.GetCurrentPadding(out padding), What, "GetCurrentPadding");
            }
            phase("IAudioClient.Stop");
            return padding >= frames
                ? new ProbeResult(ProbeOutcome.Failed, AudioStrings.Probe_NotConsuming, 0)
                : new ProbeResult(ProbeOutcome.Ok, null, 0);
        }
        finally
        {
            if (started) client!.Stop();
            AudioNode.Release(render);
            AudioNode.Release(client);
        }
    }
}
