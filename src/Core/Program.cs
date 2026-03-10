using Microsoft.Extensions.Logging;
using TalosForge.Core.Bot;
using TalosForge.Core.Caching;
using TalosForge.Core.Configuration;
using TalosForge.Core.Events;
using TalosForge.Core.IPC;
using TalosForge.Core.ObjectManager;
using TalosForge.Core.Plugins;

namespace TalosForge.Core;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var options = new BotOptions();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Information)
                .AddSimpleConsole(console =>
                {
                    console.SingleLine = true;
                    console.TimestampFormat = "HH:mm:ss ";
                });
        });

        var logger = loggerFactory.CreateLogger("TalosForge");
        ApplyCliOptions(args, options, logger);

        logger.LogInformation("TalosForge initializing... Custodem finge!");

        try
        {
            var reader = MemoryReader.Instance;
            if (!reader.Attach())
            {
                logger.LogWarning("WoW not found");
                return;
            }

            if (reader.BaseAddress == IntPtr.Zero)
            {
                logger.LogError("Attach failed: BaseAddress is zero");
                return;
            }

            logger.LogInformation("Attach succeeded. BaseAddress: 0x{BaseAddress:X}", reader.BaseAddress.ToInt64());

            var clientConnection = reader.ReadPointer(
                IntPtr.Add(reader.BaseAddress, Offsets.STATIC_CLIENT_CONNECTION));
            logger.LogInformation(
                "STATIC_CLIENT_CONNECTION pointer: 0x{ClientConnection:X}",
                clientConnection.ToInt64());

            var objectManager = new ObjectManagerService(reader, loggerFactory.CreateLogger<ObjectManagerService>());
            var eventBus = new EventBus();
            var cache = new MemoryCacheService(options);
            cache.Set("boot.playerGuid", 0UL, Abstractions.CachePolicy.LongLived);

            using var unlockerClient = new SharedMemoryUnlockerClient(options);
            using var mockUnlocker = new MockUnlockerEndpoint(options);

            var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
            using var pluginHost = new PluginHost(pluginDirectory, loggerFactory.CreateLogger<PluginHost>());
            pluginHost.LoadPlugins();

            var botEngine = new BotEngine(
                objectManager,
                eventBus,
                unlockerClient,
                options,
                loggerFactory.CreateLogger<BotEngine>(),
                pluginHost);

            using var cts = new CancellationTokenSource();
            if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(2));
            }

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var mockTask = Task.Run(async () =>
            {
                try
                {
                    await mockUnlocker.RunAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            });

            try
            {
                await botEngine.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Bot engine stopped.");
            }

            await mockTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Attach failed");
        }
    }

    private static void ApplyCliOptions(string[] args, BotOptions options, ILogger logger)
    {
        const string telemetryArg = "--telemetry-interval";
        const string telemetryLevelArg = "--telemetry-level";

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string? value = null;
            var matchedTelemetryInterval = false;
            var matchedTelemetryLevel = false;
            var interval = 0;

            if (argument.Equals(telemetryArg, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    logger.LogWarning("Missing value for {Argument}. Keeping default interval {Interval}.", telemetryArg, options.SnapshotTelemetryEveryTicks);
                    continue;
                }

                value = args[++i];
                matchedTelemetryInterval = true;
            }
            else if (argument.StartsWith(telemetryArg + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[(telemetryArg.Length + 1)..];
                matchedTelemetryInterval = true;
            }
            else if (argument.Equals(telemetryLevelArg, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    logger.LogWarning("Missing value for {Argument}. Keeping default level {Level}.", telemetryLevelArg, options.TelemetryLevel);
                    continue;
                }

                value = args[++i];
                matchedTelemetryLevel = true;
            }
            else if (argument.StartsWith(telemetryLevelArg + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[(telemetryLevelArg.Length + 1)..];
                matchedTelemetryLevel = true;
            }

            if (value == null)
            {
                continue;
            }

            if (matchedTelemetryInterval && !int.TryParse(value, out interval))
            {
                logger.LogWarning(
                    "Invalid telemetry interval '{Value}'. Keeping default interval {Interval}.",
                    value,
                    options.SnapshotTelemetryEveryTicks);
                continue;
            }

            if (matchedTelemetryInterval && interval <= 0)
            {
                options.EnableSnapshotTelemetry = false;
                logger.LogInformation("Snapshot telemetry disabled via {Argument} {Value}.", telemetryArg, interval);
                continue;
            }

            if (matchedTelemetryInterval)
            {
                options.SnapshotTelemetryEveryTicks = interval;
                options.EnableSnapshotTelemetry = true;
                logger.LogInformation("Snapshot telemetry interval set to every {Interval} ticks.", interval);
            }

            if (matchedTelemetryLevel)
            {
                if (!TryParseTelemetryLevel(value, out var level))
                {
                    logger.LogWarning(
                        "Invalid telemetry level '{Value}'. Use one of: minimal, normal, debug. Keeping {Level}.",
                        value,
                        options.TelemetryLevel);
                    continue;
                }

                options.TelemetryLevel = level;
                logger.LogInformation("Telemetry level set to {Level}.", options.TelemetryLevel);
            }
        }
    }

    private static bool TryParseTelemetryLevel(string value, out TelemetryLevel level)
    {
        if (value.Equals("minimal", StringComparison.OrdinalIgnoreCase))
        {
            level = TelemetryLevel.Minimal;
            return true;
        }

        if (value.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            level = TelemetryLevel.Normal;
            return true;
        }

        if (value.Equals("debug", StringComparison.OrdinalIgnoreCase))
        {
            level = TelemetryLevel.Debug;
            return true;
        }

        level = TelemetryLevel.Normal;
        return false;
    }
}
