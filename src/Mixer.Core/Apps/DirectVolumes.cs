using Mixer.Audio.Dsp;
using Mixer.Audio.Interop;
using Mixer.Core.Graph;

namespace Mixer.Core.Apps;

/// <summary>
/// Громкость и индикатор приложений на прямом маршруте. Звук идёт мимо микшера, поэтому уровень и mute узла
/// задаются громкостью сессий приложения на его выходе (ISimpleAudioVolume), а индикатор берётся из
/// IAudioMeterInformation этих сессий.
/// <para>
/// Windows запоминает громкость сессии за приложением и устройством. Поэтому exe записывается в журнал volumeApps
/// <b>до</b> первого уменьшения громкости, а когда маршрут перестаёт быть прямым (или после сбоя микшера — при
/// следующем запуске), громкость всех сессий приложения возвращается на 100% без mute, и exe вычёркивается.
/// </para>
/// Вызывается из одного управляющего потока.
/// </summary>
public sealed class DirectVolumes(Func<string, Func<uint, bool>, IReadOnlyList<IAppSession>> open, IBindingJournal journal) : IDisposable
{
    /// <summary>
    /// Индикатор сессии меряет звук до её громкости (так устроен индикатор эндпоинта; для сессии документация молчит):
    /// пик умножается на громкость, чтобы индикатор узла показывал звук после фейдера, как у остальных узлов.
    /// </summary>
    public const bool MeterIsPreVolume = true;

    private sealed class Entry(DirectRoute route)
    {
        public DirectRoute Route = route;
        public IReadOnlyList<IAppSession> Sessions = [];
        public readonly PeakMeter Meter = new();
    }

    private readonly Dictionary<string, Entry> _entries = new();

    /// <summary>Индикаторы узлов на прямом маршруте (по ID узла). Читаются из любого потока.</summary>
    public IReadOnlyDictionary<string, PeakMeter> Meters { get; private set; } = new Dictionary<string, PeakMeter>();

    /// <summary>Сколько сессий приложения найдено на выходе (по ID узла).</summary>
    public IReadOnlyDictionary<string, int> SessionCounts => _entries.ToDictionary(e => e.Key, e => e.Value.Sessions.Count);

    /// <summary>Последняя ошибка (журнал не записался, громкость не поставилась) по exe.</summary>
    public IReadOnlyDictionary<string, string> Errors => _errors;
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="routes">Прямые маршруты по ID узла (из сборки графа).</param>
    /// <param name="reopen">Перечитать сессии (такт, новая сессия); иначе только поставить громкость уже найденным.</param>
    /// <param name="renderEndpoints">Все устройства воспроизведения: на них ищутся сессии, чью громкость надо вернуть.</param>
    public void Update(IReadOnlyDictionary<string, DirectRoute> routes, AppSnapshot world, bool reopen, IEnumerable<string> renderEndpoints)
    {
        _errors.Clear();
        foreach (string id in _entries.Keys.Where(k => !routes.ContainsKey(k)).ToList())
        {
            Close(_entries[id]);
            _entries.Remove(id);
        }

        foreach (var (id, route) in routes)
        {
            if (!_entries.TryGetValue(id, out var e))
            {
                _entries[id] = e = new Entry(route);
                reopenOne(e);
            }
            else if (!string.Equals(e.Route.EndpointId, route.EndpointId, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(e.Route.Exe, route.Exe, StringComparison.OrdinalIgnoreCase))
            {
                Close(e);
                e.Route = route;
                reopenOne(e);
            }
            else
            {
                e.Route = route;
                if (reopen) reopenOne(e);
            }
            Apply(e);
        }

        void reopenOne(Entry e)
        {
            foreach (var s in e.Sessions) s.Dispose();
            var tree = world.TreeOf(e.Route.Exe);
            e.Sessions = tree.Count == 0 ? [] : open(e.Route.EndpointId, tree.Contains);
        }

        RestorePending(world, renderEndpoints);
        Meters = _entries.ToDictionary(kv => kv.Key, kv => kv.Value.Meter);
    }

    private void Apply(Entry e)
    {
        var r = e.Route;
        if (r.Attenuates && !journal.Apps.Contains(r.Exe, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                journal.Add(r.Exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // без журнала громкость не трогаем: после сбоя её никто не вернёт
                _errors[r.Exe] = Loc.F(CoreStrings.Bind_JournalWriteFailedFormat, ex.Message);
                return;
            }
        }
        foreach (var s in e.Sessions)
        {
            int hr = s.SetVolume(r.Level, r.Muted);
            if (hr < 0) _errors[r.Exe] = Loc.F(CoreStrings.Direct_VolumeFailedFormat, Hr.Describe(hr));
        }
    }

    /// <summary>Вернуть 100% и снять mute у сессий, которые больше не на прямом маршруте (их привязка уже сменилась).</summary>
    private static void Close(Entry e)
    {
        foreach (var s in e.Sessions)
        {
            s.SetVolume(1f, false);
            s.Dispose();
        }
        e.Sessions = [];
    }

    /// <summary>
    /// exe из журнала, которые сейчас не на прямом маршруте с уменьшенной громкостью: если приложение запущено, вернуть
    /// громкость всех его сессий на всех устройствах и вычеркнуть. Не запущено — ждёт в журнале.
    /// </summary>
    private void RestorePending(AppSnapshot world, IEnumerable<string> renderEndpoints)
    {
        var attenuating = _entries.Values.Where(e => e.Route.Attenuates).Select(e => e.Route.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = journal.Apps.Where(a => !attenuating.Contains(a)).ToList();
        if (pending.Count == 0) return;
        var endpoints = renderEndpoints.ToList();
        foreach (string exe in pending)
        {
            var tree = world.TreeOf(exe);
            if (tree.Count == 0) continue;
            bool ok = true;
            foreach (string ep in endpoints)
                foreach (var s in open(ep, tree.Contains))
                {
                    using (s)
                        ok &= s.SetVolume(1f, false) >= 0;
                }
            if (!ok) continue;
            try
            {
                journal.Remove(exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // громкость уже вернули; лишняя запись даст лишь повторный сброс при следующем запуске
            }
        }
    }

    /// <summary>Индикаторы: пики сессий (≈30 раз в секунду, в управляющем потоке).</summary>
    public void PollMeters()
    {
        foreach (var e in _entries.Values)
        {
            float l = 0, r = 0;
            foreach (var s in e.Sessions)
            {
                var (pl, pr) = s.Peaks();
                if (pl > l) l = pl;
                if (pr > r) r = pr;
            }
            float g = e.Route.Muted ? 0f : MeterIsPreVolume ? e.Route.Level : 1f;
            e.Meter.Push(l * g, r * g);
        }
    }

    public bool Any => _entries.Count > 0;

    /// <summary>Штатный выход: вернуть громкость всем (привязки к этому моменту уже сняты) и почистить журнал.</summary>
    public void RestoreAll(AppSnapshot world, IEnumerable<string> renderEndpoints)
    {
        foreach (var e in _entries.Values) Close(e);
        _entries.Clear();
        RestorePending(world, renderEndpoints);
        Meters = new Dictionary<string, PeakMeter>();
    }

    public void Dispose()
    {
        foreach (var e in _entries.Values)
            foreach (var s in e.Sessions) s.Dispose();
        _entries.Clear();
    }
}
