using System.IO;
using Mixer.Plugins.Host;

namespace Mixer.App.ViewModels;

/// <summary>Сторонний компонент: название, автор и лицензия — заголовок; текст лицензии — по раскрытию.</summary>
public sealed record LicenseRow(string Title, string Text);

/// <summary>
/// «О программе» в настройках: версия и лицензии сторонних компонентов (лицензия VST 3 SDK требует, чтобы её
/// текст был в программе; см. «Форматы и лицензии» в спецификации поддержки VST).
/// </summary>
public static class AboutInfo
{
    public static string Version =>
        typeof(AboutInfo).Assembly.GetName().Version?.ToString(3) ?? "";

    private static readonly Dictionary<string, string> s_titles = new()
    {
        ["VST3_SDK.txt"] = "VST 3 SDK · Steinberg Media Technologies GmbH · MIT",
        ["nlohmann_json.txt"] = "JSON for Modern C++ · Niels Lohmann · MIT",
        ["Oswald-OFL.txt"] = "Oswald · The Oswald Project Authors · SIL OFL 1.1",
    };

    // после s_titles: статические поля инициализируются по порядку
    public static IReadOnlyList<LicenseRow> Licenses { get; } = Load();

    private static IReadOnlyList<LicenseRow> Load()
    {
        var all = ThirdPartyLicenses.All.ToList();
        using (var s = typeof(AboutInfo).Assembly.GetManifestResourceStream("licenses/Oswald-OFL.txt"))
            if (s != null)
                all.Add(("Oswald-OFL.txt", new StreamReader(s).ReadToEnd()));
        // порядок — как в s_titles: VST 3 SDK первым
        var order = s_titles.Keys.ToList();
        return all.OrderBy(l => order.IndexOf(l.Name) is var i && i >= 0 ? i : int.MaxValue)
            .Select(l => new LicenseRow(s_titles.GetValueOrDefault(l.Name, l.Name), l.Text.Trim())).ToArray();
    }
}
