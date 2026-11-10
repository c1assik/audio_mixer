namespace Mixer.Audio.Dsp;

/// <summary>
/// Гейт: глушит фон в паузах. Открывается, когда огибающая выше порога (за время атаки), держится открытым
/// время удержания после последнего превышения и закрывается до глубины за время спада.
/// </summary>
public sealed record GateSettings
{
    public bool Enabled { get; init; }
    /// <summary>Порог открытия, −80…0 dBFS.</summary>
    public float ThresholdDb { get; init; } = -50;
    /// <summary>Атака (открытие), 0,1–50 мс.</summary>
    public float AttackMs { get; init; } = 1;
    /// <summary>Удержание после последнего превышения порога, 0–500 мс.</summary>
    public float HoldMs { get; init; } = 100;
    /// <summary>Спад (закрытие), 5–1000 мс.</summary>
    public float ReleaseMs { get; init; } = 150;
    /// <summary>Насколько глушить закрытым, −80…0 дБ.</summary>
    public float DepthDb { get; init; } = -40;

    public GateSettings Normalized() => this with
    {
        ThresholdDb = Clamp(ThresholdDb, -80, 0, -50),
        AttackMs = Clamp(AttackMs, 0.1f, 50, 1),
        HoldMs = Clamp(HoldMs, 0, 500, 100),
        ReleaseMs = Clamp(ReleaseMs, 5, 1000, 150),
        DepthDb = Clamp(DepthDb, -80, 0, -40),
    };

    internal static float Clamp(float v, float min, float max, float fallback) => float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}

public enum EqBandType { Off, Peak, LowShelf, HighShelf }

/// <summary>Параметрическая полоса EQ.</summary>
public sealed record EqBand
{
    public EqBandType Type { get; init; }
    /// <summary>20 Гц – 20 кГц.</summary>
    public float FreqHz { get; init; } = 1000;
    /// <summary>±18 дБ.</summary>
    public float GainDb { get; init; }
    /// <summary>0,3–10. У полок задаёт крутизну перехода (как в RBJ Cookbook).</summary>
    public float Q { get; init; } = 1;

    public EqBand Normalized() => this with
    {
        Type = Enum.IsDefined(Type) ? Type : EqBandType.Off,
        FreqHz = GateSettings.Clamp(FreqHz, EqSettings.MinFreq, EqSettings.MaxFreq, 1000),
        GainDb = GateSettings.Clamp(GainDb, -EqSettings.MaxGainDb, EqSettings.MaxGainDb, 0),
        Q = GateSettings.Clamp(Q, EqSettings.MinQ, EqSettings.MaxQ, 1),
    };
}

/// <summary>EQ: фильтр верхних частот (12 дБ/окт) и четыре параметрические полосы.</summary>
public sealed record EqSettings
{
    public const int BandCount = 4;
    public const float MinFreq = 20, MaxFreq = 20000;
    public const float MinHighPass = 40, MaxHighPass = 300;
    public const float MaxGainDb = 18;
    public const float MinQ = 0.3f, MaxQ = 10;

    public bool Enabled { get; init; }
    public bool HighPass { get; init; }
    /// <summary>Частота среза ФВЧ, 40–300 Гц.</summary>
    public float HighPassHz { get; init; } = 80;
    /// <summary>Ровно <see cref="BandCount"/> полос (лишние отбрасываются, недостающие — «выкл»).</summary>
    public IReadOnlyList<EqBand> Bands { get; init; } = DefaultBands;

    public static readonly IReadOnlyList<EqBand> DefaultBands =
    [
        new EqBand { FreqHz = 120 },
        new EqBand { FreqHz = 500 },
        new EqBand { FreqHz = 3000 },
        new EqBand { FreqHz = 10000 },
    ];

    public EqSettings Normalized()
    {
        var bands = new EqBand[BandCount];
        for (int i = 0; i < BandCount; i++)
            bands[i] = (Bands is not null && i < Bands.Count && Bands[i] is { } b ? b : DefaultBands[i]).Normalized();
        return this with { HighPassHz = GateSettings.Clamp(HighPassHz, MinHighPass, MaxHighPass, 80), Bands = bands };
    }

    public EqSettings WithBand(int index, Func<EqBand, EqBand> change)
    {
        var bands = Normalized().Bands.ToArray();
        bands[index] = change(bands[index]).Normalized();
        return this with { Bands = bands };
    }

    // список сравнивается по содержимому: иначе одинаковые графы из разных файлов считались бы разными
    public bool Equals(EqSettings? other) =>
        other is not null && Enabled == other.Enabled && HighPass == other.HighPass && HighPassHz.Equals(other.HighPassHz)
        && (ReferenceEquals(Bands, other.Bands) || (Bands ?? []).SequenceEqual(other.Bands ?? []));

    public override int GetHashCode() => HashCode.Combine(Enabled, HighPass, HighPassHz, Bands?.Count ?? 0);
}

/// <summary>Компрессор с мягким коленом; детектор общий на стереопару.</summary>
public sealed record CompSettings
{
    public bool Enabled { get; init; }
    /// <summary>−60…0 dBFS.</summary>
    public float ThresholdDb { get; init; } = -20;
    /// <summary>1:1 – 20:1.</summary>
    public float Ratio { get; init; } = 3;
    /// <summary>0,1–100 мс.</summary>
    public float AttackMs { get; init; } = 10;
    /// <summary>10–1000 мс.</summary>
    public float ReleaseMs { get; init; } = 120;
    /// <summary>Ширина мягкого колена, 0–12 дБ.</summary>
    public float KneeDb { get; init; } = 6;
    /// <summary>Компенсация, 0…+24 дБ.</summary>
    public float MakeupDb { get; init; }

    public CompSettings Normalized() => this with
    {
        ThresholdDb = GateSettings.Clamp(ThresholdDb, -60, 0, -20),
        Ratio = GateSettings.Clamp(Ratio, 1, 20, 3),
        AttackMs = GateSettings.Clamp(AttackMs, 0.1f, 100, 10),
        ReleaseMs = GateSettings.Clamp(ReleaseMs, 10, 1000, 120),
        KneeDb = GateSettings.Clamp(KneeDb, 0, 12, 6),
        MakeupDb = GateSettings.Clamp(MakeupDb, 0, 24, 0),
    };
}

/// <summary>
/// Цепочка обработки узла: гейт → EQ → компрессор, порядок фиксирован. Неизменяемая: интерфейс строит новую при
/// каждой правке, аудиопоток подхватывает ссылку и плавно (за 10 мс) переходит на новые значения.
/// </summary>
public sealed record ChainSettings
{
    public GateSettings Gate { get; init; } = new();
    public EqSettings Eq { get; init; } = new();
    public CompSettings Comp { get; init; } = new();

    /// <summary>Включён хотя бы один блок: узлу нужна обработка.</summary>
    public bool IsActive => Gate.Enabled || Eq.Enabled || Comp.Enabled;

    public ChainSettings Normalized() => new()
    {
        Gate = (Gate ?? new()).Normalized(),
        Eq = (Eq ?? new()).Normalized(),
        Comp = (Comp ?? new()).Normalized(),
    };
}
