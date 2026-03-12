using System.Text.Json;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Configuration;
using TalosForge.Core.Drawing;
using TalosForge.Core.Models;
using Xunit;

namespace TalosForge.Tests.Drawing;

public sealed class InGameOverlayServiceTests
{
    [Fact]
    public async Task DisabledOverlay_DoesNotSendCommand()
    {
        var unlocker = new CollectingUnlockerClient();
        var options = new BotOptions { EnableInGameOverlay = false, InGameOverlayEveryTicks = 5 };
        var service = new InGameOverlayService(unlocker, options);

        var count = await service.TryPublishAsync(5, BotState.Idle, BuildSnapshot(5), 0, CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(unlocker.Commands);
    }

    [Fact]
    public async Task EnabledOverlay_SendsImmediately_When_First_Becoming_Visible_Then_Respects_Interval()
    {
        var unlocker = new CollectingUnlockerClient();
        var options = new BotOptions { EnableInGameOverlay = true, InGameOverlayEveryTicks = 3 };
        var service = new InGameOverlayService(unlocker, options);

        var c1 = await service.TryPublishAsync(2, BotState.Idle, BuildSnapshot(2), 0, CancellationToken.None);
        var c2 = await service.TryPublishAsync(3, BotState.Combat, BuildSnapshot(3), 1, CancellationToken.None);

        Assert.Equal(1, c1);
        Assert.Equal(0, c2);
        Assert.Single(unlocker.Commands);

        var payload = JsonSerializer.Deserialize<JsonElement>(unlocker.Commands[0].PayloadJson);
        var lua = payload.GetProperty("code").GetString();
        Assert.NotNull(lua);
        Assert.Contains("frame.TalosForgeText", lua, StringComparison.Ordinal);
        Assert.Contains("Tick:2", lua, StringComparison.Ordinal);
    }

    private static WorldSnapshot BuildSnapshot(long tickId)
    {
        return new WorldSnapshot(
            tickId,
            DateTimeOffset.UtcNow,
            Array.Empty<WowObjectSnapshot>(),
            new PlayerSnapshot(1, new Vector3(0, 0, 0), 0, 99, false, false, false, false),
            true,
            null);
    }

    private sealed class CollectingUnlockerClient : IUnlockerClient
    {
        public List<UnlockerCommand> Commands { get; } = new();

        public Task<UnlockerAck> SendAsync(UnlockerCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new UnlockerAck(command.CommandId, true, "ok", command.PayloadJson, DateTimeOffset.UtcNow));
        }

        public void Dispose()
        {
        }
    }
}
