using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mixer.Plugins.Catalog;
using Mixer.Plugins.Host;

namespace Mixer.PluginBench;

/// <summary>
/// Этап 5: проверка совместимости каждого эффекта из каталога тем же кодом, что у микшера (PluginHost, AudioLink).
/// Загрузка, звук (блоки по 480, неровные блоки, 44,1 кГц), окно редактора со звуком под ним, состояние после
/// перезапуска хоста, выгрузка. Звук гоняется без пауз, с дедлайном 1 с: здесь важно, что плагин считает и что
/// выдаёт, а нагрузка берётся из времени process. Итог — таблица для docs/DEVELOPMENT.md и JSON.
/// </summary>
internal static class CompatCheck
{
    private const int Rate = 48000;
    private const int Block = 480;

    public static int Run(string hostExe, IReadOnlyList<string> folders, string? only, string? outPath)
    {
        string root = Paths.RepoRoot() ?? ".";
        string shots = Path.Combine(root, "probe-out", "compat");
        Directory.CreateDirectory(shots);

        var catalog = new PluginCatalog(new PluginScanner(hostExe), log: Console.WriteLine);
        catalog.RefreshAsync(PluginFiles.StandardFolders.Concat(folders)).GetAwaiter().GetResult();
        var effects = catalog.Effects
            .Where(e => only == null || e.Class.Name.Contains(only, StringComparison.OrdinalIgnoreCase) ||
                        e.Module.Path.Contains(only, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Class.Vendor, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Class.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (inL, inR, source) = Signal();
        Console.WriteLine($"сигнал: {source}, {inL.Length / (double)Rate:0.0} с; эффектов: {effects.Count}");

        var rows = new List<Row>();
        foreach (var e in effects)
        {
            Console.WriteLine($"\n{e.Class.Name} · {e.Class.Vendor} · {e.Module.Path}");
            var row = new Row
            {
                Name = e.Class.Name, Vendor = e.Class.Vendor, Version = e.Class.Version, Path = e.Module.Path,
                Categories = string.Join("/", e.Class.Categories), Signer = e.Module.Signature?.Signer,
            };
            try { Check(hostExe, e, row, inL, inR, shots); }
            catch (Exception ex) { row.Notes.Add($"проверка прервана: {ex.GetType().Name}: {ex.Message}"); }
            foreach (var n in row.Notes) Console.WriteLine($"  ! {n}");
            Console.WriteLine($"  итог: {(row.Works ? "работает" : "НЕ РАБОТАЕТ")}");
            rows.Add(row);
        }

        outPath ??= Path.Combine(root, "probe-out", $"compat-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(outPath, Markdown(rows, catalog, source));
        File.WriteAllText(Path.ChangeExtension(outPath, ".json"),
            JsonSerializer.Serialize(rows, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            }));
        Console.WriteLine($"\nработают {rows.Count(r => r.Works)} из {rows.Count}; таблица: {Path.GetFullPath(outPath)}");
        return rows.All(r => r.Works) ? 0 : 1;
    }

    private static void Check(string hostExe, CatalogEntry e, Row row, float[] inL, float[] inR, string shots)
    {
        var load = PluginLoad.Vst3(e.Module.Path, e.Class.ClassId);
        float[] outA = new float[inL.Length], outAR = new float[inL.Length];
        byte[]? state = null;
        PluginParam? edited = null;
        double editedValue = 0;
        string? editedText = null;

        // 1. главный хост: загрузка, звук, частота, окно, правка параметра и состояние, выгрузка
        using (var h = PluginHost.Start(new PluginHostOptions { HostExe = hostExe }))
        {
            var sw = Stopwatch.StartNew();
            PluginInfo info;
            try { info = h.LoadAsync(load).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                row.Notes.Add($"не загрузился: {ex.Message} {Died(h)}");
                return;
            }
            row.LoadMs = sw.Elapsed.TotalMilliseconds;
            row.Loaded = true;
            row.Latency = info.LatencySamples;
            row.Channels = $"{info.InChannels}→{info.OutChannels}";
            row.HasEditor = info.HasEditor;
            row.Params = info.Params.Count(p => !p.Hidden && !p.ReadOnly && !p.Bypass);
            row.HasBypass = info.Params.Any(p => p.Bypass);
            using (var p = Process.GetProcessById(h.ProcessId))
            {
                row.PrivateMb = p.PrivateMemorySize64 / 1048576.0;
                row.WorkingSetMb = p.WorkingSet64 / 1048576.0;
            }
            Console.WriteLine($"  загружен за {row.LoadMs:0} мс; {row.PrivateMb:0} МБ частной; задержка {row.Latency}; " +
                              $"каналы {row.Channels}; окно {(row.HasEditor ? "есть" : "нет")}; параметров {row.Params}");

            row.Fixed = Run(h, inL, inR, outA, outAR, _ => Block);
            Console.WriteLine($"  480: {row.Fixed}");
            if (!h.IsRunning) { row.Notes.Add($"упал на блоках по 480 {Died(h)}"); return; }

            if (info.HasEditor) row.Editor = Editor(h, info, inL, inR, shots, row);
            else row.Editor = $"none, parameter list ({row.Params})";
            row.EditorOk = info.HasEditor ? row.Editor.StartsWith("yes") : row.Params > 0;
            Console.WriteLine($"  окно: {row.Editor}");
            if (!h.IsRunning) { row.Notes.Add($"упал с открытым окном {Died(h)}"); return; }

            try
            {
                var pars = h.GetParamsAsync().GetAwaiter().GetResult();
                // выбор пресета плагин не обязан восстанавливать из состояния (Youlean): такой — только если нет других
                edited = pars.Where(p => !p.Hidden && !p.ReadOnly && !p.Bypass)
                    .OrderBy(p => p.Title.Contains("preset", StringComparison.OrdinalIgnoreCase) ||
                                  p.Title.Contains("program", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                if (edited != null)
                {
                    editedValue = Other(edited);
                    h.SetParam(edited.Id, editedValue);
                    // полсекунды звука: плагины на JUCE переносят параметр в своё состояние таймером в главном
                    // потоке, getState сразу после правки отдаёт старое (микшер берёт состояние через 2 с)
                    float[] o = new float[Block], o2 = new float[Block];
                    for (int i = 0; i < 25 && h.IsRunning; i++)
                    {
                        h.Audio.Process(inL.AsSpan(i * Block, Block), inR.AsSpan(i * Block, Block), o, o2,
                            Stopwatch.GetTimestamp() + Stopwatch.Frequency);
                        Thread.Sleep(20);
                    }
                    // сравнивать с тем, что плагин принял: ступенчатый параметр без объявленных шагов он округляет (SPAN)
                    var took = h.GetParamsAsync().GetAwaiter().GetResult().FirstOrDefault(p => p.Id == edited.Id);
                    editedValue = took?.Value ?? editedValue;
                    editedText = took?.Text;
                }
                state = h.GetStateAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) { row.Notes.Add($"состояние: {ex.Message} {Died(h)}"); }

            // смена частоты последней: плагин, который на ней падает, не мешает проверить остальное
            try
            {
                h.SetRateAsync(44100).GetAwaiter().GetResult();
                Settle(h);
                var half = inL.Length / 2;
                row.Rate441 = Run(h, inL[..half], inR[..half], new float[half], new float[half], _ => 441);
                Console.WriteLine($"  44,1 кГц: {row.Rate441}");
                h.SetRateAsync(Rate).GetAwaiter().GetResult();
                Run(h, inL[..(Block * 50)], inR[..(Block * 50)], new float[Block * 50], new float[Block * 50], _ => Block);
            }
            catch (Exception ex) { row.Notes.Add($"смена частоты: {ex.Message} {Died(h)}"); }
            if (!h.IsRunning)
            {
                row.RateCrash = h.Exit?.Message;
                if (row.Rate441 != null) row.Rate441.Died = true;
                Console.WriteLine($"  смена частоты: хост упал {Died(h)}");
            }

            h.StopAsync().GetAwaiter().GetResult();
            row.Exit = h.Exit?.Reason.ToString();
            if (h.Exit?.Reason != HostExitReason.Quit && row.RateCrash == null) row.Notes.Add($"выгрузка: {h.Exit?.Reason} {h.Exit?.Message} {h.StderrTail()}".TrimEnd());
        }

        // 2. неровные блоки в свежем хосте: тот же вход, выход сравнивается с блоками по 480
        var rnd = new Random(1);
        var outB = new float[inL.Length];
        using (var h = Loaded(hostExe, load, row, "неровные блоки"))
        {
            if (h != null)
            {
                row.Ragged = Run(h, inL, inR, outB, new float[inL.Length], _ => rnd.Next(1, 1025));
                row.RaggedDiffDb = DiffDb(outA, outB, 0);
                if (row.RaggedDiffDb > -40)
                {
                    // выход «другой» часто просто сдвинут: задержка плагина зависит от размера блока
                    row.RaggedLag = BestLag(outA, outB, 4096);
                    row.RaggedLagDiffDb = DiffDb(outA, outB, row.RaggedLag.Value);
                    // цифрами не решить, хорош ли такой звук: оба выхода (левый канал) — послушать
                    string stem = Path.Combine(shots, Safe(e.Class.Name));
                    WriteWav(stem + "-480.wav", outA);
                    WriteWav(stem + "-ragged.wav", outB);
                }
                Console.WriteLine($"  неровные: {row.Ragged}; от блоков по 480: {Db(row.RaggedDiffDb)}" +
                                  (row.RaggedLag is { } lag ? $"; лучший сдвиг {lag} кадров, с ним {Db(row.RaggedLagDiffDb!.Value)}" : ""));
            }
        }

        // разница бывает и без неровных блоков (случайность, время): повтор блоками по 480 в свежем хосте
        if (row.RaggedDiffDb > -90 && row.Ragged?.Ok == true)
        {
            var outA2 = new float[inL.Length];
            using var h = Loaded(hostExe, load, row, "повтор");
            if (h != null)
            {
                Run(h, inL, inR, outA2, new float[inL.Length], _ => Block);
                row.RepeatDiffDb = DiffDb(outA, outA2, 0);
                Console.WriteLine($"  повтор блоками по 480: {Db(row.RepeatDiffDb.Value)}");
            }
        }

        // не справился с неровными — проверить «ровные блоки» (выход на 256 кадров позже)
        if (row.Ragged?.Ok != true || (row.RaggedDiffDb > -60 && row.RepeatDiffDb < row.RaggedDiffDb - 20))
        {
            rnd = new Random(1);
            using var h = Loaded(hostExe, load with { EvenBlocks = true }, row, "ровные блоки");
            if (h != null)
            {
                var outE = new float[inL.Length];
                var even = Run(h, inL, inR, outE, new float[inL.Length], _ => rnd.Next(1, 1025));
                row.EvenBlocks = $"{even.Short}, vs 480 {Db(DiffDb(outA, outE, 256))}";
                Console.WriteLine($"  ровные блоки: {row.EvenBlocks}");
            }
        }

        // 3. состояние после перезапуска: новый хост с сохранённым состоянием
        if (state != null)
        {
            using var h = PluginHost.Start(new PluginHostOptions { HostExe = hostExe });
            try
            {
                var info = h.LoadAsync(load with { State = state }).GetAwaiter().GetResult();
                var again = h.GetStateAsync().GetAwaiter().GetResult();
                bool same = again.AsSpan().SequenceEqual(state);
                var p = edited == null ? null : info.Params.FirstOrDefault(x => x.Id == edited.Id);
                bool kept = p != null && (Math.Abs(p.Value - editedValue) < 1e-4 || !string.IsNullOrEmpty(editedText) && p.Text == editedText);
                bool preset = edited != null && (edited.Title.Contains("preset", StringComparison.OrdinalIgnoreCase) ||
                                                 edited.Title.Contains("program", StringComparison.OrdinalIgnoreCase));
                row.StateOk = edited != null && !preset ? kept : same || kept;
                if (!row.StateOk && preset) row.Notes.Add("только параметр пресета, байты состояния разные: проверить вручную в окне");
                row.State = (edited == null ? "no editable parameter; " : $"«{edited.Title}» {(kept ? "kept" : $"LOST ({p?.Value:0.####} instead of {editedValue:0.####})")}; ") +
                            $"{state.Length} bytes{(same ? ", bit-exact" : ", bytes differ")}";
            }
            catch (Exception ex) { row.State = $"failed: {ex.Message}"; row.Notes.Add($"загрузка с состоянием: {ex.Message} {Died(h)}"); }
            Console.WriteLine($"  состояние: {row.State}");
            h.StopAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>Плагин после смены частоты может ещё перенастраиваться (restartComponent): подождать до 2 с в реальном времени.</summary>
    private static void Settle(PluginHost h)
    {
        float[] x = new float[441], y = new float[441];
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(2) && h.IsRunning &&
               h.Audio.Process(x, x, y, y, Stopwatch.GetTimestamp() + Stopwatch.Frequency) != BlockOutcome.Processed)
            Thread.Sleep(10);
    }

    private static PluginHost? Loaded(string hostExe, PluginLoad load, Row row, string what)
    {
        var h = PluginHost.Start(new PluginHostOptions { HostExe = hostExe });
        try
        {
            h.LoadAsync(load).GetAwaiter().GetResult();
            return h;
        }
        catch (Exception ex)
        {
            row.Notes.Add($"{what}: не загрузился второй раз: {ex.Message} {Died(h)}");
            h.Dispose();
            return null;
        }
    }

    /// <summary>Окно редактора над своим окном-владельцем, со звуком под ним; снимок в PNG.</summary>
    private static string Editor(PluginHost h, PluginInfo info, float[] inL, float[] inR, string shots, Row row)
    {
        var opened = new TaskCompletionSource<EditorBounds>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<EditorBounds>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.EditorOpened += b => opened.TrySetResult(b);
        h.EditorClosed += b => closed.TrySetResult(b);
        h.EditorFailed += m => opened.TrySetException(new InvalidOperationException(m));

        using var owner = EditorCheck.OwnerWindow.Create();
        h.OpenEditor(owner.Handle, $"{info.Name} — compat");
        try
        {
            if (!opened.Task.Wait(TimeSpan.FromSeconds(10))) return "NO: not opened in 10 s";
        }
        catch (AggregateException ex) { return $"NO: {ex.InnerException?.Message}"; }

        // 2 с звука, пока окно открыто: так плагин живёт и в микшере
        var sw = Stopwatch.StartNew();
        float[] o = new float[Block], o2 = new float[Block];
        for (int pos = 0; sw.Elapsed < TimeSpan.FromSeconds(2) && h.IsRunning; pos = (pos + Block) % (inL.Length - Block))
        {
            h.Audio.Process(inL.AsSpan(pos, Block), inR.AsSpan(pos, Block), o, o2, Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            Thread.Sleep(10);
        }
        if (!h.IsRunning) return "NO: host died";

        IntPtr wnd = EditorCheck.FindEditor(h.ProcessId);
        if (wnd == IntPtr.Zero) return "NO: window not found";
        Native.GetClientRect(wnd, out var cr);
        Native.GetWindowRect(wnd, out var wr);
        bool owned = Native.GetWindow(wnd, Native.GW_OWNER) == owner.Handle;
        string png = Path.Combine(shots, Safe(info.Name) + ".png");
        int colors = EditorCheck.SavePng(wnd, wr.Right - wr.Left, wr.Bottom - wr.Top, png);
        row.Screenshot = png;
        h.CloseEditor();
        bool closedOk = closed.Task.Wait(TimeSpan.FromSeconds(5));

        var problems = new List<string>();
        if (!owned) problems.Add("not owned by the mixer window");
        if (colors <= 1) problems.Add("blank");
        if (!closedOk) problems.Add("did not close");
        return $"{(problems.Count == 0 ? "yes" : "NO")} {cr.Right}×{cr.Bottom}{(problems.Count > 0 ? ": " + string.Join(", ", problems) : "")}";
    }

    /// <summary>Прогнать весь сигнал блоками от <paramref name="frames"/>. Непосчитанный блок идёт сухим.</summary>
    private static Sound Run(PluginHost h, float[] l, float[] r, float[] outL, float[] outR, Func<int, int> frames)
    {
        var s = new Sound();
        var proc = new List<double>();
        var t = new BlockTiming();
        for (int pos = 0; pos < l.Length;)
        {
            int n = Math.Min(frames(pos), l.Length - pos);
            var outcome = h.Audio.Process(l.AsSpan(pos, n), r.AsSpan(pos, n), outL.AsSpan(pos, n), outR.AsSpan(pos, n),
                Stopwatch.GetTimestamp() + Stopwatch.Frequency, ref t);
            s.Blocks++;
            if (outcome != BlockOutcome.Processed)
            {
                // приостановлен — хост перенастраивает плагин (restartComponent): так и в микшере, не провал
                if (outcome == BlockOutcome.Bypassed) s.Suspended++;
                else s.Dry++;
                l.AsSpan(pos, n).CopyTo(outL.AsSpan(pos, n));
                r.AsSpan(pos, n).CopyTo(outR.AsSpan(pos, n));
                if (!h.IsRunning) { s.Died = true; break; }
            }
            else
            {
                if (t.Result != 0) s.BadResults++;
                proc.Add(t.ProcessTicks * 1e6 / Stopwatch.Frequency / n * Block); // в пересчёте на блок 480
            }
            pos += n;
        }
        double inSq = 0, outSq = 0, peak = 0;
        for (int i = 0; i < l.Length; i++)
        {
            if (!float.IsFinite(outL[i]) || !float.IsFinite(outR[i])) { s.NonFinite++; outL[i] = outR[i] = 0; }
            inSq += l[i] * l[i];
            outSq += outL[i] * outL[i];
            peak = Math.Max(peak, Math.Max(Math.Abs(outL[i]), Math.Abs(outR[i])));
        }
        s.GainDb = 10 * Math.Log10((outSq + 1e-30) / (inSq + 1e-30));
        s.PeakDb = 20 * Math.Log10(peak + 1e-30);
        if (proc.Count > 0)
        {
            proc.Sort();
            s.ProcessP99Us = proc[(int)Math.Min(proc.Count - 1, proc.Count * 0.99)];
            s.ProcessMeanUs = proc.Average();
        }
        return s;
    }

    /// <summary>Наибольшая разница двух выходов (второй сдвинут на <paramref name="shift"/>), дБ от пика первого.</summary>
    private static double DiffDbForward(float[] a, float[] b, int shift)
    {
        double max = 0, peak = 0;
        for (int i = 0; i + shift < a.Length; i++)
        {
            max = Math.Max(max, Math.Abs(a[i] - b[i + shift]));
            peak = Math.Max(peak, Math.Abs(a[i]));
        }
        return max == 0 ? double.NegativeInfinity : 20 * Math.Log10(max / Math.Max(peak, 1e-9));
    }

    /// <summary>Сдвиг b относительно a (кадры, b позже — плюс) с наибольшей корреляцией на самой громкой секунде.</summary>
    private static int BestLag(float[] a, float[] b, int maxLag)
    {
        int len = Rate, start = maxLag;
        double best = 0;
        for (int i = maxLag; i + len + maxLag < a.Length; i += len / 4)
        {
            double e = 0;
            for (int k = 0; k < len; k += 4) e += a[i + k] * a[i + k];
            if (e > best) { best = e; start = i; }
        }
        int lag = 0;
        double top = double.MinValue;
        for (int d = -maxLag; d <= maxLag; d++)
        {
            double c = 0;
            for (int k = 0; k < len; k += 2) c += a[start + k] * b[start + k + d];
            if (c > top) { top = c; lag = d; }
        }
        return lag;
    }

    /// <summary>Наибольшая разница при сдвиге, который может быть и отрицательным.</summary>
    private static double DiffDb(float[] a, float[] b, int shift) => shift >= 0 ? DiffDbForward(a, b, shift) : DiffDbForward(b, a, -shift);

    private static string Db(double db) => double.IsNegativeInfinity(db) ? "bit-exact" : $"{db:0} dB";

    /// <summary>Другое значение параметра: соседний шаг или четверть шкалы.</summary>
    private static double Other(PluginParam p)
    {
        if (p.Steps > 0)
        {
            int k = (int)Math.Round(p.Value * p.Steps);
            return (k > 0 ? k - 1 : 1) / (double)p.Steps;
        }
        return Math.Round(p.Value < 0.5 ? p.Value + 0.25 : p.Value - 0.25, 4);
    }

    private static void WriteWav(string path, float[] mono)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + mono.Length * 2); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(mono.Length * 2);
        foreach (float v in mono) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
    }

    private static string Died(PluginHost h) => h.IsRunning ? "" : $"(хост: {h.Exit?.Reason} {h.Exit?.Message} {h.StderrTail()})".TrimEnd();

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    /// <summary>
    /// Речь из SAPI (два предложения с шипящими, 48 кГц) с шумом −50 dBFS: шумодавам и деэссерам есть с чем работать.
    /// Без SAPI — гармонический «голос» со слогами.
    /// </summary>
    private static (float[] L, float[] R, string Source) Signal()
    {
        float[]? speech = null;
        string source = "SAPI speech";
        try { speech = Speech(); }
        catch (Exception ex) { source = $"synthetic voice (SAPI: {ex.Message})"; }
        if (speech == null)
        {
            speech = new float[Rate * 8];
            for (int i = 0; i < speech.Length; i++)
            {
                double t = i / (double)Rate;
                double f0 = 140 + 15 * Math.Sin(2 * Math.PI * 5 * t);
                double env = Math.Max(0, Math.Sin(2 * Math.PI * 2.5 * t));
                double v = 0;
                for (int k = 1; k <= 12; k++) v += Math.Sin(2 * Math.PI * f0 * k * t) / k;
                speech[i] = (float)(0.3 * env * v);
            }
        }
        float peak = speech.Max(Math.Abs);
        var rnd = new Random(7);
        float noise = (float)Math.Pow(10, -50 / 20.0) * 1.7f;
        var l = new float[speech.Length];
        var r = new float[speech.Length];
        for (int i = 0; i < l.Length; i++)
        {
            float v = speech[i] / peak * 0.5f; // −6 dBFS
            l[i] = v + noise * (float)(rnd.NextDouble() * 2 - 1);
            r[i] = 0.9f * v + noise * (float)(rnd.NextDouble() * 2 - 1);
        }
        return (l, r, source);
    }

    private static float[]? Speech()
    {
        string wav = Path.Combine(Path.GetTempPath(), $"mixer-compat-{Environment.ProcessId}.wav");
        dynamic voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")!)!;
        dynamic file = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpFileStream")!)!;
        file.Format.Type = 39; // SAFT48kHz16BitMono
        file.Open(wav, 3, false); // SSFMCreateForWrite
        voice.AudioOutputStream = file;
        voice.Speak("Привет, это проверка микрофона. Шесть шипящих: щётка, шорох, свист и шёпот. " +
                    "She sells sea shells by the sea shore, so sharp and so soft.", 0);
        file.Close();
        try
        {
            byte[] bytes = File.ReadAllBytes(wav);
            int data = bytes.AsSpan().IndexOf("data"u8);
            if (data < 0) return null;
            int len = Math.Min(BitConverter.ToInt32(bytes, data + 4), bytes.Length - data - 8);
            var s = new float[len / 2];
            for (int i = 0; i < s.Length; i++) s[i] = BitConverter.ToInt16(bytes, data + 8 + 2 * i) / 32768f;
            return s.Length > Rate ? s : null;
        }
        finally { File.Delete(wav); }
    }

    private static string Markdown(List<Row> rows, PluginCatalog catalog, string source)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"Checked {DateTime.Now:d MMMM yyyy} with `Mixer.PluginBench --compat`, input: {source}. " +
                      $"{rows.Count(r => r.Works)} of {rows.Count} work.");
        sb.AppendLine();
        sb.AppendLine("| Plug-in | Load | Latency | Sound: 480 / ragged / 44.1 kHz | `process` p99 | Window | State after restart | Result |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var r in rows)
        {
            string load = r.Loaded ? string.Format(inv, "{0:0} ms, {1:0} MB", r.LoadMs, r.PrivateMb) : "**no**";
            string ragged = r.Ragged == null ? "—" : r.Ragged.Ok ? $"{r.Ragged.Short}, vs 480 {Db(r.RaggedDiffDb)}" : $"**{r.Ragged.Short}**";
            if (r.RaggedLag is { } lag) ragged += $", shifted {lag:+0;−0} frames → {Db(r.RaggedLagDiffDb!.Value)}";
            if (r.RepeatDiffDb is { } rep) ragged += $", repeat {Db(rep)}";
            if (r.EvenBlocks != null) ragged += $"; even blocks: {r.EvenBlocks}";
            string sound = r.Fixed == null ? "—" : $"{r.Fixed.Short} / {ragged} / {r.Rate441?.Short ?? "—"}";
            string p99 = r.Fixed?.ProcessP99Us is { } us ? string.Format(inv, "{0:0.0}%", us / 10000 * 100) : "—";
            sb.AppendLine($"| {r.Name} ({r.Vendor}) | {load} | {(r.Loaded ? r.Latency.ToString(inv) : "—")} | {sound} | {p99} | " +
                          $"{r.Editor ?? "—"} | {r.State ?? "—"} | {(r.Works ? "works" : "**fails**")} |");
        }
        if (catalog.NotLoaded.Count > 0 || catalog.NotSupported.Count > 0)
        {
            sb.AppendLine();
            foreach (var m in catalog.NotLoaded) sb.AppendLine($"- Not loaded: `{m.FileName}` — {m.Problem} {m.Detail}".TrimEnd());
            foreach (var p in catalog.NotSupported) sb.AppendLine($"- Not supported: {p.Name} (`{Path.GetFileName(p.Path)}`) — {p.Problem} {p.Detail}".TrimEnd());
        }
        return sb.ToString();
    }

    internal sealed class Sound
    {
        public int Blocks { get; set; }
        public int Dry { get; set; }
        public int Suspended { get; set; }
        public int BadResults { get; set; }
        public int NonFinite { get; set; }
        public bool Died { get; set; }
        public double GainDb { get; set; }
        public double PeakDb { get; set; }
        public double? ProcessP99Us { get; set; }
        public double? ProcessMeanUs { get; set; }

        public bool Silent => GainDb < -60;
        public bool Ok => !Died && Dry == 0 && NonFinite == 0 && BadResults == 0 && !Silent;

        /// <summary>Коротко для таблицы: ok и усиление, или что не так.</summary>
        public string Short => Died ? "crashed" : Dry > 0 ? $"{Dry} dry blocks" : NonFinite > 0 ? $"{NonFinite} NaN/Inf" :
            BadResults > 0 ? $"process error ×{BadResults}" : Silent ? "silence" :
            $"ok {Gain(GainDb)}{(Suspended > 0 ? $", {Suspended} suspended" : "")}";

        public static string Gain(double db) => Math.Round(db) == 0 ? "0 dB" : $"{Math.Round(db):+0;−0} dB";

        public override string ToString() => $"блоков {Blocks}, сухих {Dry}, приостановлен {Suspended}, ошибок {BadResults}, NaN {NonFinite}, " +
            $"усиление {GainDb:0.0} дБ, пик {PeakDb:0.0} dBFS, process p99 {ProcessP99Us:0} мкс на 480{(Died ? ", ХОСТ УПАЛ" : "")}";
    }

    internal sealed class Row
    {
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string Version { get; set; } = "";
        public string Path { get; set; } = "";
        public string Categories { get; set; } = "";
        public string? Signer { get; set; }
        public bool Loaded { get; set; }
        public double LoadMs { get; set; }
        public double PrivateMb { get; set; }
        public double WorkingSetMb { get; set; }
        public int Latency { get; set; }
        public string? Channels { get; set; }
        public bool HasEditor { get; set; }
        public bool HasBypass { get; set; }
        public int Params { get; set; }
        public Sound? Fixed { get; set; }
        public Sound? Ragged { get; set; }
        public double RaggedDiffDb { get; set; } = double.NaN;
        public int? RaggedLag { get; set; }
        public double? RaggedLagDiffDb { get; set; }
        public double? RepeatDiffDb { get; set; }
        public string? RateCrash { get; set; }
        public string? EvenBlocks { get; set; }
        public Sound? Rate441 { get; set; }
        public string? Editor { get; set; }
        public bool EditorOk { get; set; }
        public string? Screenshot { get; set; }
        public string? State { get; set; }
        public bool StateOk { get; set; }
        public string? Exit { get; set; }
        public List<string> Notes { get; } = [];

        /// <summary>Критерий спецификации: загрузка, звук, окно, состояние после перезапуска.</summary>
        public bool Works => Loaded && Fixed?.Ok == true && EditorOk && StateOk;
    }
}
