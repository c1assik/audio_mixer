using System.Collections;
using System.Windows;
using System.Windows.Media;
using Mixer.App.Ui;
using Mixer.Core.Config;
using Mixer.Core.Editing;

namespace Mixer.Tests;

/// <summary>Этап 7 (v2): темы — одинаковые ключи, стили собираются с каждой темой, контраст Daylight; раскладка стойки Modular.</summary>
public class ThemeTests
{
    private static ResourceDictionary Load(string theme) => new() { Source = ThemeManager.DictionaryUri(theme) };

    [Fact]
    public void All_themes_have_the_same_keys()
    {
        WpfThread.Run(() =>
        {
            var keys = ThemeManager.All.ToDictionary(t => t, t => Load(t).Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet());
            var graphite = keys[ThemeManager.Graphite];
            Assert.True(graphite.Count > 60);
            foreach (var (theme, set) in keys)
            {
                Assert.True(set.SetEquals(graphite),
                    $"{theme}: missing [{string.Join(", ", graphite.Except(set))}], extra [{string.Join(", ", set.Except(graphite))}]");
            }
        });
    }

    [Fact]
    public void Styles_resolve_every_resource_with_each_theme()
    {
        WpfThread.Run(() =>
        {
            foreach (string theme in ThemeManager.All)
            {
                // как в App.OnStartup: тема, потом стили; пропущенный ключ со StaticResource — исключение при разборе
                var app = new ResourceDictionary();
                app.MergedDictionaries.Add(Load(theme));
                var old = Application.Current.Resources;
                Application.Current.Resources = app;
                try
                {
                    var styles = new ResourceDictionary { Source = ThemeManager.StylesUri };
                    app.MergedDictionaries.Add(styles);
                    foreach (DictionaryEntry e in styles) Assert.NotNull(e.Value); // ленивые значения создаются здесь
                    var window = new Mixer.App.Views.SettingsView();
                    Assert.NotNull(window);
                }
                finally
                {
                    Application.Current.Resources = old;
                }
            }
        });
    }

    private static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void Daylight_keeps_text_at_4_5_and_wires_and_meters_at_3_to_1()
    {
        WpfThread.Run(() =>
        {
            var d = Load(ThemeManager.Daylight);
            Color C(string key) => (Color)d[key + "Color"];
            foreach (string bg in new[] { "Bg", "Card", "Bar" })
            {
                foreach (string text in new[] { "Text", "Text2", "Muted" })
                    Assert.True(Contrast(C(text), C(bg)) >= 4.5, $"{text} on {bg}: {Contrast(C(text), C(bg)):F2}");
                for (int i = 0; i < 8; i++)
                    Assert.True(Contrast(C("Source" + i), C(bg)) >= 3, $"Source{i} on {bg}: {Contrast(C("Source" + i), C(bg)):F2}");
                foreach (string mark in new[] { "Accent", "Green", "Red", "Yellow", "WireNeutral", "WireOff" })
                    Assert.True(Contrast(C(mark), C(bg)) >= 3, $"{mark} on {bg}: {Contrast(C(mark), C(bg)):F2}");
            }
            // индикатор: зелёный на дорожке
            Assert.True(Contrast(C("Green"), C("MeterTrack")) >= 3, $"Green on MeterTrack: {Contrast(C("Green"), C("MeterTrack")):F2}");
        });
    }

    [Fact]
    public void Source_colours_keep_their_place_in_the_palette()
    {
        WpfThread.Run(() =>
        {
            var old = Application.Current.Resources;
            var app = new ResourceDictionary();
            app.MergedDictionaries.Add(Load(ThemeManager.Daylight));
            Application.Current.Resources = app;
            try
            {
                var stored = (Color)ColorConverter.ConvertFromString(ThemeColors.Canonical[1]);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#B85A10"), ThemeColors.Source(stored)); // тот же второй цвет, но темнее
                var own = Color.FromRgb(1, 2, 3);
                Assert.Equal(own, ThemeColors.Source(own)); // свой цвет — как есть
            }
            finally
            {
                Application.Current.Resources = old;
            }
        });
    }

    // ── Modular ──

    private static GraphConfig Streamer() => new()
    {
        Nodes =
        [
            new NodeConfig { Id = "out2", Kind = NodeKind.DeviceOutput, Name = "Stream", Y = 300 },
            new NodeConfig { Id = "mic", Kind = NodeKind.DeviceInput, Name = "Mic", Y = 60 },
            new NodeConfig { Id = "game", Kind = NodeKind.App, Name = "Game", Exe = "game.exe", Y = 210 },
            new NodeConfig { Id = "bus", Kind = NodeKind.Bus, Name = "Mix", Y = 100 },
            new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput, Name = "Headphones", Y = 60 },
        ],
        Wires =
        [
            new WireConfig { From = "game", To = "bus" }, new WireConfig { From = "mic", To = "bus" },
            new WireConfig { From = "bus", To = "hp" }, new WireConfig { From = "bus", To = "out2" }, new WireConfig { From = "mic", To = "hp" },
        ],
    };

    [Fact]
    public void Rack_orders_modules_by_column_then_by_y_and_gives_each_input_a_jack_plus_a_free_one()
    {
        var r = RackLayout.Compute(Streamer(), 3000);
        Assert.Equal(1, r.Rows);
        Assert.Equal(["mic", "game", "bus", "hp", "out2"], r.Modules.Values.OrderBy(m => m.X).Select(m => m.NodeId));
        var bus = r.Modules["bus"];
        // IN 1 — от самого левого модуля, затем свободное гнездо и OUT
        Assert.Equal(["mic", "game", null], bus.Inputs.Select(j => j.From));
        Assert.NotNull(bus.Output);
        Assert.Equal(RackLayout.ModuleWidth + RackLayout.JackPitch, bus.Width); // четыре гнезда — модуль шире
        Assert.Equal(RackLayout.ModuleWidth, r.Modules["hp"].Width); // три гнезда — обычная ширина
        Assert.Empty(r.Modules["mic"].Inputs);
        Assert.Equal(r.Modules["mic"].X + RackLayout.ModuleWidth / 2, r.Modules["mic"].Output!.X);
        // между колонками — промежуток
        Assert.True(bus.X - (r.Modules["game"].X + r.Modules["game"].Width) >= RackLayout.ColumnGap);

        // кабели: из OUT источника в его IN; два кабеля из одного OUT — стопкой штекеров
        Assert.Equal(5, r.Cables.Count);
        var micCables = r.Cables.Where(c => c.From == "mic").ToList();
        Assert.Equal([0, 1], micCables.Select(c => c.Plug).Order());
        var toBus = r.Cables.Single(c => c.From == "game" && c.To == "bus");
        Assert.Equal(bus.Inputs[1].X, toBus.BX);
    }

    [Fact]
    public void Rack_wraps_rows_to_the_window_and_reorders_by_swapping_neighbours()
    {
        var g = Streamer();
        var narrow = RackLayout.Compute(g, 600);
        Assert.True(narrow.Rows > 1);
        Assert.All(narrow.Modules.Values, m => Assert.True(m.X + m.Width <= Math.Max(600, m.Width + RackLayout.Left * 2)));
        Assert.Equal(narrow.Modules.Values.Select(m => m.Row).Max() + 1, narrow.Rows);

        var wide = RackLayout.Compute(g, 3000);
        var game = wide.Modules["game"];
        // бросили микрофон на место игры — меняются местами; в чужую колонку — никак
        Assert.Equal("game", RackLayout.MoveTarget(wide, "mic", game.X + game.Width / 2, game.Y + 100));
        Assert.Null(RackLayout.MoveTarget(wide, "mic", wide.Modules["bus"].X + 20, game.Y + 100));
        Assert.Null(RackLayout.MoveTarget(wide, "mic", wide.Modules["mic"].X + 10, game.Y + 100));
    }

    [Fact]
    public void Patch_cable_sags_by_50_plus_0_22_of_its_length_and_hit_testing_follows_it()
    {
        WpfThread.Run(() =>
        {
            var a = new Point(100, 500);
            var b = new Point(700, 500);
            bool before = WireGeometry.Patch;
            WireGeometry.Patch = true;
            try
            {
                var mid = WireGeometry.At(a, b, 0.5);
                Assert.Equal(500 + RackLayout.Sag(600), mid.Y, 3);
                Assert.Equal(50 + 0.22 * 600, RackLayout.Sag(600), 6);
                var (d, t) = WireGeometry.Nearest(a, b, new Point(mid.X, mid.Y + 3));
                Assert.True(d < 4);
                Assert.Equal(0.5, t, 1);
                Assert.True(WireGeometry.Nearest(a, b, new Point(400, 500)).Distance > 100); // по хорде кабеля нет
            }
            finally
            {
                WireGeometry.Patch = before;
            }
            Assert.Equal(500, WireGeometry.At(a, b, 0.5).Y, 3); // Graphite: Безье без провиса
        });
    }

    [Fact]
    public void Knob_scale_and_led_meter()
    {
        Assert.Equal(0.75, Knob.Fraction(0), 6); // 0 дБ — на трёх четвертях хода
        Assert.Equal(0, Knob.Fraction(-60), 6);
        Assert.Equal(1, Knob.Fraction(12), 6);
        foreach (double db in new[] { -60, -30, -6, 0, 3, 12 })
            Assert.Equal(db, Knob.FromFraction(Knob.Fraction(db)), 6);
        Assert.Equal(0, LedMeter.Lit(-60));
        Assert.Equal(1, LedMeter.Lit(-54));
        Assert.Equal(9, LedMeter.Lit(-6));
        Assert.Equal(10, LedMeter.Lit(0));
        Assert.Equal(10, LedMeter.Lit(3));
    }
}
