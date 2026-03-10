using Microsoft.Extensions.Logging;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Bot;
using TalosForge.Core.Caching;
using TalosForge.Core.Configuration;
using TalosForge.Core.Diagnostics;
using TalosForge.Core.Drawing;
using TalosForge.Core.Events;
using TalosForge.Core.IPC;
using TalosForge.Core.Models;
using TalosForge.Core.ObjectManager;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.Runtime;

public sealed class BotRuntimeHost
{
    private readonly BotOptions _options;
    private readonly RuntimeOptions _runtimeOptions;
    private readonly Action<string>? _logSink;
    private readonly Action<BotTickMetrics, WorldSnapshot>? _tickSink;

    public BotRuntimeHost(
        BotOptions options,
        RuntimeOptions runtimeOptions,
        Action<string>? logSink = null,
        Action<BotTickMetrics, WorldSnapshot>? tickSink = null)
    {
        _options = options;
        _runtimeOptions = runtimeOptions;
        _logSink = logSink;
        _tickSink = tickSink;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Information)
                .AddSimpleConsole(console =>
                {
                    console.SingleLine = true;
                    console.TimestampFormat = "HH:mm:ss ";
                });

            if (_logSink != null)
            {
                builder.AddProvider(new CallbackLoggerProvider(_logSink));
            }
        });

        var logger = loggerFactory.CreateLogger("TalosForge");
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
            var cache = new MemoryCacheService(_options);
            cache.Set("boot.playerGuid", 0UL, CachePolicy.LongLived);

            using var unlockerClient = new SharedMemoryUnlockerClient(_options);
            using var mockUnlocker = new MockUnlockerEndpoint(_options);

            var pluginDirectory = ResolvePluginDirectory(_runtimeOptions.PluginDirectoryOverride, logger);
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
                _options,
                loggerFactory.CreateLogger<BotEngine>(),
                pluginHost,
                new InGameOverlayService(unlockerClient, _options));

            if (_tickSink != null)
            {
                botEngine.TickCompleted += _tickSink;
            }

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_runtimeOptions.SmokeMode)
            {
                runCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _runtimeOptions.SmokeDurationSeconds)));
            }

            var token = runCts.Token;

            var mockTask = Task.Run(async () =>
            {
                try
                {
                    await mockUnlocker.RunAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }, token);

            try
            {
                await botEngine.RunAsync(token).ConfigureAwait(false);
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
}
