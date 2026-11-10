using System.ComponentModel;
using System.Diagnostics;
using Mixer.Probe.Interop;

namespace Mixer.Probe.Sys;

/// <summary>Снимок счётчиков: свой процесс, audiodg.exe (если система даёт его читать) и все процессоры.</summary>
public sealed record CpuSample(long Ticks, TimeSpan Self, long SelfPrivateBytes, TimeSpan? AudioDg, long? AudioDgWorkingSet,
    long SysIdle, long SysBusy)
{
    public static CpuSample Take()
    {
        using var self = Process.GetCurrentProcess();
        TimeSpan? dg = null;
        long? dgWs = null;
        foreach (var p in Process.GetProcessesByName("audiodg"))
        {
            try
            {
                dg = (dg ?? TimeSpan.Zero) + p.TotalProcessorTime;
                dgWs = (dgWs ?? 0) + p.WorkingSet64;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // защищённый процесс: CPU audiodg недоступен без прав администратора
            }
            finally
            {
                p.Dispose();
            }
        }
        NativeMethods.GetSystemTimes(out long idle, out long kernel, out long user);
        return new CpuSample(Stopwatch.GetTimestamp(), self.TotalProcessorTime, self.PrivateMemorySize64, dg, dgWs, idle, kernel + user - idle);
    }
}

/// <summary>Загрузка между двумя снимками. «% ядра» — доля одного логического процессора, «% CPU» — доля всех.</summary>
public sealed record CpuLoad(double SelfCore, double SelfTotal, double? AudioDgCore, double? AudioDgTotal, double SystemTotal)
{
    public static CpuLoad Between(CpuSample a, CpuSample b)
    {
        double wall = (b.Ticks - a.Ticks) / (double)Stopwatch.Frequency;
        int cores = Environment.ProcessorCount;
        double self = (b.Self - a.Self).TotalSeconds / wall * 100;
        double? dg = a.AudioDg is { } x && b.AudioDg is { } y ? (y - x).TotalSeconds / wall * 100 : null;
        long busy = b.SysBusy - a.SysBusy, idle = b.SysIdle - a.SysIdle;
        double sys = busy + idle > 0 ? 100.0 * busy / (busy + idle) : 0;
        return new CpuLoad(self, self / cores, dg, dg / cores, sys);
    }
}
