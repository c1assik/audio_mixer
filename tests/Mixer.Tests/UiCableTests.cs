using Mixer.App.Ui;
using Mixer.App.ViewModels;
using Mixer.Audio.Interop;
using Mixer.Core.Apps;
using Mixer.Core.Config;
using Mixer.Core.Editing;
using static Mixer.Tests.CableTests;

namespace Mixer.Tests;

/// <summary>Этап 6 (v2): кабели и помощник в интерфейсе — палитра, карточка, подвал, шаги помощника.</summary>
public class UiCableTests
{
    private static readonly List<EndpointInfo> Devices = [Hp, Mic, CableIn, Cable16, CableOut];

    private static readonly SourceCatalog Catalog = new(Devices,
    [
        new AppEntry("spotify.exe", "Spotify", null, 1, HasSession: true, Playing: true),
        new AppEntry("obs64.exe", "OBS Studio", null, 1, HasSession: false, Playing: false),
        new AppEntry("game.exe", "Game", null, 1, HasSession: false, Playing: false),
    ], null, VoicemeeterRunning: false);

    internal static (MainViewModel Vm, GraphStore Store) Make(GraphConfig g, IReadOnlyList<EndpointInfo>? devices = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "mixer-tests", Guid.NewGuid().ToString("N"), "graph.json");
        var store = new GraphStore(path, g);
        var prefs = Mixer.App.Preferences.Load(Mixer.App.Preferences.PathFor(path), System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), out _);
        var vm = new MainViewModel(g, store, host: null, offlineDevices: devices ?? Devices)
        {
            Preferences = prefs,
            CatalogOverride = () => Task.FromResult(Catalog with { Devices = devices ?? Devices }),
        };
        return (vm, store);
    }

    [Fact]
    public void Palette_shows_a_cable_as_one_row_with_its_role()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make(new GraphConfig());
            using var _ = store;
            var outs = new PaletteViewModel(vm, PaletteMode.Outputs, null, Task.FromResult(Catalog));
            Assert.Equal("ВЫХОДЫ", outs.Rows[0].Header); // устройства по умолчанию — наверху, кабели — после выходов
            int cablesAt = outs.Rows.ToList().FindIndex(r => r.Header == "ВИРТУАЛЬНЫЕ КАБЕЛИ");
            var mic = outs.Rows[cablesAt + 1];
            Assert.Equal("CABLE (VB-Audio)", mic.Title);
            Assert.Contains("микшер играет в CABLE Input", mic.Subtitle);
            Assert.Equal(CableIn.Id, mic.Template!.EndpointId);
            Assert.Equal(CableRole.VirtualMic, mic.Template.Cable!.Role);
            // основной вход кабеля не повторяется среди устройств, второй вход — остаётся
            Assert.Single(outs.Rows, r => r.Template?.EndpointId == CableIn.Id);
            Assert.Contains(outs.Rows, r => r.Template?.EndpointId == Cable16.Id);

            var src = new PaletteViewModel(vm, PaletteMode.Sources, null, Task.FromResult(Catalog));
            int at = src.Rows.ToList().FindIndex(r => r.Header is not null && r.Header.Contains("кабели", StringComparison.OrdinalIgnoreCase));
            var input = src.Rows[at + 1];
            Assert.Equal(CableOut.Id, input.Template!.EndpointId);
            Assert.Equal(CableRole.VirtualInput, input.Template.Cable!.Role);
            Assert.Contains("приложение играет в CABLE Input", input.Subtitle);

            outs.Commit(mic);
            var node = vm.Nodes.Single();
            Assert.Equal("CABLE", node.Name);
            Assert.True(node.IsVirtualMic);
            vm.Dispose();
        });
    }

    [Fact]
    public void Card_shows_the_role_and_who_records_from_the_cable()
    {
        WpfThread.Run(() =>
        {
            var g = new GraphConfig
            {
                Nodes =
                [
                    new NodeConfig { Id = "s", Kind = NodeKind.DeviceOutput, Name = "Микрофон стрима", EndpointId = CableIn.Id, EndpointName = CableIn.Name, MicFor = ["obs64.exe"] },
                    new NodeConfig { Id = "g", Kind = NodeKind.DeviceInput, Name = "Игра", EndpointId = CableOut.Id, EndpointName = CableOut.Name },
                    new NodeConfig { Id = "hp", Kind = NodeKind.DeviceOutput, Name = "Наушники", EndpointId = Hp.Id, EndpointName = Hp.Name },
                ],
            };
            var (vm, store) = Make(g);
            using var _ = store;
            var s = vm.FindNode("s")!;
            Assert.Equal("кабель-микрофон · CABLE", s.Subtitle);
            Assert.Equal("Микрофон для: OBS Studio", s.MicForText);
            Assert.False(s.ShowFx); // в строке обработки — кому назначен кабель
            Assert.Contains("CABLE Output", s.Tooltip);
            Assert.Contains("виртуальный микрофон", s.Tooltip);
            Assert.Contains("OBS Studio: не запущено", s.MicForTip);
            Assert.Contains("По умолчанию", s.MicForTip);
            Assert.Equal("кабель-вход · CABLE", vm.FindNode("g")!.Subtitle);
            Assert.False(vm.FindNode("hp")!.IsCable);
            Assert.Equal("Кабели: CABLE (Микрофон стрима, Игра)", vm.CableFooter(Devices).Text);

            vm.SetMicFor(s, "Discord", true);
            Assert.Equal("Микрофон для: OBS Studio, Discord", s.MicForText);
            vm.UndoCommand.Execute(null);
            Assert.Equal("Микрофон для: OBS Studio", s.MicForText);
            vm.Dispose();
        });
    }

    [Fact]
    public void Footer_lists_free_cables_and_says_where_to_get_one()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make(new GraphConfig());
            using var _ = store;
            Assert.Equal("Кабели: CABLE свободен · CABLE-A свободен", vm.CableFooter([Hp, CableIn, CableOut, AIn, AOut]).Text);
            Assert.Equal("VB-Cable не найден", vm.CableFooter([Hp, Mic]).Text);
            vm.Dispose();
        });
    }

    [Fact]
    public void Setup_walks_three_steps_and_builds_the_graph_in_one_undo_step()
    {
        WpfThread.Run(() =>
        {
            var old = new GraphConfig { Nodes = [new NodeConfig { Id = "x", Kind = NodeKind.DeviceOutput, Name = "Старое", EndpointId = Hp.Id, EndpointName = Hp.Name }] };
            var (vm, store) = Make(old);
            using var _ = store;
            vm.OpenSetup();
            var s = vm.Setup!;
            Assert.True(vm.HasOverlay);
            Assert.Equal("Шаг 1 из 3", s.StepText);
            Assert.Equal(Hp, s.Headphones!.Device); // по умолчанию — устройства Windows, кабели не предлагаются
            Assert.Equal(Mic, s.Microphone!.Device);
            Assert.DoesNotContain(s.Outputs, d => d.Device == CableIn);
            s.NextCommand.Execute(null);

            // стрим и звонок: нужно 2 кабеля, есть 1 — дальше нельзя
            s.HeadphonesOnly = false;
            s.Stream = true;
            Assert.False(s.CablesShort);
            Assert.Contains("CABLE (VB-Audio)", s.CablesText);
            s.Call = true;
            Assert.True(s.CablesShort);
            Assert.Contains("нужно 2 виртуальных кабеля", s.CablesText);
            Assert.Contains("Найдено: 1 — CABLE (VB-Audio)", s.CablesText);
            Assert.False(s.NextCommand.CanExecute(null));
            s.Call = false;
            Assert.True(s.NextCommand.CanExecute(null));
            s.NextCommand.Execute(null);

            // шаг 3: приложения из каталога, кроме OBS и мессенджеров
            Assert.True(s.IsLast);
            Assert.Equal(["Game", "Spotify"], s.Apps.Select(a => a.Name).Order());
            Assert.True(s.ReplacesGraph);
            s.Apps.Single(a => a.Exe == "spotify.exe").IsChecked = true;
            s.FinishCommand.Execute(null);

            Assert.Null(vm.Setup);
            var g = vm.Graph;
            Assert.Contains(g.Nodes, n => n.MicFor?.Contains("obs64.exe") == true && n.EndpointId == CableIn.Id);
            Assert.Contains(g.Nodes, n => n.Exe == "spotify.exe");
            Assert.DoesNotContain(g.Nodes, n => n.Id == "x" && n.Name == "Старое");
            Assert.Contains("Выберите в OBS Studio микрофон «По умолчанию»", vm.Toast);
            Assert.True(vm.Preferences!.SetupShown);

            vm.UndoCommand.Execute(null); // одна правка
            Assert.Equal("Старое", Assert.Single(vm.Graph.Nodes).Name);
            vm.Dispose();
        });
    }

    [Fact]
    public void Setup_is_offered_once_on_an_empty_graph()
    {
        WpfThread.Run(() =>
        {
            var (vm, store) = Make(new GraphConfig());
            using var _ = store;
            vm.OfferSetup();
            Assert.NotNull(vm.Setup);
            vm.Setup!.SkipCommand.Execute(null);
            Assert.Null(vm.Setup);
            vm.OfferSetup();
            Assert.Null(vm.Setup); // уже предлагался

            var (full, store2) = Make(new GraphConfig { Nodes = [new NodeConfig { Id = "a", Kind = NodeKind.System, Name = "S" }] });
            using var __ = store2;
            full.OfferSetup();
            Assert.Null(full.Setup); // схема не пустая
            vm.Dispose();
            full.Dispose();
        });
    }

    [Fact]
    public void Autostart_writes_the_run_value_with_the_minimized_switch()
    {
        string key = @"Software\MixerTests\" + Guid.NewGuid().ToString("N");
        string before = Autostart.KeyPath;
        Autostart.KeyPath = key;
        try
        {
            Assert.False(Autostart.IsEnabled(@"C:\Apps\Mixer.exe"));
            Autostart.Set(true, @"C:\Apps\Mixer.exe");
            Assert.True(Autostart.IsEnabled(@"C:\Apps\Mixer.exe"));
            Assert.False(Autostart.IsEnabled(@"D:\Other\Mixer.exe")); // другая копия программы — не наш автозапуск
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal("\"C:\\Apps\\Mixer.exe\" --minimized", k!.GetValue(Autostart.ValueName));
            Autostart.Set(true, @"C:\Apps\Mixer.exe", @"D:\my graph.json");
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal("\"C:\\Apps\\Mixer.exe\" --minimized --graph \"D:\\my graph.json\"", k!.GetValue(Autostart.ValueName));
            Autostart.Set(false, @"C:\Apps\Mixer.exe");
            Assert.False(Autostart.IsEnabled(@"C:\Apps\Mixer.exe"));
        }
        finally
        {
            Autostart.KeyPath = before;
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\MixerTests", throwOnMissingSubKey: false);
        }
    }
}
