using System.Globalization;
using System.Text;
using Mixer.Audio.Interop;
using Mixer.Run;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");

var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

var cli = new Args(args);
try
{
    return cli.Command switch
    {
        "devices" => Commands.Devices(cli),
        "run" => Commands.Run(cli, cancel.Token),
        "latency" => Latency.Run(cli, cancel.Token),
        "example" => Commands.Example(cli),
        "apps" => Commands.Apps(cli),
        "sink-check" => Commands.SinkCheck(cli),
        _ => Commands.Help(),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (AudioException ex)
{
    Console.Error.WriteLine($"Ошибка аудио: {ex.Message}");
    return 3;
}
