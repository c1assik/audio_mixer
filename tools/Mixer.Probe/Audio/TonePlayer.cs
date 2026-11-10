using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixer.Probe.Interop;
using Mixer.Probe.Sys;

namespace Mixer.Probe.Audio;

/// <summary>
/// Синус 1 кГц в устройство по умолчанию. Это тестовое «приложение» для ворот этапа 0:
/// с ним проверка не трогает настройки настоящих приложений пользователя.
/// </summary>
public static class TonePlayer
{
    public const double Frequency = 1000;

    /// <summary>Писать сигнал только в FL/FR, остальные каналы — нули.</summary>
    public static bool FrontOnly { get; set; }

    /// <summary>Конкретное устройство вместо устройства по умолчанию (ID или часть имени). null — по умолчанию.</summary>
    public static string? Device { get; set; }

    /// <summary>
    /// Белый шум вместо синуса. Две копии шума со сдвигом складываются по мощности (+3 дБ), а две копии синуса
    /// могут и погасить друг друга, поэтому для замера «двойного звука» нужен шум.
    /// </summary>
    public static bool Noise { get; set; }

    /// <summary>
    /// Перед открытием потока познакомиться с аудиослужбой (без потока), напечатать READY и ждать строку в stdin.
    /// За это время родитель успевает задать привязку IAudioPolicyConfig, и поток сразу открывается на нужном устройстве.
    /// </summary>
    public static bool WaitStdin { get; set; }

    public static int Run(double seconds, double levelDb, bool autoRouting)
    {
        if (Device is not null) autoRouting = false;
        var steps = Stopwatch.StartNew();
        void Step(string what) => Console.WriteLine($"tone: [{steps.ElapsedMilliseconds,5} мс] {what}");

        if (WaitStdin)
        {
            Devices.Sessions(Devices.List(EDataFlow.Render)); // без этого Set/Get для процесса возвращают E_INVALIDARG
            Console.WriteLine($"READY pid={Environment.ProcessId}");
            if (Console.ReadLine() is null) return 3;
        }

        IAudioClient client;
        Step(autoRouting ? "ActivateAudioInterfaceAsync(DEVINTERFACE_AUDIO_RENDER)…" : "IMMDevice.Activate(устройство по умолчанию)…");
        if (autoRouting)
        {
            client = ProcessLoopback.ActivateDefaultRender();
        }
        else
        {
            var all = Devices.List(EDataFlow.Render);
            var def = Device is not null
                ? Devices.Find(all, Device)
                : all.FirstOrDefault(e => e.IsDefault) ?? throw new AudioException("Нет устройства воспроизведения по умолчанию");
            Step($"устройство: {def.Name}");
            client = Devices.ActivateAudioClient(def.Id);
        }

        Step("GetMixFormat…");
        AudioException.Check(client.GetMixFormat(out IntPtr pMix), "GetMixFormat");
        var format = AudioFormat.FromPointer(pMix);
        Step($"Initialize {format}…");
        try
        {
            AudioException.Check(client.Initialize(AudioConst.AUDCLNT_SHAREMODE_SHARED, AudioConst.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                200_000, 0, pMix, IntPtr.Zero), "IAudioClient.Initialize (render)");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pMix);
        }
        Step("Initialize ok");

        int bytesPerSample = format.BlockAlign / format.Channels;
        if (!(format.IsFloat && bytesPerSample == 4) && bytesPerSample != 2 && bytesPerSample != 4)
            throw new AudioException($"Формат микса {format} не поддержан генератором");

        AudioException.Check(client.GetBufferSize(out uint bufferFrames), "GetBufferSize");
        Guid iid = typeof(IAudioRenderClient).GUID;
        AudioException.Check(client.GetService(ref iid, out object svc), "GetService(IAudioRenderClient)");
        var render = (IAudioRenderClient)svc;
        using var evt = new EventWaitHandle(false, EventResetMode.AutoReset);
        AudioException.Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");

        double amplitude = Math.Pow(10, levelDb / 20);
        var noise = new Random(Environment.ProcessId);
        double phase = 0, step = 2 * Math.PI * Frequency / format.SampleRate;
        var clock = Stopwatch.StartNew();

        long prefill = 0;
        Fill(render, client, bufferFrames, format, bytesPerSample, amplitude, ref phase, step, ref prefill, noise);
        Step("Start…");
        AudioException.Check(client.Start(), "IAudioClient.Start (render)");
        Console.WriteLine($"MARK start qpc={Stopwatch.GetTimestamp()}"); // момент первого звука для родителя (newapp)
        Console.WriteLine($"tone: {format}, {Ui.Db(levelDb)} dBFS, маршрутизация {(autoRouting ? "auto" : "fixed")}, PID {Environment.ProcessId}");
        long written = 0;
        int timeouts = 0;
        double nextReport = 1;
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds && !Ui.Cancel.IsSet)
            {
                if (!evt.WaitOne(100)) timeouts++;
                int hr = Fill(render, client, bufferFrames, format, bytesPerSample, amplitude, ref phase, step, ref written, noise);
                if (hr < 0)
                {
                    Console.WriteLine($"tone: поток воспроизведения оборвался: {Hr.Describe(hr)}");
                    return 2;
                }
                if (clock.Elapsed.TotalSeconds >= nextReport)
                {
                    Console.WriteLine($"tone: {Ui.Num(clock.Elapsed.TotalSeconds, "F1")} с, записано кадров за секунду {written}, событий не было {timeouts} раз");
                    written = 0;
                    timeouts = 0;
                    nextReport += 1;
                }
            }
        }
        finally
        {
            client.Stop();
            Marshal.ReleaseComObject(render);
            Marshal.ReleaseComObject(client);
        }
        return 0;
    }

    private static unsafe int Fill(IAudioRenderClient render, IAudioClient client, uint bufferFrames, AudioFormat format,
        int bytesPerSample, double amplitude, ref double phase, double step, ref long written, Random noise)
    {
        int hr = client.GetCurrentPadding(out uint padding);
        if (hr < 0) return hr;
        uint frames = bufferFrames - padding;
        if (frames == 0) return 0;
        written += frames;
        hr = render.GetBuffer(frames, out IntPtr data);
        if (hr < 0) return hr;

        int ch = format.Channels;
        for (int i = 0; i < frames; i++)
        {
            double v = amplitude * Math.Sin(phase);
            phase += step;
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
            for (int c = 0; c < ch; c++)
            {
                int idx = i * ch + c;
                if (Noise) v = amplitude * Math.Sqrt(3) * (2 * noise.NextDouble() - 1); // равномерный шум с RMS = amplitude, свой в каждом канале
                double cv = FrontOnly && c >= 2 ? 0 : v; // как стерео-контент в многоканальном потоке: только FL/FR
                if (format.IsFloat) ((float*)data)[idx] = (float)cv;
                else if (bytesPerSample == 2) ((short*)data)[idx] = (short)(cv * 32767);
                else ((int*)data)[idx] = (int)(cv * int.MaxValue);
            }
        }
        return render.ReleaseBuffer(frames, 0);
    }
}
