using System.Globalization;
using Mixer.Audio.Interop;

namespace Mixer.Run;

public sealed class UsageException(string message) : Exception(message);

/// <summary>Разбор «команда позиционные --ключ значение --флаг».</summary>
public sealed class Args
{
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = [];

    public string Command { get; }

    public Args(string[] args)
    {
        Command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                string key = args[i][2..];
                string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
                _opts[key] = value;
            }
            else
            {
                _positional.Add(args[i]);
            }
        }
    }

    public string? Positional(int i) => i < _positional.Count ? _positional[i] : null;
    public bool Has(string key) => _opts.ContainsKey(key);
    public string? Get(string key) => _opts.GetValueOrDefault(key);

    public double GetDouble(string key, double fallback)
    {
        string? v = Get(key);
        if (v is null) return fallback;
        if (double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
        throw new UsageException($"--{key}: «{v}» — не число");
    }

    public string Require(string key) => Get(key) ?? throw new UsageException($"Нужен параметр --{key}");

    /// <summary>Устройство по ID или по части имени (должно подходить ровно одно).</summary>
    public static EndpointInfo FindDevice(IReadOnlyList<EndpointInfo> devices, string query, EDataFlow flow)
    {
        var list = devices.Where(d => d.Flow == flow).ToList();
        var byId = list.FirstOrDefault(e => e.Id.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var exact = list.Where(e => e.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var matches = list.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        string kind = flow == EDataFlow.Capture ? "записи" : "воспроизведения";
        if (matches.Count == 0)
            throw new UsageException($"Устройство {kind} «{query}» не найдено. Доступны:\n" + string.Join("\n", list.Select(e => $"    {e.Name}")));
        throw new UsageException($"«{query}» подходит к нескольким устройствам {kind}, уточните:\n" + string.Join("\n", matches.Select(e => $"    {e.Name}")));
    }
}
