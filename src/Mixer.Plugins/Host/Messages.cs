using System.Text.Json.Nodes;

namespace Mixer.Plugins.Host;

/// <summary>Что загрузить в хост: файл VST3 (и класс в нём) или встроенный тестовый «плагин».</summary>
public sealed record PluginLoad
{
    /// <summary>Путь к бандлу .vst3 или однофайловому .vst3.</summary>
    public string? Path { get; init; }
    /// <summary>GUID класса (32 hex-цифры); пусто — первый аудиоэффект модуля.</summary>
    public string? ClassId { get; init; }
    /// <summary>Встроенный: copy, gain, sleep, crash, nan, latency (тесты и замеры).</summary>
    public string? Builtin { get; init; }
    public BuiltinOptions? Options { get; init; }
    public int SampleRate { get; init; } = 48000;
    /// <summary>«Ровные блоки»: хост нарезает поток по 256 кадров ценой такой же задержки.</summary>
    public bool EvenBlocks { get; init; }
    /// <summary>Состояние из прошлой сессии (формат — <see cref="PluginState"/>).</summary>
    public byte[]? State { get; init; }

    public static PluginLoad Vst3(string path, string? classId = null, int sampleRate = 48000) =>
        new() { Path = path, ClassId = classId, SampleRate = sampleRate };

    public static PluginLoad Test(string builtin, BuiltinOptions? options = null, int sampleRate = 48000) =>
        new() { Builtin = builtin, Options = options, SampleRate = sampleRate };
}

/// <summary>Настройки встроенных плагинов хоста (см. builtin_processors.h).</summary>
public sealed record BuiltinOptions
{
    public bool Mono { get; init; }
    public int SleepMs { get; init; } = 50;
    public int After { get; init; }
    public int Latency { get; init; } = 512;
    /// <summary>latency: restartComponent сразу после активации (как CHOWTapeModel), первый process — 20 мс.</summary>
    public bool RestartOnLoad { get; init; }
}

/// <summary>Параметр плагина: для списка в панели и для зеркала правок в окне.</summary>
public sealed record PluginParam(
    uint Id,
    string Title,
    string Units,
    int Steps,
    double Default,
    double Value,
    string Text,
    bool ReadOnly,
    bool Hidden,
    bool Bypass,
    bool List)
{
    internal static PluginParam From(JsonNode n) => new(
        (uint)n["id"]!,
        (string?)n["title"] ?? "",
        (string?)n["units"] ?? "",
        (int?)n["steps"] ?? 0,
        (double?)n["default"] ?? 0,
        (double?)n["value"] ?? 0,
        (string?)n["text"] ?? "",
        (bool?)n["readOnly"] ?? false,
        (bool?)n["hidden"] ?? false,
        (bool?)n["bypass"] ?? false,
        (bool?)n["list"] ?? false);

    internal static IReadOnlyList<PluginParam> ListFrom(JsonNode? n) =>
        n is JsonArray a ? a.Where(x => x != null).Select(x => From(x!)).ToArray() : Array.Empty<PluginParam>();
}

/// <summary>Новое значение параметра: правка в окне плагина или эхо на setParam.</summary>
public readonly record struct PluginParamValue(uint Id, double Value, string Text);

/// <summary>Положение и размер окна редактора: угол окна и размер содержимого, экранные пиксели.</summary>
public readonly record struct EditorBounds(int X, int Y, int Width, int Height)
{
    internal static EditorBounds From(JsonNode n) =>
        new((int?)n["x"] ?? 0, (int?)n["y"] ?? 0, (int?)n["w"] ?? 0, (int?)n["h"] ?? 0);
}

/// <summary>Плагин загружен и активирован: его возможности (ответ ready).</summary>
public sealed record PluginInfo(
    string Name,
    string Vendor,
    string Version,
    string? ClassId,
    int LatencySamples,
    int PluginLatencySamples,
    int InChannels,
    int OutChannels,
    bool EvenBlocks,
    bool HasEditor,
    IReadOnlyList<PluginParam> Params)
{
    /// <summary>Параметр обхода самого плагина (kIsBypass), если есть.</summary>
    public PluginParam? BypassParam => Params.FirstOrDefault(p => p.Bypass);

    internal static PluginInfo From(JsonObject m) => new(
        (string?)m["name"] ?? "",
        (string?)m["vendor"] ?? "",
        (string?)m["version"] ?? "",
        (string?)m["classId"],
        (int?)m["latency"] ?? 0,
        (int?)m["pluginLatency"] ?? 0,
        (int?)m["inChannels"] ?? 2,
        (int?)m["outChannels"] ?? 2,
        (bool?)m["evenBlocks"] ?? false,
        (bool?)m["hasEditor"] ?? false,
        PluginParam.ListFrom(m["params"]));
}

/// <summary>Задержка или шины изменились (restartComponent, ровные блоки, новая частота).</summary>
public readonly record struct PluginLayout(int LatencySamples, int InChannels, int OutChannels);

/// <summary>
/// Формат состояния плагина (он же — файл <c>.state</c>): длина (uint32, LE) и данные IComponent::getState,
/// затем длина и данные IEditController::getState.
/// </summary>
public static class PluginState
{
    public static byte[] Pack(ReadOnlySpan<byte> component, ReadOnlySpan<byte> controller)
    {
        var blob = new byte[8 + component.Length + controller.Length];
        BitConverter.TryWriteBytes(blob.AsSpan(0, 4), (uint)component.Length);
        component.CopyTo(blob.AsSpan(4));
        BitConverter.TryWriteBytes(blob.AsSpan(4 + component.Length, 4), (uint)controller.Length);
        controller.CopyTo(blob.AsSpan(8 + component.Length));
        return blob;
    }

    /// <summary>false — не наш формат.</summary>
    public static bool TryUnpack(ReadOnlySpan<byte> blob, out byte[] component, out byte[] controller)
    {
        component = controller = Array.Empty<byte>();
        if (blob.Length < 4) return false;
        uint n1 = BitConverter.ToUInt32(blob[..4]);
        if (n1 > blob.Length - 8) return false;
        int pos = 4 + (int)n1;
        uint n2 = BitConverter.ToUInt32(blob.Slice(pos, 4));
        if (n2 != blob.Length - pos - 4) return false;
        component = blob.Slice(4, (int)n1).ToArray();
        controller = blob.Slice(pos + 4).ToArray();
        return true;
    }
}

/// <summary>Почему хост закончился.</summary>
public enum HostExitReason
{
    /// <summary>Вышел сам по нашей просьбе (quit).</summary>
    Quit,
    /// <summary>Процесс завершился сам: упал (код 5 — исключение в плагине) или вышел.</summary>
    Crashed,
    /// <summary>Не отвечал на ping: процесс завершён принудительно.</summary>
    Hung,
    /// <summary>Процесс завершён по нашей команде (Kill).</summary>
    Killed,
}

public sealed record HostExit(HostExitReason Reason, int? ExitCode, string Message);

/// <summary>Хост ответил ошибкой на запрос или не смог загрузить плагин.</summary>
public sealed class PluginHostException(string message) : Exception(message);
