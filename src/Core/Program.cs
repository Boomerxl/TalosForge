using Microsoft.Extensions.Logging;
using TalosForge.Core.Configuration;
using TalosForge.Core.Runtime;

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
        var runOptions = ApplyCliOptions(args, options, logger);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var runtimeHost = new BotRuntimeHost(options, runOptions);
        await runtimeHost.RunAsync(cts.Token).ConfigureAwait(false);
    }

    private static RuntimeOptions ApplyCliOptions(string[] args, BotOptions options, ILogger logger)
    {
        const string telemetryArg = "--telemetry-interval";
        const string telemetryLevelArg = "--telemetry-level";
        const string pluginDirArg = "--plugin-dir";
        const string smokeArg = "--smoke";
        const string inGameUiArg = "--ingame-ui";
        const string inGameUiIntervalArg = "--ingame-ui-interval";
        var runtime = new RuntimeOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string? value = null;
            var matchedTelemetryInterval = false;
            var matchedTelemetryLevel = false;
            var matchedPluginDir = false;
            var matchedInGameUiInterval = false;
            var interval = 0;

            if (argument.Equals(smokeArg, StringComparison.OrdinalIgnoreCase))
            {
                runtime.SmokeMode = true;
                continue;
            }
            else if (argument.Equals(inGameUiArg, StringComparison.OrdinalIgnoreCase))
            {
                options.EnableInGameOverlay = true;
                logger.LogInformation("In-game overlay enabled.");
                continue;
            }

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
            else if (argument.Equals(pluginDirArg, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    logger.LogWarning("Missing value for {Argument}. Ignoring plugin override.", pluginDirArg);
                    continue;
                }

                value = args[++i];
                matchedPluginDir = true;
            }
            else if (argument.StartsWith(pluginDirArg + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[(pluginDirArg.Length + 1)..];
                matchedPluginDir = true;
            }
            else if (argument.Equals(inGameUiIntervalArg, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    logger.LogWarning("Missing value for {Argument}. Keeping default interval {Interval}.", inGameUiIntervalArg, options.InGameOverlayEveryTicks);
                    continue;
                }

                value = args[++i];
                matchedInGameUiInterval = true;
            }
            else if (argument.StartsWith(inGameUiIntervalArg + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[(inGameUiIntervalArg.Length + 1)..];
                matchedInGameUiInterval = true;
            }

            if (value == null)
            {
                continue;
            }

            if ((matchedTelemetryInterval || matchedInGameUiInterval) && !int.TryParse(value, out interval))
            {
                logger.LogWarning("Invalid interval '{Value}'.", value);
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

            if (matchedPluginDir)
            {
                runtime.PluginDirectoryOverride = value;
                logger.LogInformation("Plugin directory override set to: {PluginDirectory}", value);
            }

            if (matchedInGameUiInterval)
            {
                options.EnableInGameOverlay = interval > 0;
                options.InGameOverlayEveryTicks = Math.Max(1, interval);
                logger.LogInformation(
                    "In-game overlay interval set to every {Interval} ticks (enabled={Enabled}).",
                    options.InGameOverlayEveryTicks,
                    options.EnableInGameOverlay);
            }
        }

        return runtime;
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
