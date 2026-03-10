using Microsoft.Extensions.Logging.Abstractions;
using TalosForge.Core.IPC;
using TalosForge.Core.Models;
using TalosForge.Core.Plugins;
using Xunit;

namespace TalosForge.Tests.Smoke;

public sealed class PluginSmokeTests
{
    [Fact]
    public async Task PluginHost_Ticks_Without_Runtime_Failures()
    {
        var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        Directory.CreateDirectory(pluginDir);

        using var host = new PluginHost(pluginDir, NullLogger<PluginHost>.Instance);
        host.LoadPlugins();

        var snapshot = new WorldSnapshot(
            TickId: 10,
            TimestampUtc: DateTimeOffset.UtcNow,
            Objects: Array.Empty<WowObjectSnapshot>(),
            Player: new PlayerSnapshot(1, new Vector3(0, 0, 0), 0, null, false, false, false, false),
            Success: true,
            ErrorMessage: null);

        var sent = await host.TickAsync(snapshot, Array.Empty<BotEvent>(), new NullUnlockerClient(), CancellationToken.None);
        Assert.True(sent >= 0);
    }
}
