using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RackPilot;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Contains("--list"))
        {
            MidiEngine.ListDevices();
            return 0;
        }

        using IHost host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                cfg.AddEnvironmentVariables();
            })
            .ConfigureLogging(lb =>
            {
                lb.ClearProviders();
                lb.AddSimpleConsole(o =>
                {
                    o.IncludeScopes = false;
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                });
            })
            .Build();

        var config = host.Services.GetRequiredService<IConfiguration>();
        var midi = config.GetSection("Midi").Get<MidiSettings>() ?? new MidiSettings();

        try
        {
            await using var engine = new MidiEngine(midi);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; Console.WriteLine("Stopping..."); };
            Console.WriteLine("RackPilot running. Press Ctrl+C to stop.");
            await engine.StartAsync();
            await Task.Delay(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine("Hint: run with --list to see available MIDI outs.");
            return 1;
        }

        return 0;
    }
}
