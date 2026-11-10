using System.Diagnostics;
using System.Globalization;
using Mixer.Audio.Engine;

namespace Mixer.Run;

/// <summary>Выгрузка трассировки проводов в CSV: kind;t_ms;ideal_ms;frames;avail;virtual;eps_ppm (время от первого события).</summary>
public static class TraceDump
{
    public static void Save(string path, AudioEngine engine)
    {
        var inv = CultureInfo.InvariantCulture;
        using var w = new StreamWriter(path);
        w.WriteLine("wire;kind;t_ms;ideal_ms;frames;avail;virtual;eps_ppm");
        foreach (var wire in engine.Wires)
        {
            var t = wire.Trace;
            if (t is null) continue;
            int nw = Volatile.Read(ref t.WriteCount), nr = Volatile.Read(ref t.ReadCount);
            long t0 = long.MaxValue;
            if (nw > 0) t0 = Math.Min(t0, t.WriteNow[0]);
            if (nr > 0) t0 = Math.Min(t0, t.ReadNow[0]);
            double Ms(long ticks) => (ticks - t0) * 1000.0 / Stopwatch.Frequency;
            string name = $"{wire.FromId}->{wire.ToId}";
            for (int i = 0; i < nw; i++)
                w.WriteLine(string.Join(';', name, "W", Ms(t.WriteNow[i]).ToString("F3", inv), Ms(t.WriteIdeal[i]).ToString("F3", inv), t.WriteFrames[i], "", "", ""));
            for (int i = 0; i < nr; i++)
                w.WriteLine(string.Join(';', name, "R", Ms(t.ReadNow[i]).ToString("F3", inv), Ms(t.ReadIdeal[i]).ToString("F3", inv), t.ReadFrames[i],
                    t.ReadAvail[i], t.ReadVirtual[i].ToString("F1", inv), (t.ReadEps[i] * 1e6).ToString("F1", inv)));
        }
        Console.WriteLine($"Трассировка: {Path.GetFullPath(path)}");
    }
}
