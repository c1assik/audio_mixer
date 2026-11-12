namespace Mixer.Plugins.Catalog;

/// <summary>Почему модуль не попал в каталог. Первые — «не поддерживается», последние три — «не загрузился».</summary>
public enum ScanProblem
{
    None,
    /// <summary>Модуль загрузился, но в нём нет ни одного аудиомодуля (класса «Audio Module Class»).</summary>
    NoEffects,
    /// <summary>Класс — инструмент, а не эффект.</summary>
    Instrument,
    /// <summary>32-битный модуль.</summary>
    Bits32,
    /// <summary>В бандле нет x64-версии (только ARM и т. п.).</summary>
    NoX64Binary,
    /// <summary>Файл выглядит как VST2: точка входа VSTPluginMain без GetPluginFactory.</summary>
    Vst2,
    LoadFailed,
    Crashed,
    /// <summary>Не ответил за таймаут (часто — окно активации лицензии): «Просканировать с окном».</summary>
    Timeout,
}

public enum SignatureStatus { NotSigned, Valid, Invalid }

/// <summary>Цифровая подпись файла модуля (WinVerifyTrust) и имя подписавшего.</summary>
public sealed record FileSignature(SignatureStatus Status, string? Signer);

/// <summary>Класс фабрики VST3: из moduleinfo.json или от загруженного модуля.</summary>
public sealed record PluginClass(
    string ClassId,
    string Category,
    string Name,
    string Vendor,
    string Version,
    string SdkVersion,
    IReadOnlyList<string> SubCategories)
{
    public const string AudioModuleClass = "Audio Module Class";

    public bool IsAudioModule => Category == AudioModuleClass;
    public bool IsEffect => IsAudioModule && SubCategories.Contains("Fx", StringComparer.OrdinalIgnoreCase);
    public bool IsInstrument => IsAudioModule && !IsEffect &&
        SubCategories.Any(s => s.StartsWith("Instrument", StringComparison.OrdinalIgnoreCase));

    /// <summary>Группы в выборе плагина: подкатегории кроме Fx (Dynamics, EQ, Reverb, Restoration…).</summary>
    public IEnumerable<string> Categories => SubCategories.Where(s => !s.Equals("Fx", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Результат скана одного модуля (бандла .vst3, однофайлового .vst3 или .dll VST2). Ключ кэша — путь, размер и
/// время изменения файла модуля (<see cref="Binary"/>). <see cref="Classes"/> — только аудиомодули.
/// </summary>
public sealed record ModuleScan(
    string Path,
    string? Binary,
    long Size,
    DateTime ModifiedUtc,
    ScanProblem Problem,
    string? Detail,
    IReadOnlyList<PluginClass> Classes,
    bool FromModuleInfo,
    FileSignature? Signature)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool NotLoaded => Problem is ScanProblem.LoadFailed or ScanProblem.Crashed or ScanProblem.Timeout;
    public bool NotSupported => Problem != ScanProblem.None && !NotLoaded;
}

/// <summary>Эффект в каталоге.</summary>
public sealed record CatalogEntry(PluginClass Class, ModuleScan Module);

/// <summary>Строка списка «не поддерживается»: модуль целиком или отдельный класс (инструмент).</summary>
public sealed record CatalogProblem(string Path, string Name, ScanProblem Problem, string? Detail);

/// <summary>Прогресс скана: «проверено <see cref="Checked"/> из <see cref="Total"/>»; Module — только что проверенный.</summary>
public readonly record struct ScanProgress(int Checked, int Total, ModuleScan? Module);
