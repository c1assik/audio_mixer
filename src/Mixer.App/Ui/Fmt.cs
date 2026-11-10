using System.Globalization;
using Mixer.App.Resources;
using Mixer.Core.Config;
using DspDb = Mixer.Audio.Dsp.Db;

namespace Mixer.App.Ui;

/// <summary>Числа в интерфейсе — по текущей культуре («−3,0 дБ» по-русски, «−3.0 dB» по-английски), с настоящим минусом «−».</summary>
public static class Fmt
{
    private static CultureInfo C => CultureInfo.CurrentCulture;

    /// <summary>Языки интерфейса: нейтральный (английский) и русский.</summary>
    private static readonly CultureInfo[] UiCultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("ru")];

    private static string Signed(double v, string format, bool plus)
    {
        string abs = Math.Abs(v).ToString(format, C);
        if (abs == 0.0.ToString(format, C)) return abs;
        return v < 0 ? "−" + abs : plus ? "+" + abs : abs;
    }

    private static string Db(string number) => string.Format(C, Strings.Unit_dBFormat, number);

    /// <summary>Фейдер: «0,0 дБ», «−3,0 дБ», «+6,0 дБ», «−∞ дБ».</summary>
    public static string Gain(double db) => Db(db <= DspDb.Min ? "−∞" : Signed(db, "0.0", plus: true));

    /// <summary>Пилюля провода: «0 дБ», «−6 дБ», «−4,5 дБ».</summary>
    public static string Pill(double db) => Db(db <= DspDb.Min ? "−∞" : Signed(db, "0.#", plus: true));

    /// <summary>Слагаемое суммы усилений: «0», «10» (знак ставит вызывающий).</summary>
    public static string Term(double db) => Math.Abs(db).ToString("0.#", C);

    public static string Khz(int rate) => (rate / 1000.0).ToString("0.#", C);

    public static string Ms(double ms) => ms.ToString("0", C);

    public static string Percent(double v) => v.ToString(v < 10 ? "0.#" : "0", C);

    /// <summary>Строка ресурса с подстановками по текущей культуре.</summary>
    public static string F(string format, params object?[] args) => string.Format(C, format, args);

    /// <summary>
    /// «1 источник», «2 источника», «5 источников». forms — строка Plural_* из ресурсов, формы через «|»:
    /// две по-английски (one|other), три по-русски (one|few|many).
    /// </summary>
    public static string Plural(int n, string forms)
    {
        var f = forms.Split('|');
        int i;
        if (f.Length == 3)
        {
            int n10 = n % 10, n100 = n % 100;
            i = n10 == 1 && n100 != 11 ? 0 : n10 is >= 2 and <= 4 && (n100 < 12 || n100 > 14) ? 1 : 2;
        }
        else
        {
            i = n == 1 || f.Length == 1 ? 0 : 1;
        }
        return $"{n.ToString(C)} {f[i]}";
    }

    /// <summary>Первая буква — заглавная (причина из Core вставляется в начало фразы).</summary>
    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], C) + s[1..];

    /// <summary>Имя устройства без драйвера в скобках: «Динамики (fifine)» → «Динамики».</summary>
    public static string ShortDevice(string name)
    {
        int open = name.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 && name.EndsWith(')') ? name[..open] : name;
    }

    private static string DefaultNodeNameKey(NodeKind kind, DefaultRole role) =>
        (kind, role) switch
        {
            (NodeKind.DeviceInput, DefaultRole.Communications) => nameof(Strings.NodeName_DefaultMicComms),
            (NodeKind.DeviceInput, _) => nameof(Strings.NodeName_DefaultMic),
            (NodeKind.Loopback, DefaultRole.Communications) => nameof(Strings.NodeName_DefaultLoopbackComms),
            (NodeKind.Loopback, _) => nameof(Strings.NodeName_DefaultLoopback),
            (_, DefaultRole.Communications) => nameof(Strings.NodeName_DefaultOutputComms),
            _ => nameof(Strings.NodeName_DefaultOutput),
        };

    /// <summary>Имя нового узла «Устройство по умолчанию» (короткое: что это за роль, видно в подписи карточки).</summary>
    public static string DefaultNodeName(NodeKind kind, DefaultRole role) =>
        Strings.ResourceManager.GetString(DefaultNodeNameKey(kind, role), Strings.Culture) ?? "";

    /// <summary>Имя дал микшер на любом из языков интерфейса: схему могли собрать, пока Windows была на другом языке.</summary>
    public static bool IsDefaultNodeName(string name, NodeKind kind, DefaultRole role)
    {
        string key = DefaultNodeNameKey(kind, role);
        return name == DefaultNodeName(kind, role) || UiCultures.Any(c => Strings.ResourceManager.GetString(key, c) == name);
    }

    // ── обработка ──

    /// <summary>«80 Гц», «3,5 кГц», «10 кГц».</summary>
    public static string Hz(double hz) => hz >= 1000
        ? string.Format(C, Strings.Unit_kHzFormat, (hz / 1000).ToString(hz >= 10000 ? "0.#" : "0.##", C))
        : string.Format(C, Strings.Unit_HzFormat, hz.ToString("0", C));

    /// <summary>«+2,5 дБ», «−3,0 дБ», «0,0 дБ».</summary>
    public static string SignedDb(double db) => Db(Signed(db, "0.0", plus: true));

    /// <summary>Ширина без знака: «6,0 дБ».</summary>
    public static string PlainDb(double db) => Db(db.ToString("0.0", C));

    /// <summary>Порог: «−48 dBFS».</summary>
    public static string Dbfs(double db) => string.Format(C, Strings.Unit_dBFSFormat, Signed(db, "0.#", plus: false));

    /// <summary>Уровень индикатора: «−18 dBFS», ниже −60 — «−∞ dBFS».</summary>
    public static string Level(double db) => string.Format(C, Strings.Unit_dBFSFormat, db <= MeterState.FloorDb ? "−∞" : Signed(db, "0", plus: false));

    /// <summary>«8 мс», «0,5 мс».</summary>
    public static string Millis(double ms) => string.Format(C, Strings.Unit_msFormat, ms.ToString(ms < 10 ? "0.#" : "0", C));

    /// <summary>«3 : 1».</summary>
    public static string Ratio(double r) => r.ToString("0.#", C) + " : 1";

    /// <summary>Добротность: «Q 1,0».</summary>
    public static string Q(double q) => "Q " + q.ToString(q < 10 ? "0.0#" : "0", C);

    public static string BandType(Mixer.Audio.Dsp.EqBandType t) => t switch
    {
        Mixer.Audio.Dsp.EqBandType.Peak => Strings.Fx_TypePeak,
        Mixer.Audio.Dsp.EqBandType.LowShelf => Strings.Fx_TypeLowShelf,
        Mixer.Audio.Dsp.EqBandType.HighShelf => Strings.Fx_TypeHighShelf,
        _ => Strings.Fx_TypeOff,
    };

    /// <summary>Округление дБ до 0,1 и притягивание к 0 дБ рядом с нулём.</summary>
    public static double SnapDb(double db, bool snapZero = true)
    {
        db = Math.Clamp(db, DspDb.Min, DspDb.Max);
        if (snapZero && Math.Abs(db) < 0.35) return 0;
        return Math.Round(db, 1);
    }
}
