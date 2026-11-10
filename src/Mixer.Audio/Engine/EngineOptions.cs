using Mixer.Audio.Dsp;

namespace Mixer.Audio.Engine;

/// <summary>Настройки движка. Значения по умолчанию — из спецификации (раздел «Аудиодвижок»).</summary>
public sealed record EngineOptions
{
    /// <summary>Период устройства, мс (WASAPI shared по умолчанию 10 мс). Из него считается цель заполнения колец.</summary>
    public double PeriodMs { get; init; } = 10;

    /// <summary>Буфер воспроизведения WASAPI, мс. Это и есть задержка выхода сверх периода.</summary>
    public double RenderBufferMs { get; init; } = 10;

    /// <summary>
    /// До скольких мс заполнять буфер воспроизведения на каждом периоде: период (10 мс) плюс запас на опоздание потока.
    /// Windows обычно выделяет буфер больше запрошенного; держать его полным — лишняя задержка.
    /// </summary>
    public double RenderQueueMs { get; init; } = 15;

    /// <summary>Буфер захвата WASAPI, мс. Задержку не добавляет: пакеты забираются по событию, как только готовы.</summary>
    public double CaptureBufferMs { get; init; } = 50;

    /// <summary>Логическая ёмкость кольца провода, мс.</summary>
    public double RingMs { get; init; } = 200;

    /// <summary>Заполнение сверх этого — переполнение: лишнее выбрасывается до цели.</summary>
    public double OverflowMs { get; init; } = 150;

    /// <summary>
    /// Сглаженное заполнение выше цели больше чем на столько — сбросить избыток сразу (с фейдом),
    /// а не сгонять его регулятором на ±1000 ppm.
    /// </summary>
    public double ResyncMs { get; init; } = 10;

    /// <summary>Целевое заполнение кольца в периодах выхода.</summary>
    public double TargetPeriods { get; init; } = 2.0;

    public ResamplerKind Resampler { get; init; } = ResamplerKind.Sinc;

    public double Kp { get; init; } = DriftController.DefaultKp;
    public double Ki { get; init; } = DriftController.DefaultKi;
    public double FillSmoothingSeconds { get; init; } = DriftController.DefaultSmoothingSeconds;
    public double MaxPpm { get; init; } = DriftController.DefaultMaxPpm;
    /// <summary>Предел прямой оценки дрейфа в слежении, ppm. Равный MaxPpm — строго как в спецификации.</summary>
    public double MaxDriftPpm { get; init; } = DriftController.DefaultMaxDriftPpm;

    // режим слежения регулятора (см. DriftController)
    public double TrackKp { get; init; } = DriftController.DefaultTrackKp;
    public double TrackKi { get; init; } = DriftController.DefaultTrackKi;
    public double TrackSmoothingSeconds { get; init; } = DriftController.DefaultTrackSmoothingSeconds;
    public double AcquireSeconds { get; init; } = DriftController.DefaultAcquireSeconds;
    /// <summary>Порог сброса избытка в режиме слежения: шире, чем в захвате, чтобы «пила» пакетного источника его не задевала.</summary>
    public double TrackResyncMs { get; init; } = 30;

    /// <summary>Полоса DLL, очищающего время событий от джиттера пробуждений, Гц.</summary>
    public double TimelineBandwidthHz { get; init; } = JitterFilter.DefaultBandwidthHz;

    /// <summary>Регистрировать аудиопотоки в MMCSS как «Pro Audio».</summary>
    public bool UseMmcss { get; init; } = true;

    /// <summary>Пауза перед повторной попыткой открыть устройство после ошибки, мс.</summary>
    public int RetryDelayMs { get; init; } = 2000;

    /// <summary>Наибольший ожидаемый блок выхода в кадрах (для заранее выделенных буферов).</summary>
    public int MaxBlockFrames { get; init; } = 8192;

    /// <summary>Физическая ёмкость кольца: 200 мс при 192 кГц.</summary>
    internal int RingCapacityFrames => (int)Math.Ceiling(192_000 * RingMs / 1000.0);
}
