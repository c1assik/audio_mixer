using Mixer.Audio.Interop;
using Mixer.Core.Config;

namespace Mixer.Core.Graph;

/// <summary>
/// Защита от обратной связи. У источника есть набор эндпоинтов воспроизведения, из которых он берёт звук:
/// у loopback это сам выход, у CABLE Output — парные входы кабеля. Провод в такой выход запрещён.
/// </summary>
public static class FeedbackRules
{
    /// <summary>Имя драйвера в скобках: «CABLE Output (VB-Audio Virtual Cable)» → «VB-Audio Virtual Cable».</summary>
    public static string? DriverName(string endpointName)
    {
        int open = endpointName.LastIndexOf('(');
        int close = endpointName.LastIndexOf(')');
        if (open < 0 || close <= open) return null;
        return endpointName[(open + 1)..close].Trim();
    }

    /// <summary>Виртуальный кабель VB-Audio (VB-Cable, Cable A/B, Hi-Fi Cable). Voicemeeter сюда не входит.</summary>
    public static bool IsVirtualCable(string endpointName)
    {
        string? driver = DriverName(endpointName);
        return driver is not null
               && driver.StartsWith("VB-Audio", StringComparison.OrdinalIgnoreCase)
               && driver.Contains("Cable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Эндпоинты воспроизведения, которые слышит вход-кабель: все входы (In/Input) того же драйвера кабеля.
    /// У VB-Cable это «CABLE Input» и «CABLE In 16ch» — оба идут в один «CABLE Output».
    /// </summary>
    public static IEnumerable<EndpointInfo> PairedCableInputs(EndpointInfo capture, IEnumerable<EndpointInfo> devices)
    {
        if (capture.Flow != EDataFlow.Capture || !IsVirtualCable(capture.Name)) yield break;
        string driver = DriverName(capture.Name)!;
        foreach (var d in devices)
        {
            if (d.Flow != EDataFlow.Render) continue;
            if (!string.Equals(DriverName(d.Name), driver, StringComparison.OrdinalIgnoreCase)) continue;
            yield return d;
        }
    }

    /// <summary>ID эндпоинтов воспроизведения, из которых источник берёт звук.</summary>
    public static HashSet<string> SourceTaps(NodeConfig source, EndpointInfo? resolved, IReadOnlyList<EndpointInfo> devices)
    {
        var taps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (source.Kind)
        {
            case NodeKind.Loopback:
                if (resolved is not null) taps.Add(resolved.Id);
                else if (source.EndpointId is not null) taps.Add(source.EndpointId);
                break;
            case NodeKind.DeviceInput when resolved is not null:
                foreach (var d in PairedCableInputs(resolved, devices)) taps.Add(d.Id);
                break;
            // «Весь звук системы» исключает микшер по PID, «Приложение» захватывает процесс, а не устройство:
            // через устройство петли нет.
        }
        return taps;
    }

    /// <summary>Причина запрета провода или null, если провод допустим.</summary>
    public static string? WhyForbidden(NodeConfig source, EndpointInfo? resolvedSource, EndpointInfo? resolvedOutput, IReadOnlyList<EndpointInfo> devices)
    {
        if (resolvedOutput is null) return null;
        var taps = SourceTaps(source, resolvedSource, devices);
        if (!taps.Contains(resolvedOutput.Id)) return null;
        return source.Kind == NodeKind.Loopback
            ? Loc.F(CoreStrings.Feedback_LoopbackFormat, source.Name, resolvedOutput.Name)
            : Loc.F(CoreStrings.Feedback_CableFormat, resolvedOutput.Name, resolvedSource?.Name ?? source.Name);
    }
}
