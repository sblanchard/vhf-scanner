using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using VhfScanner.Core.Asr;
using VhfScanner.Core.Audio;
using VhfScanner.Core.Notifications;
using VhfScanner.Core.Radio;
using VhfScanner.Core.Scanner;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/vhf-scanner-.log", 
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("╔════════════════════════════════════════════════════════════════╗");
    Log.Information("║          VHF Scanner for IC-705 with Parakeet ASR              ║");
    Log.Information("╚════════════════════════════════════════════════════════════════╝");

    var host = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureAppConfiguration((context, config) =>
        {
            config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            config.AddEnvironmentVariables("VHFSCANNER_");
        })
        .ConfigureServices((context, services) =>
        {
            var config = context.Configuration;

            // IC-705 Controller
            services.AddSingleton(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<Ic705Controller>>();
                var portName = config["Radio:PortName"] ?? (OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyUSB0");
                var baudRate = config.GetValue("Radio:BaudRate", 19200);
                Log.Information("Radio: {Port} @ {BaudRate} baud", portName, baudRate);
                return new Ic705Controller(logger, portName, baudRate);
            });

            // Audio capture from IC-705 USB (platform-specific)
            services.AddSingleton<IAudioCapture>(sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var deviceIndex = config.GetValue("Audio:DeviceIndex", -1);

                if (deviceIndex < 0)
                {
                    // Try to find IC-705, otherwise use first available input device
                    var ic705Index = AudioDevices.FindIc705Device();

                    if (ic705Index.HasValue)
                    {
                        deviceIndex = ic705Index.Value;
                        Log.Information("Auto-detected IC-705 audio device: {Index}", deviceIndex);
                    }
                    else
                    {
                        var firstDevice = AudioDevices.GetDevices().FirstOrDefault();
                        if (firstDevice != null)
                        {
                            deviceIndex = firstDevice.Index;
                            Log.Information("Using first available audio device: [{Index}] {Name}",
                                deviceIndex, firstDevice.Name);
                        }
                        else
                        {
                            throw new InvalidOperationException("No audio input devices available");
                        }
                    }
                }

                var sampleRate = config.GetValue("Audio:SampleRate", 48000);
                return AudioDevices.CreateCapture(loggerFactory, deviceIndex, sampleRate);
            });

            // Transmission recorder
            services.AddSingleton(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<TransmissionRecorder>>();
                var sampleRate = config.GetValue("Audio:SampleRate", 48000);
                return new TransmissionRecorder(logger, sampleRate);
            });

            // Parakeet ASR
            services.AddSingleton<IAsrService>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ParakeetAsrService>>();
                var settings = new ParakeetSettings
                {
                    ModelName = config["Asr:Model"] ?? "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
                    ModelsDirectory = config["Asr:ModelsDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "models"),
                    UseGpu = config.GetValue("Asr:UseGpu", false),
                    Threads = config.GetValue("Asr:Threads", 0)
                };
                Log.Information("ASR: {Model} (GPU: {Gpu})", settings.ModelName, settings.UseGpu);
                return new ParakeetAsrService(logger, settings);
            });

            // Notifications
            services.AddSingleton<INotificationService>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<CompositeNotifier>>();
                var notifiers = new List<INotificationService>();

                // Telegram
                var telegramToken = config["Notifications:Telegram:BotToken"];
                var telegramChat = config["Notifications:Telegram:ChatId"];
                if (!string.IsNullOrEmpty(telegramToken) && !string.IsNullOrEmpty(telegramChat))
                {
                    var tgLogger = sp.GetRequiredService<ILogger<TelegramNotifier>>();
                    notifiers.Add(new TelegramNotifier(tgLogger, telegramToken, telegramChat));
                    Log.Information("✓ Telegram notifications enabled");
                }

                // Discord
                var discordUrl = config["Notifications:Discord:WebhookUrl"];
                if (!string.IsNullOrEmpty(discordUrl))
                {
                    var dcLogger = sp.GetRequiredService<ILogger<WebhookNotifier>>();
                    notifiers.Add(new WebhookNotifier(dcLogger, discordUrl, WebhookFormat.Discord));
                    Log.Information("✓ Discord notifications enabled");
                }

                if (notifiers.Count == 0)
                {
                    Log.Warning("No notification services configured - using console output");
                    notifiers.Add(new ConsoleNotifier());
                }

                return new CompositeNotifier(logger, notifiers);
            });

            // Scanner options
            services.AddSingleton(new VhfScannerOptions
            {
                PollIntervalMs = config.GetValue("Scanner:PollIntervalMs", 50),
                MinCallsignConfidence = config.GetValue("Scanner:MinCallsignConfidence", 0.5)
            });

            // Scanner service
            services.AddHostedService<VhfScannerService>();
        })
        .Build();

    // List audio devices
    Log.Information("Available audio devices:");
    foreach (var device in AudioDevices.GetDevices())
    {
        Log.Information("  [{Index}] {Name} {Default}", device.Index, device.Name, device.IsDefault ? "(default)" : "");
    }

    Log.Information("");
    Log.Information("Instructions:");
    Log.Information("  1. Connect IC-705 via USB");
    Log.Information("  2. Program memory channels with VHF frequencies");
    Log.Information("  3. Start Memory Scan on IC-705 (SCAN > MEMO)");
    Log.Information("  4. The scanner will detect when squelch opens and transcribe audio");
    Log.Information("");

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// Console notifier for testing when no external services configured
/// </summary>
internal sealed class ConsoleNotifier : INotificationService
{
    public string ServiceName => "Console";

    public Task SendActivityAsync(DetectedActivity activity, CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║  📻 CALLSIGN: {activity.Callsign,-20}                       ║");
        Console.WriteLine($"║  📡 Frequency: {activity.FrequencyMHz:F4} MHz                             ║");
        Console.WriteLine($"║  🕐 Time: {activity.Timestamp:HH:mm:ss} UTC                                  ║");
        Console.WriteLine($"║  📊 Confidence: {activity.Confidence:P0}                                     ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
        
        if (!string.IsNullOrWhiteSpace(activity.TranscribedText))
            Console.WriteLine($"  💬 \"{activity.TranscribedText}\"");
        
        Console.WriteLine();
        return Task.CompletedTask;
    }
}
