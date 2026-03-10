using Microsoft.Extensions.Logging.Abstractions;
using TalosForge.Core.Configuration;
using TalosForge.Core.IPC;
using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Configuration;
using TalosForge.UnlockerHost.Execution;
using TalosForge.UnlockerHost.Host;
using Xunit;

namespace TalosForge.Tests.IPC;

public sealed class UnlockerHostIntegrationTests
{
    [Fact]
    public async Task UnlockerHost_MockExecutor_Processes_Command_And_Acks()
    {
        var suffix = Guid.NewGuid().ToString("N");

        var botOptions = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.HostInt.{suffix}",
            EventMmfName = $"TalosForge.Evt.HostInt.{suffix}",
            RingCapacityBytes = 8192,
            UnlockerTimeoutMs = 500,
            UnlockerRetryCount = 1,
        };

        var hostOptions = new UnlockerHostOptions
        {
            CommandRingName = botOptions.CommandMmfName,
            EventRingName = botOptions.EventMmfName,
            RingCapacityBytes = botOptions.RingCapacityBytes,
            PollDelayMs = 1,
            AckWriteRetryCount = 10,
            AckWriteDelayMs = 1,
            StatsIntervalSeconds = 60,
            ExecutorMode = "mock",
        };

        using var host = new UnlockerHostService(
            hostOptions,
            new MockCommandExecutor(),
            NullLogger<UnlockerHostService>.Instance);
        using var client = new SharedMemoryUnlockerClient(botOptions);
        using var cts = new CancellationTokenSource();

        var hostTask = host.RunAsync(cts.Token);

        var command = new UnlockerCommand(
            CommandId: 9001,
            Opcode: UnlockerOpcode.LuaDoString,
            PayloadJson: "{\"code\":\"print('hello')\"}",
            TimestampUtc: DateTimeOffset.UtcNow);

        var ack = await client.SendAsync(command, CancellationToken.None);

        Assert.True(ack.Success);
        Assert.Equal(command.CommandId, ack.CommandId);
        Assert.Equal("ACK:LuaDoString", ack.Message);
        Assert.Equal(command.PayloadJson, ack.PayloadJson);

        cts.Cancel();
        await hostTask;
    }
}
