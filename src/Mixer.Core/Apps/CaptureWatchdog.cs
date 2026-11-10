namespace Mixer.Core.Apps;

/// <summary>
/// Сторожевой таймер захвата приложений. Если служба аудио нездорова, захват может молча перестать получать пакеты
/// (этап 0: GetNextPacketSize висел без единого пакета). Правило спецификации: активная сессия у цели есть, а пакетов
/// нет 3 с — захват пересоздаётся. Пакеты с флагом SILENT — активность; без активной сессии отсутствие пакетов — норма.
/// Сессия должна быть активной уже 3 с, чтобы только что запущенное воспроизведение не приняли за сбой.
/// </summary>
public sealed class CaptureWatchdog
{
    public const double TimeoutSeconds = 3;

    private readonly Dictionary<string, double> _activeSince = new();

    /// <param name="sourceId">ID источника в движке.</param>
    /// <param name="now">Текущее время, с (монотонное).</param>
    /// <param name="sessionActive">У цели захвата сейчас есть активная аудиосессия.</param>
    /// <param name="nodeOpen">Узел работает или открывается (не в паузе между попытками).</param>
    /// <param name="secondsSinceActivity">Секунд с последнего пакета или с начала открытия.</param>
    /// <returns>true — захват пора пересоздать.</returns>
    public bool Check(string sourceId, double now, bool sessionActive, bool nodeOpen, double secondsSinceActivity)
    {
        if (!sessionActive || !nodeOpen)
        {
            _activeSince.Remove(sourceId);
            return false;
        }
        if (!_activeSince.TryGetValue(sourceId, out double since))
            _activeSince[sourceId] = since = now;
        return now - since >= TimeoutSeconds && secondsSinceActivity >= TimeoutSeconds;
    }

    /// <summary>Забыть источники, которых больше нет.</summary>
    public void Retain(IEnumerable<string> alive)
    {
        var keep = alive.ToHashSet();
        foreach (string id in _activeSince.Keys.Where(k => !keep.Contains(k)).ToList()) _activeSince.Remove(id);
    }
}
