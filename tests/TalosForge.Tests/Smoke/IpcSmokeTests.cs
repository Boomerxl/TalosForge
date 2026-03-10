using TalosForge.Core.Configuration;
using TalosForge.Core.IPC;
using TalosForge.Core.Models;
using Xunit;

namespace TalosForge.Tests.Smoke;

public sealed class IpcSmokeTests
{
    [Fact]
    public async Task SharedMemory_Client_And_MockEndpoint_Exchange_Ack()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var options = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.Smoke.{suffix}",
            EventMmfName = $"TalosForge.Evt.Smoke.{suffix}",
            RingCapacityBytes = 4096,
            UnlockerTimeoutMs = 400,
            UnlockerRetryCount = 1,
        };

        using var endpoint = new MockUnlockerEndpoint(options);
        using var client = new SharedMemoryUnlockerClient(options);

        using var cts = new CancellationTokenSource();
        var loopTask = Task.Run(async () => await endpoint.RunAsync(cts.Token));

        var command = new UnlockerCommand(
            99,
            UnlockerOpcode.Face,
            "{\"facing\":1.2}",
            DateTimeOffset.UtcNow);

        var ack = await client.SendAsync(command, CancellationToken.None);

        Assert.True(ack.Success);
        Assert.Equal(99, ack.CommandId);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loopTask);
    }
}
