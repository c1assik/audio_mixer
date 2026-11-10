using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Mixer.Probe.Interop;
using Mixer.Probe.Sys;

namespace Mixer.Probe;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Ui.Cancel.Set();
        };
        NativeMethods.RoInitialize(1); // RO_INIT_MULTITHREADED; S_FALSE, если COM уже в MTA

        PrintHeader();
        if (argv.Length == 0 || argv[0] is "help" or "--help" or "-h" or "/?")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var a = Args.Parse(argv);
            return a.Command switch
            {
                "list" => Commands.List(),
                "capture" => Commands.Capture(a),
                "formats" => Commands.Formats(a),
                "test" => Commands.Test(a),
                "redirect" => Commands.Redirect(a),
                "reset" => Commands.Reset(a),
                "status" => Commands.Status(a),
                "tone" => Commands.Tone(a),
                "wavstat" => Commands.WavStat(a),
                "multi" => Commands.Multi(a),
                _ => throw new UsageException($"Неизвестная команда «{a.Command}»."),
            };
        }
        catch (UsageException ex)
        {
            Ui.Colored(ConsoleColor.Red, "Ошибка: " + ex.Message);
            Console.WriteLine("Справка: Mixer.Probe help");
            return 1;
        }
        catch (AudioException ex)
        {
            Ui.Colored(ConsoleColor.Red, "Ошибка аудио: " + ex.Message);
            return 2;
        }
    }

    private static void PrintHeader()
    {
        var v = Environment.OSVersion.Version;
        string display = "", ubr = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = key?.GetValue("DisplayVersion") as string ?? "";
            ubr = key?.GetValue("UBR")?.ToString() ?? "";
        }
        catch
        {
            // версия нужна только для отчёта
        }
        string win = v.Build >= 22000 ? "Windows 11" : "Windows 10";
        Console.WriteLine($"Mixer.Probe · этап 0 · {win} {display}, сборка {v.Build}.{ubr}, {RuntimeInformation.ProcessArchitecture}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""

        Команды:
          list                                   устройства, аудиосессии, доступность API
          capture --exe <имя> [--seconds 10]     захват звука приложения в WAV
          capture --system [--seconds 10]        весь звук системы, кроме самой утилиты (exclude по своему PID)
          formats --exe <имя> [--seconds 5]      матрица форматов: pcm16/float32 × 48000/44100 × флаги
          test --exe <имя> --sink <устройство>   ворота этапа 0: захват → «Забрать себе» → сброс
               [--pre 5] [--during 10] [--post 5]
          test --tone --sink <устройство>        то же на встроенном генераторе тона (настройки ваших приложений не трогаются)
               [--routing auto|fixed] [--level -30]
          tone [--seconds 30] [--level -30] [--routing auto|fixed]   синус 1 кГц в устройство по умолчанию
          redirect --exe <имя> --sink <устройство>   перенаправить приложение (без автосброса)
          reset --exe <имя>                      вернуть приложению устройство по умолчанию
          status --exe <имя>                     текущая привязка приложения

        Общие параметры захвата:
          --format pcm16|float32   (по умолчанию pcm16)
          --rate 48000             частота захвата
          --no-autoconvert         без AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
          --src-quality            добавить AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY
          --out <папка>            куда писать WAV (по умолчанию .\probe-out)

        <устройство> — часть имени («CABLE Input») или полный ID эндпоинта из list.
        Ctrl+C останавливает запись; test при этом сбрасывает привязку.
        """);
    }
}
