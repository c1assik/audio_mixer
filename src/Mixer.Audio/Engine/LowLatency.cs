using System.Diagnostics;
using System.Runtime.InteropServices;
using Mixer.Audio.Interop;

namespace Mixer.Audio.Engine;

public enum LowLatencyStatus
{
    /// <summary>Режим не просили: обычный Initialize, как в v1.</summary>
    Off,
    /// <summary>Поток открыт через IAudioClient3 с периодом меньше обычного.</summary>
    Active,
    /// <summary>Драйвер не даёт периодов меньше обычного: поток открыт как в v1.</summary>
    Unsupported,
    /// <summary>IAudioClient3 нет или InitializeSharedAudioStream отказал (движок занят другим периодом): поток открыт как в v1.</summary>
    Unavailable,
    /// <summary>С малым периодом было больше <see cref="GlitchWindow.Limit"/> сбоев за минуту: узел вернулся на обычный период.</summary>
    FellBack,
}

/// <summary>
/// Что вышло с режимом «Низкая задержка» у узла. Периоды — в кадрах на частоте устройства.
/// DefaultFrames — обычный период движка (10 мс), MinFrames — наименьший, который даёт драйвер.
/// </summary>
public sealed record LowLatencyInfo(LowLatencyStatus Status, int SampleRate = 0, int PeriodFrames = 0, int MinFrames = 0, int DefaultFrames = 0)
{
    public static readonly LowLatencyInfo Off = new(LowLatencyStatus.Off);

    /// <summary>Ошибка IAudioClient3 (для Unavailable).</summary>
    public string? Error { get; init; }

    public double PeriodMs => Ms(PeriodFrames);
    public double MinMs => Ms(MinFrames);
    public double DefaultMs => Ms(DefaultFrames);

    private double Ms(int frames) => SampleRate > 0 ? frames * 1000.0 / SampleRate : 0;
}

/// <summary>
/// Счётчик сбоев для автоотката: больше Limit сбоев за WindowSeconds — пора возвращаться на обычный период.
/// Без аллокаций после создания, вызывается из аудиопотока.
/// </summary>
public sealed class GlitchWindow
{
    /// <summary>Порог из спецификации: больше 3 сбоев за минуту.</summary>
    public const int Limit = 3;
    public const double WindowSeconds = 60;

    private readonly long[] _times = new long[Limit];
    private readonly long _window;
    private int _count;
    private int _next;

    public GlitchWindow(long ticksPerSecond = 0)
    {
        _window = (long)(WindowSeconds * (ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency));
    }

    /// <summary>Отметить сбой. true — это уже (Limit + 1)-й сбой за окно.</summary>
    public bool Add(long ticks)
    {
        // в кольце — время Limit последних сбоев; самый старый из них лежит в _next
        bool exceeded = _count == Limit && ticks - _times[_next] <= _window;
        _times[_next] = ticks;
        _next = (_next + 1) % Limit;
        if (_count < Limit) _count++;
        return exceeded;
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
    }
}

/// <summary>Поток с малым периодом сбоит: узел должен открыться заново с обычным периодом.</summary>
internal sealed class LowLatencyFallbackException() : Exception("low latency fell back");

/// <summary>Открытие потока через IAudioClient3::InitializeSharedAudioStream с наименьшим периодом движка.</summary>
internal static class LowLatencyStream
{
    /// <summary>
    /// Попробовать открыть поток с малым периодом. Возвращает (формат, сведения); формат null — поток не открыт,
    /// и надо открыть обычным Initialize. needNewClient — Initialize уже вызывался и отказал: этот клиент больше
    /// не годится, нужен новый (так требует WASAPI после неудачного Initialize).
    /// </summary>
    public static (WaveFormat? Format, LowLatencyInfo Info) TryInitialize(IAudioClient client, uint flags, string what, out bool needNewClient)
    {
        needNewClient = false;
        if (client is not IAudioClient3 c3)
            return (null, new LowLatencyInfo(LowLatencyStatus.Unavailable) { Error = "IAudioClient3" });

        AudioException.Check(client.GetMixFormat(out IntPtr pMix), what, "GetMixFormat");
        try
        {
            var format = WaveFormat.FromPointer(pMix);
            if (format.Type == SampleType.Unsupported) return (null, LowLatencyInfo.Off); // обычный путь сам объяснит
            int hr = c3.GetSharedModeEnginePeriod(pMix, out uint def, out _, out uint min, out _);
            if (hr < 0)
                return (null, new LowLatencyInfo(LowLatencyStatus.Unavailable, format.SampleRate) { Error = "GetSharedModeEnginePeriod: " + Hr.Describe(hr) });
            if (min == 0 || min >= def)
                return (null, new LowLatencyInfo(LowLatencyStatus.Unsupported, format.SampleRate, (int)def, (int)min, (int)def));

            hr = c3.InitializeSharedAudioStream(flags, min, pMix, IntPtr.Zero);
            if (hr < 0)
            {
                needNewClient = true;
                return (null, new LowLatencyInfo(LowLatencyStatus.Unavailable, format.SampleRate, (int)def, (int)min, (int)def)
                {
                    Error = "InitializeSharedAudioStream: " + Hr.Describe(hr),
                });
            }
            // фактический период: если движок устройства уже держит другой малый период, он и останется
            uint period = min;
            if (c3.GetCurrentSharedModeEnginePeriod(out IntPtr pCur, out uint cur) >= 0)
            {
                Marshal.FreeCoTaskMem(pCur);
                if (cur > 0) period = cur;
            }
            var status = period < def ? LowLatencyStatus.Active : LowLatencyStatus.Unsupported;
            return (format, new LowLatencyInfo(status, format.SampleRate, (int)period, (int)min, (int)def));
        }
        finally
        {
            Marshal.FreeCoTaskMem(pMix);
        }
    }
}
