using Mixer.Audio.Dsp;

namespace Mixer.Core.Editing;

/// <summary>Готовая цепочка обработки. Свои пресеты пользователя хранятся в настройках программы.</summary>
public sealed record ChainPreset(string Id, string Name, ChainSettings Chain, bool BuiltIn = false);

/// <summary>
/// Встроенные пресеты. Значения — отправная точка для голоса на USB/XLR-микрофоне в комнате и для фоновой музыки;
/// спецификация оставляет подбор на слух, поэтому их легко поправить (пресет копируется в узел, а не связывается).
/// </summary>
public static class ChainPresets
{
    public const string VoiceClean = "voice-clean";
    public const string VoiceBroadcast = "voice-broadcast";
    public const string MusicSmooth = "music-smooth";

    public static IReadOnlyList<ChainPreset> BuiltIn =>
    [
        new(VoiceClean, CoreStrings.Preset_VoiceClean, new ChainSettings
        {
            Gate = new GateSettings { Enabled = true, ThresholdDb = -50, AttackMs = 1, HoldMs = 150, ReleaseMs = 150, DepthDb = -30 },
            Eq = new EqSettings
            {
                Enabled = true, HighPass = true, HighPassHz = 80,
                Bands =
                [
                    new EqBand { Type = EqBandType.Peak, FreqHz = 250, GainDb = -2, Q = 1 },
                    new EqBand { Type = EqBandType.Peak, FreqHz = 3500, GainDb = 1.5f, Q = 0.8f },
                    new EqBand { FreqHz = 6000 },
                    new EqBand { FreqHz = 10000 },
                ],
            },
            Comp = new CompSettings { Enabled = true, ThresholdDb = -20, Ratio = 2.5f, AttackMs = 10, ReleaseMs = 150, KneeDb = 6, MakeupDb = 3 },
        }, true),
        new(VoiceBroadcast, CoreStrings.Preset_VoiceBroadcast, new ChainSettings
        {
            Gate = new GateSettings { Enabled = true, ThresholdDb = -48, AttackMs = 1, HoldMs = 120, ReleaseMs = 120, DepthDb = -40 },
            Eq = new EqSettings
            {
                Enabled = true, HighPass = true, HighPassHz = 80,
                Bands =
                [
                    new EqBand { Type = EqBandType.Peak, FreqHz = 250, GainDb = -3, Q = 1 },
                    new EqBand { Type = EqBandType.Peak, FreqHz = 3500, GainDb = 2.5f, Q = 0.8f },
                    new EqBand { Type = EqBandType.HighShelf, FreqHz = 10000, GainDb = 2, Q = 0.7f },
                    new EqBand { FreqHz = 12000 },
                ],
            },
            Comp = new CompSettings { Enabled = true, ThresholdDb = -22, Ratio = 3, AttackMs = 8, ReleaseMs = 120, KneeDb = 6, MakeupDb = 4 },
        }, true),
        new(MusicSmooth, CoreStrings.Preset_MusicSmooth, new ChainSettings
        {
            Eq = new EqSettings
            {
                Enabled = true,
                Bands =
                [
                    new EqBand { Type = EqBandType.LowShelf, FreqHz = 120, GainDb = -2, Q = 0.7f },
                    new EqBand { Type = EqBandType.Peak, FreqHz = 3000, GainDb = -1.5f, Q = 0.7f },
                    new EqBand { FreqHz = 6000 },
                    new EqBand { FreqHz = 10000 },
                ],
            },
            Comp = new CompSettings { Enabled = true, ThresholdDb = -26, Ratio = 2, AttackMs = 20, ReleaseMs = 300, KneeDb = 10, MakeupDb = 0 },
        }, true),
    ];
}
