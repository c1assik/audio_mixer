using Mixer.Core.Config;

namespace Mixer.Core.Editing;

/// <summary>
/// История правок графа для Ctrl+Z / Ctrl+Y: неизменяемые снимки GraphConfig, не больше <see cref="Capacity"/> шагов.
/// Непрерывная правка (фейдер, колесо над пилюлей, перетаскивание узла) склеивается в один шаг по ключу:
/// правки с тем же ключом, пришедшие не позже <see cref="MergeWindow"/> после предыдущей, заменяют текущий снимок.
/// </summary>
public sealed class GraphHistory
{
    public const int Capacity = 50;
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1.5);

    private readonly LinkedList<GraphConfig> _undo = new();
    private readonly Stack<GraphConfig> _redo = new();
    private readonly Func<long> _clockMs;
    private string? _mergeKey;
    private long _mergeAt;

    public GraphHistory(GraphConfig initial, Func<long>? clockMs = null)
    {
        Current = initial;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public GraphConfig Current { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoDepth => _undo.Count;

    /// <summary>Текущий снимок сменился (правка, отмена, повтор).</summary>
    public event Action<GraphConfig>? Changed;

    /// <param name="mergeKey">Ключ непрерывной правки («gain:n1»); null — отдельный шаг.</param>
    public void Apply(GraphConfig next, string? mergeKey = null)
    {
        if (ReferenceEquals(next, Current)) return;
        long now = _clockMs();
        bool merge = mergeKey is not null && mergeKey == _mergeKey && now - _mergeAt <= (long)MergeWindow.TotalMilliseconds;
        if (!merge)
        {
            _undo.AddLast(Current);
            while (_undo.Count > Capacity) _undo.RemoveFirst();
        }
        _redo.Clear();
        _mergeKey = mergeKey;
        _mergeAt = now;
        Current = next;
        Changed?.Invoke(next);
    }

    /// <summary>Закончить непрерывную правку (отпустили мышь): следующая правка — новый шаг.</summary>
    public void EndMerge() => _mergeKey = null;

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Current);
        Current = _undo.Last!.Value;
        _undo.RemoveLast();
        _mergeKey = null;
        Changed?.Invoke(Current);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.AddLast(Current);
        Current = _redo.Pop();
        _mergeKey = null;
        Changed?.Invoke(Current);
        return true;
    }
}
