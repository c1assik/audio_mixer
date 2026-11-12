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

    /// <summary>
    /// Виртуальный кабель: устройство из таблицы сопоставления (VB-Cable, Cable A–D, Hi-Fi Cable, Virtual Audio Cable)
    /// или любой драйвер VB-Audio со словом Cable. Voicemeeter сюда не входит.
    /// </summary>
    public static bool IsVirtualCable(string endpointName)
    {
        if (Cables.CableTable.Default.Match(endpointName, EDataFlow.Render) is not null
            || Cables.CableTable.Default.Match(endpointName, EDataFlow.Capture) is not null) return true;
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
        // по таблице: у Virtual Audio Cable все линии на одном драйвере, но «Line 1» слышит только свою «Line 1»
        if (Cables.CableTable.Default.PairIdOf(capture.Name, EDataFlow.Capture) is { } pairId)
        {
            foreach (var d in devices)
                if (d.Flow == EDataFlow.Render && Cables.CableTable.Default.PairIdOf(d.Name, EDataFlow.Render) == pairId) yield return d;
            yield break;
        }
        string driver = DriverName(capture.Name)!;
        foreach (var d in devices)
        {
            if (d.Flow != EDataFlow.Render) continue;
            if (!string.Equals(DriverName(d.Name), driver, StringComparison.OrdinalIgnoreCase)) continue;
            yield return d;
        }
    }

    /// <summary>
    /// Два разных входа одного виртуального кабеля («CABLE Input» и «CABLE In 16ch»). Проверка 11 ноября 2026: пока один
    /// открыт, второй отвечает «занято» (эксклюзивный режим), а поток приложения, перенесённый в него, обрывается
    /// (AUDCLNT_E_NOT_INITIALIZED).
    /// </summary>
    public static bool SameCable(EndpointInfo a, EndpointInfo b)
    {
        if (a.Flow != EDataFlow.Render || b.Flow != EDataFlow.Render || string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)) return false;
        if (Cables.CableTable.Default.PairIdOf(a.Name, EDataFlow.Render) is { } pa)
            return Cables.CableTable.Default.PairIdOf(b.Name, EDataFlow.Render) == pa;
        return IsVirtualCable(a.Name) && string.Equals(DriverName(a.Name), DriverName(b.Name), StringComparison.OrdinalIgnoreCase);
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
                // кабель, связанный вручную: второй конец записан в узле
                if (source.Cable is { PartnerId: { Length: > 0 } partner })
                {
                    var p = devices.FirstOrDefault(d => d.Flow == EDataFlow.Render && string.Equals(d.Id, partner, StringComparison.OrdinalIgnoreCase))
                            ?? devices.FirstOrDefault(d => d.Flow == EDataFlow.Render && d.Name == source.Cable.PartnerName);
                    if (p is not null) taps.Add(p.Id);
                }
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
