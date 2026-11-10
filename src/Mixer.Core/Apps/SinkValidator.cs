using Mixer.Audio.Interop;
using Mixer.Core.Sys;

namespace Mixer.Core.Apps;

public enum SinkVerdict { Ok, Warning, Rejected }

public sealed record SinkCheck(SinkVerdict Verdict, string? Message, EndpointInfo? Endpoint)
{
    public bool Usable => Verdict != SinkVerdict.Rejected;
}

/// <summary>
/// Проверка заглушки «Забрать себе» до того, как её принять. Плохая заглушка роняет службу Windows Audio
/// (этап 0, результат 4), поэтому входы Voicemeeter VAIO без запущенного Voicemeeter отклоняются без пробного потока,
/// а остальные проверяются пробным потоком с таймаутом (<see cref="StreamProbe"/>).
/// </summary>
public static class SinkValidator
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Voicemeeter, Banana, Potato: voicemeeter.exe, voicemeeterpro.exe, voicemeeter8x64.exe и т. п.</summary>
    public static bool IsVoicemeeterRunning(ProcessSnapshot processes) =>
        processes.ByPid.Values.Any(p => p.Exe.StartsWith("voicemeeter", StringComparison.OrdinalIgnoreCase));

    /// <param name="channels">Число каналов формата микса устройства (null — неизвестно; для VAIO не опрашивать).</param>
    public static SinkCheck Check(string? sinkEndpointId, IReadOnlyList<EndpointInfo> devices, ProcessSnapshot processes,
        Func<string, ProbeResult> probe, Func<EndpointInfo, int?>? channels = null)
    {
        if (string.IsNullOrEmpty(sinkEndpointId))
            return new SinkCheck(SinkVerdict.Rejected, CoreStrings.Sink_NotSet, null);

        var ep = devices.FirstOrDefault(d => d.Flow == EDataFlow.Render && string.Equals(d.Id, sinkEndpointId, StringComparison.OrdinalIgnoreCase));
        if (ep is null)
        {
            bool isCapture = devices.Any(d => d.Flow == EDataFlow.Capture && string.Equals(d.Id, sinkEndpointId, StringComparison.OrdinalIgnoreCase));
            return new SinkCheck(SinkVerdict.Rejected, isCapture
                ? CoreStrings.Sink_NotPlayback
                : CoreStrings.Sink_NotFound, null);
        }

        bool vaio = ep.IsVoicemeeter;
        if (vaio && !IsVoicemeeterRunning(processes))
            return new SinkCheck(SinkVerdict.Rejected,
                Loc.F(CoreStrings.Sink_VaioOffFormat, ep.Name), ep);

        var r = probe(ep.Id);
        if (!r.Ok)
            return new SinkCheck(SinkVerdict.Rejected, Loc.F(CoreStrings.Sink_ProbeFailedFormat, ep.Name, r.Message), ep);

        var warnings = new List<string>();
        if (ep.IsDefault)
            warnings.Add(Loc.F(CoreStrings.Sink_IsDefaultFormat, ep.Name));
        if (vaio)
            warnings.Add(Loc.F(CoreStrings.Sink_VaioUnreliableFormat, ep.Name));
        if (channels is not null && FewerChannelsThanDefault(ep, devices, channels) is { } w)
            warnings.Add(w);
        return warnings.Count == 0
            ? new SinkCheck(SinkVerdict.Ok, null, ep)
            : new SinkCheck(SinkVerdict.Warning, string.Join("; ", warnings), ep);
    }

    /// <summary>
    /// Этап 2, проверка на устройствах: поток, открытый в 7.1 на устройстве по умолчанию, при переезде на стерео-заглушку
    /// (automatic stream routing, без переоткрытия) Windows сводит в стерео с нормировкой, и захват приложения тише
    /// на 13,5 дБ (тон −70 → −83,5 dBFS). Поток, открытый прямо на заглушке, приходит без потерь.
    /// </summary>
    private static string? FewerChannelsThanDefault(EndpointInfo sink, IReadOnlyList<EndpointInfo> devices, Func<EndpointInfo, int?> channels)
    {
        var def = devices.FirstOrDefault(d => d.Flow == EDataFlow.Render && d.IsDefault);
        if (def is null || def.Id == sink.Id) return null;
        int? sinkCh = channels(sink), defCh = channels(def);
        if (sinkCh is not { } s || defCh is not { } d || s >= d) return null;
        return Loc.F(CoreStrings.Sink_FewerChannelsFormat, s, def.Name, d, 13.5);
    }
}
