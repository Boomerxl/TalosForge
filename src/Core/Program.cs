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
}
