using TalosForge.Core.Configuration;
using TalosForge.Core.IPC;
using TalosForge.Core.Models;
using Xunit;

namespace TalosForge.Tests.IPC;

public sealed class SharedMemoryUnlockerClientTests
{
    [Fact]
    public async Task SendAsync_Receives_Correlated_Ack()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var options = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.Test.{suffix}",
            EventMmfName = $"TalosForge.Evt.Test.{suffix}",
            RingCapacityBytes = 4096,
            UnlockerTimeoutMs = 500,
            UnlockerRetryCount = 1,
        };

        using var mock = new MockUnlockerEndpoint(options);
        using var client = new SharedMemoryUnlockerClient(options);
        using var cts = new CancellationTokenSource();

        var pumpTask = Task.Run(async () => await mock.RunAsync(cts.Token));

        var command = new UnlockerCommand(
            CommandId: 42,
            Opcode: UnlockerOpcode.LuaDoString,
            PayloadJson: "{\"code\":\"print('hello')\"}",
            TimestampUtc: DateTimeOffset.UtcNow);

        var ack = await client.SendAsync(command, CancellationToken.None);

        Assert.True(ack.Success);
        Assert.Equal(42, ack.CommandId);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pumpTask);
    }

    [Fact]
    public async Task SendAsync_When_NoAck_Throws_TimeoutException()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var options = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.Test.Timeout.{suffix}",
            EventMmfName = $"TalosForge.Evt.Test.Timeout.{suffix}",
            RingCapacityBytes = 4096,
            UnlockerTimeoutMs = 30,
            UnlockerRetryCount = 0,
        };

        using var client = new SharedMemoryUnlockerClient(options);

        var command = new UnlockerCommand(
            CommandId: 43,
            Opcode: UnlockerOpcode.LuaDoString,
            PayloadJson: "{\"code\":\"print('timeout')\"}",
            TimestampUtc: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await client.SendAsync(command, CancellationToken.None));
    }
}
