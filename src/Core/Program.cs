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
        var runOptions = ApplyCliOptions(args, options, logger);

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

            var pluginDirectory = ResolvePluginDirectory(runOptions.PluginDirectoryOverride, logger);
            using var pluginHost = new PluginHost(pluginDirectory, loggerFactory.CreateLogger<PluginHost>());
            pluginHost.LoadPlugins();
            logger.LogInformation(
                "Plugin host initialized. directory={PluginDirectory} loaded={PluginCount}",
                pluginDirectory,
                pluginHost.LoadedPluginNames.Count);
            if (pluginHost.LoadedPluginNames.Count > 0)
            {
                logger.LogInformation("Loaded plugins: {Plugins}", string.Join(", ", pluginHost.LoadedPluginNames));
            }

            var botEngine = new BotEngine(
                objectManager,
                eventBus,
                unlockerClient,
                options,
                loggerFactory.CreateLogger<BotEngine>(),
                pluginHost);

            using var cts = new CancellationTokenSource();
            if (runOptions.SmokeMode)
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

    private static RuntimeOptions ApplyCliOptions(string[] args, BotOptions options, ILogger logger)
    {
        const string telemetryArg = "--telemetry-interval";
        const string telemetryLevelArg = "--telemetry-level";
        const string pluginDirArg = "--plugin-dir";
        const string smokeArg = "--smoke";
        var runtime = new RuntimeOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string? value = null;
            var matchedTelemetryInterval = false;
            var matchedTelemetryLevel = false;
            var matchedPluginDir = false;
            var interval = 0;

            if (argument.Equals(smokeArg, StringComparison.OrdinalIgnoreCase))
            {
                runtime.SmokeMode = true;
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

            if (matchedPluginDir)
            {
                runtime.PluginDirectoryOverride = value;
                logger.LogInformation("Plugin directory override set to: {PluginDirectory}", value);
            }
        }

        return runtime;
    }

    private static string ResolvePluginDirectory(string? overrideDirectory, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            var fullOverride = Path.GetFullPath(overrideDirectory);
            if (Directory.Exists(fullOverride))
            {
                return fullOverride;
            }

            logger.LogWarning("Plugin override directory not found: {PluginDirectory}", fullOverride);
        }

        var runtimePlugins = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (ContainsPluginManifest(runtimePlugins))
        {
            return runtimePlugins;
        }

        var repoRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var sampleDebug = Path.Combine(repoRoot, "src", "Plugins", "SampleCombatPlugin", "bin", "Debug", "net8.0");
            if (ContainsPluginManifest(sampleDebug))
            {
                return sampleDebug;
            }

            var sampleRelease = Path.Combine(repoRoot, "src", "Plugins", "SampleCombatPlugin", "bin", "Release", "net8.0");
            if (ContainsPluginManifest(sampleRelease))
            {
                return sampleRelease;
            }
        }

        Directory.CreateDirectory(runtimePlugins);
        return runtimePlugins;
    }

    private static bool ContainsPluginManifest(string directory)
    {
        return Directory.Exists(directory) &&
               Directory.EnumerateFiles(directory, "*.plugin.json", SearchOption.AllDirectories).Any();
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current != null)
        {
            var solutionPath = Path.Combine(current.FullName, "TalosForge.sln");
            if (File.Exists(solutionPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
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

    private sealed class RuntimeOptions
    {
        public bool SmokeMode { get; set; }
        public string? PluginDirectoryOverride { get; set; }
    }
}
