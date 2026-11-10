using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Mixer.App;
using Mixer.App.Resources;
using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Audio.Interop;
using Mixer.Audio.Resources;
using Mixer.Core;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using Mixer.Core.Resources;

namespace Mixer.Tests;

/// <summary>
/// Тесты по умолчанию идут на русском: так проверки текстов не зависят от языка Windows на машине, где их запускают.
/// Английский проверяется явно (<see cref="Culture.Use"/>).
/// </summary>
internal static class Culture
{
    [ModuleInitializer]
    internal static void Init()
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = ru;
        CultureInfo.DefaultThreadCurrentUICulture = ru;
        CultureInfo.CurrentCulture = ru;
        CultureInfo.CurrentUICulture = ru;
    }

    /// <summary>Язык интерфейса и чисел на текущем потоке до Dispose.</summary>
    public static IDisposable Use(string name) => new Scope(CultureInfo.GetCultureInfo(name));

    private sealed class Scope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture, _ui = CultureInfo.CurrentUICulture;

        public Scope(CultureInfo c)
        {
            CultureInfo.CurrentCulture = c;
            CultureInfo.CurrentUICulture = c;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _ui;
        }
    }
}

public class LocalizationTests
{
    public static TheoryData<string> Resources => new() { nameof(Strings), nameof(CoreStrings), nameof(AudioStrings) };

    private static (ResourceManager Rm, Type Class) Resource(string name) => name switch
    {
        nameof(Strings) => (Strings.ResourceManager, typeof(Strings)),
        nameof(CoreStrings) => (CoreStrings.ResourceManager, typeof(CoreStrings)),
        _ => (AudioStrings.ResourceManager, typeof(AudioStrings)),
    };

    private static Dictionary<string, string> Read(ResourceManager rm, CultureInfo c) =>
        rm.GetResourceSet(c, createIfNotExists: true, tryParents: false)!.Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    private static string Placeholders(string s) =>
        string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());

    [Theory]
    [MemberData(nameof(Resources))]
    public void Both_languages_have_the_same_keys_and_placeholders(string name)
    {
        var rm = Resource(name).Rm;
        var en = Read(rm, CultureInfo.InvariantCulture);
        var ru = Read(rm, CultureInfo.GetCultureInfo("ru"));
        Assert.NotEmpty(en);
        Assert.Equal(en.Keys.Order(), ru.Keys.Order());
        foreach (var (key, value) in en)
        {
            Assert.True(Placeholders(value) == Placeholders(ru[key]), $"{name}.{key}: «{value}» и «{ru[key]}»");
            if (key.StartsWith("Plural_", StringComparison.Ordinal))
            {
                Assert.True(value.Split('|').Length == 2, $"{name}.{key}: английских форм две");
                Assert.True(ru[key].Split('|').Length == 3, $"{name}.{key}: русских форм три");
            }
            // в английском тексте не должно остаться русского (недопереведённая строка)
            Assert.False(Regex.IsMatch(value, "[А-Яа-яЁё]"), $"{name}.{key}: «{value}»");
        }
    }

    /// <summary>Классы строк пишет tools/gen-strings.ps1: после правки .resx его надо запустить.</summary>
    [Theory]
    [MemberData(nameof(Resources))]
    public void Generated_class_matches_the_resx(string name)
    {
        var (rm, cls) = Resource(name);
        var keys = Read(rm, CultureInfo.InvariantCulture).Keys.Order();
        var props = cls.GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).Order();
        Assert.Equal(keys, props);
    }

    [Fact]
    public void English_numbers_use_a_point_and_true_minus()
    {
        using var _ = Culture.Use("en-US");
        Assert.Equal("0.0 dB", Fmt.Gain(0));
        Assert.Equal("−3.0 dB", Fmt.Gain(-3));
        Assert.Equal("+6.0 dB", Fmt.Gain(6));
        Assert.Equal("−∞ dB", Fmt.Gain(-60));
        Assert.Equal("−4.5 dB", Fmt.Pill(-4.5));
        Assert.Equal("48", Fmt.Khz(48000));
        Assert.Equal("44.1", Fmt.Khz(44100));
    }

    [Fact]
    public void Plurals_follow_the_language()
    {
        using (Culture.Use("ru-RU"))
        {
            string forms = Strings.Plural_Source;
            Assert.Equal("1 источник", Fmt.Plural(1, forms));
            Assert.Equal("2 источника", Fmt.Plural(2, forms));
            Assert.Equal("5 источников", Fmt.Plural(5, forms));
            Assert.Equal("11 источников", Fmt.Plural(11, forms));
            Assert.Equal("21 источник", Fmt.Plural(21, forms));
            Assert.Equal("22 источника", Fmt.Plural(22, forms));
            Assert.Equal("1 выключен", Fmt.Plural(1, Strings.Plural_WireOff));
            Assert.Equal("3 выключено", Fmt.Plural(3, Strings.Plural_WireOff));
        }
        using (Culture.Use("en-US"))
        {
            Assert.Equal("1 source", Fmt.Plural(1, Strings.Plural_Source));
            Assert.Equal("2 sources", Fmt.Plural(2, Strings.Plural_Source));
            Assert.Equal("0 wires", Fmt.Plural(0, Strings.Plural_Wire));
            Assert.Equal("1 process", Fmt.Plural(1, Strings.Plural_Process));
            Assert.Equal("6 processes", Fmt.Plural(6, Strings.Plural_Process));
        }
    }

    [Fact]
    public void Language_follows_windows_unless_set_by_option()
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var de = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("ru-RU", AppOptions.PickCulture(null, ru).Name);
        Assert.Equal("en-US", AppOptions.PickCulture(null, de).Name);
        Assert.Equal("en-US", AppOptions.PickCulture(null, CultureInfo.GetCultureInfo("en-GB")).Name);
        Assert.Equal("en-US", AppOptions.PickCulture("en", ru).Name);
        Assert.Equal("ru-RU", AppOptions.PickCulture("RU", de).Name);
        Assert.Equal("ru-RU", AppOptions.PickCulture("auto", ru).Name);
        Assert.Equal("en", AppOptions.Parse(["--no-audio", "--lang", "en"]).Language);
    }

    [Fact]
    public void Core_and_audio_messages_follow_the_ui_language()
    {
        var g = new GraphConfig
        {
            Nodes =
            [
                new NodeConfig { Id = "a", Kind = NodeKind.DeviceInput, Name = "Mic" },
                new NodeConfig { Id = "b", Kind = NodeKind.DeviceInput, Name = "Line" },
            ],
        };
        using (Culture.Use("en-US"))
        {
            Assert.Equal("“Mic”: x", Loc.Subject("Mic", "x"));
            Assert.Equal("a wire goes from a source to an output, and these are two sources", ConnectionRules.Check(g, "a", "b", []).Reason);
            Assert.Equal("float32 48000 Hz 8 ch.", WaveFormat.ProcessLoopback(48000).ToString());
        }
        using (Culture.Use("ru-RU"))
        {
            Assert.Equal("«Mic»: x", Loc.Subject("Mic", "x"));
            Assert.Equal("провод идёт от источника к выходу, а это два источника", ConnectionRules.Check(g, "a", "b", []).Reason);
            Assert.Equal("float32 48000 Гц 8 кан.", WaveFormat.ProcessLoopback(48000).ToString());
        }
    }

    [Fact]
    public void Default_node_names_are_recognised_in_either_language()
    {
        string ru, en;
        using (Culture.Use("ru-RU")) ru = Fmt.DefaultNodeName(NodeKind.DeviceOutput, DefaultRole.Console);
        using (Culture.Use("en-US")) en = Fmt.DefaultNodeName(NodeKind.DeviceOutput, DefaultRole.Console);
        Assert.Equal("Выход по умолчанию", ru);
        Assert.Equal("Default output", en);
        using (Culture.Use("en-US"))
        {
            Assert.True(Fmt.IsDefaultNodeName(ru, NodeKind.DeviceOutput, DefaultRole.Console)); // схему собрали на русской Windows
            Assert.False(Fmt.IsDefaultNodeName("Наушники", NodeKind.DeviceOutput, DefaultRole.Console));
        }
    }

    [Fact]
    public void Interface_is_in_english_on_english_windows()
    {
        var hp = new EndpointInfo("{0.0.0.00000000}.{hp}", "Headphones (Realtek)", EDataFlow.Render, true);
        var mic = new EndpointInfo("{0.0.1.00000000}.{mic}", "Microphone (fifine)", EDataFlow.Capture, true);
        var g = new GraphConfig
        {
            Nodes =
            [
                new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, Name = "Mic", EndpointId = mic.Id, EndpointName = mic.Name, X = 40, Y = 60 },
                new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput, Name = "Headphones", EndpointId = hp.Id, EndpointName = hp.Name, X = GraphLayout.OutputX, Y = 60 },
            ],
            Wires = [new WireConfig { From = "mic", To = "hp", GainDb = -6, Enabled = false }],
        };
        WpfThread.Run(() =>
        {
            using var _ = Culture.Use("en-US");
            string path = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "graph.json");
            using var store = new GraphStore(path, g);
            var vm = new MainViewModel(g, store, host: null, offlineDevices: [hp, mic]);
            Assert.Equal("1 source · 1 output · 1 wire (1 off)", vm.CountsText);
            Assert.Equal("Saved", vm.SaveText);
            Assert.Equal("off", vm.Wires[0].PillText);
            Assert.Equal("input device", vm.FindNode("mic")!.Subtitle);
            Assert.Equal("−6.0 dB", Fmt.Gain(vm.Wires[0].GainDb));

            var p = new PaletteViewModel(vm, PaletteMode.Sources, null,
                Task.FromResult(new Mixer.Core.Apps.SourceCatalog([hp, mic], [], null, VoicemeeterRunning: false)));
            Assert.Equal("Add source", p.Title);
            Assert.Equal(new[] { "INPUTS", "OUTPUT AUDIO (LOOPBACK)", "SPECIAL" }, p.Rows.Where(r => r.IsHeader).Select(r => r.Header));
            Assert.Equal("Default microphone", p.Rows[1].Title);
            vm.Dispose();
        });
    }

    private static string TempSettings() =>
        Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void Language_choice_is_saved_next_to_the_graph()
    {
        Assert.Equal(Path.Combine(@"C:\data\Mixer", "settings.json"), Preferences.PathFor(@"C:\data\Mixer\graph.json"));

        string path = TempSettings();
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var fresh = Preferences.Load(path, ru, out string? error);
        Assert.Null(error);
        Assert.Equal(Preferences.Auto, fresh.Language); // файла нет — как в Windows

        fresh.SetLanguage("en");
        var again = Preferences.Load(path, ru, out error);
        Assert.Null(error);
        Assert.Equal("en", again.Language);
        Assert.Equal("en-US", again.Resolve(again.Language).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => again.SetLanguage("de"));

        File.WriteAllText(path, "{ мусор");
        var broken = Preferences.Load(path, ru, out error);
        Assert.NotNull(error); // испорченный файл не мешает запуску
        Assert.Equal(Preferences.Auto, broken.Language);
    }

    [Fact]
    public void Choosing_a_language_saves_it_and_offers_a_restart()
    {
        using var scope = Culture.Use("ru-RU"); // программа сейчас на русском, Windows тоже на русском
        string path = TempSettings();
        var prefs = Preferences.Load(path, CultureInfo.GetCultureInfo("ru-RU"), out _);
        int restarts = 0;
        var lang = new LanguageSettings(prefs, () => restarts++);

        Assert.Equal(new[] { "auto", "en", "ru" }, lang.Options.Select(o => o.Code));
        Assert.Equal(new[] { "Как в Windows", "English", "Русский" }, lang.Options.Select(o => o.Title));
        Assert.True(lang.Options[0].IsCurrent);
        Assert.False(lang.NeedsRestart);
        Assert.Null(lang.Note);

        lang.ChooseCommand.Execute(lang.Options[1]); // English
        Assert.Equal("en", Preferences.Load(path, CultureInfo.GetCultureInfo("ru-RU"), out _).Language);
        Assert.True(lang.Options[1].IsCurrent);
        Assert.False(lang.Options[0].IsCurrent);
        Assert.True(lang.NeedsRestart);
        Assert.Equal(Strings.Settings_LanguageRestartNote, lang.Note);

        lang.ChooseCommand.Execute(lang.Options[2]); // Русский — тот, что уже на экране
        Assert.False(lang.NeedsRestart);
        Assert.Null(lang.Note);

        lang.ChooseCommand.Execute(lang.Options[1]);
        lang.RestartCommand.Execute(null);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public void Restart_keeps_options_except_language_and_one_off_ones()
    {
        Assert.Equal(new[] { "--no-audio", "--graph", "g.json" },
            Mixer.App.App.RestartArgs(["--lang", "ru", "--no-audio", "--graph", "g.json", "--open", "settings", "--snapshot", "x.png"]));
    }
}
