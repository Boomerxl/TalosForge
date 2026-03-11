using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Text.Json;
using TalosForge.AdapterBridge.Execution;
using TalosForge.AdapterBridge.Runtime;
using TalosForge.Core.Configuration;
using TalosForge.Core.IPC;
using TalosForge.Core.Models;
using TalosForge.UnlockerAgentHost.Execution;
using TalosForge.UnlockerAgentHost.Runtime;
using TalosForge.UnlockerHost.Configuration;
using TalosForge.UnlockerHost.Execution;
using TalosForge.UnlockerHost.Host;
using TalosForge.UnlockerHost.Models;
using Xunit;

namespace TalosForge.Tests.IPC;

public sealed class UnlockerHostIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        Assert.True(ack.Success, $"{ack.Message} | {ack.PayloadJson}");
        Assert.Equal(command.CommandId, ack.CommandId);
        Assert.Equal("ACK:LuaDoString", ack.Message);
        Assert.Equal(command.PayloadJson, ack.PayloadJson);

        cts.Cancel();
        await hostTask;
    }

    [Fact]
    public async Task UnlockerHost_Writes_Status_File()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var statusFile = Path.Combine(Path.GetTempPath(), $"TalosForge.UnlockerHost.{suffix}.status.json");

        var hostOptions = new UnlockerHostOptions
        {
            CommandRingName = $"TalosForge.Cmd.Status.{suffix}",
            EventRingName = $"TalosForge.Evt.Status.{suffix}",
            RingCapacityBytes = 4096,
            PollDelayMs = 1,
            StatusWriteIntervalMs = 100,
            StatusFilePath = statusFile,
            ExecutorMode = "mock",
        };

        using var host = new UnlockerHostService(
            hostOptions,
            new MockCommandExecutor(),
            NullLogger<UnlockerHostService>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await host.RunAsync(cts.Token);

        Assert.True(File.Exists(statusFile));
        var json = await File.ReadAllTextAsync(statusFile);
        var status = System.Text.Json.JsonSerializer.Deserialize<UnlockerHostStatusFile>(json);
        Assert.NotNull(status);
        Assert.False(status!.Running);

        File.Delete(statusFile);
    }

    [Fact]
    public async Task UnlockerHost_MockExecutor_Acks_Stop_Opcode()
    {
        var suffix = Guid.NewGuid().ToString("N");

        var botOptions = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.Stop.{suffix}",
            EventMmfName = $"TalosForge.Evt.Stop.{suffix}",
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
            CommandId: 9100,
            Opcode: UnlockerOpcode.Stop,
            PayloadJson: "{}",
            TimestampUtc: DateTimeOffset.UtcNow);

        var ack = await client.SendAsync(command, CancellationToken.None);

        Assert.True(ack.Success);
        Assert.Equal(command.CommandId, ack.CommandId);
        Assert.Equal("ACK:Stop", ack.Message);
        Assert.Equal(command.PayloadJson, ack.PayloadJson);

        cts.Cancel();
        await hostTask;
    }

    [Fact]
    public async Task UnlockerHost_AdapterExecutor_Returns_BackendUnavailable_Code()
    {
        var suffix = Guid.NewGuid().ToString("N");

        var botOptions = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.Adapter.{suffix}",
            EventMmfName = $"TalosForge.Evt.Adapter.{suffix}",
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
            ExecutorMode = "adapter",
        };

        using var host = new UnlockerHostService(
            hostOptions,
            new AdapterCommandExecutor(new UnavailableAdapterBackend()),
            NullLogger<UnlockerHostService>.Instance);
        using var client = new SharedMemoryUnlockerClient(botOptions);
        using var cts = new CancellationTokenSource();

        var hostTask = host.RunAsync(cts.Token);

        var command = new UnlockerCommand(
            CommandId: 9200,
            Opcode: UnlockerOpcode.LuaDoString,
            PayloadJson: "{\"code\":\"print('hi')\"}",
            TimestampUtc: DateTimeOffset.UtcNow);

        var ack = await client.SendAsync(command, CancellationToken.None);

        Assert.False(ack.Success);
        Assert.StartsWith(AdapterResultCodes.BackendUnavailable, ack.Message, StringComparison.Ordinal);
        Assert.NotNull(ack.PayloadJson);
        Assert.Contains($"\"code\":\"{AdapterResultCodes.BackendUnavailable}\"", ack.PayloadJson!, StringComparison.Ordinal);

        cts.Cancel();
        await hostTask;
    }

    [Fact]
    public async Task UnlockerHost_AdapterExecutor_NamedPipe_Backend_Acks_Success()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $"TalosForge.Adapter.Int.{suffix}";

        var botOptions = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.AdapterPipe.{suffix}",
            EventMmfName = $"TalosForge.Evt.AdapterPipe.{suffix}",
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
            ExecutorMode = "adapter",
            AdapterBackendMode = "pipe",
            AdapterPipeName = pipeName,
            AdapterConnectTimeoutMs = 500,
            AdapterRequestTimeoutMs = 1_000,
        };
        AdapterPipeRequest? capturedRequest = null;

        var backendServerTask = Task.Run(async () =>
        {
            await using var pipeServer = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await pipeServer.WaitForConnectionAsync();

            using var reader = new StreamReader(pipeServer);
            using var writer = new StreamWriter(pipeServer) { AutoFlush = true };

            var line = await reader.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                capturedRequest = JsonSerializer.Deserialize<AdapterPipeRequest>(line!, JsonOptions);
            }

            var response = new AdapterPipeResponse(
                Success: true,
                Message: "ACK:AdapterPipe",
                PayloadJson: "{\"bridge\":true}",
                Code: AdapterResultCodes.Ok);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        });

        using var host = new UnlockerHostService(
            hostOptions,
            new AdapterCommandExecutor(new NamedPipeAdapterBackend(hostOptions)),
            NullLogger<UnlockerHostService>.Instance);
        using var client = new SharedMemoryUnlockerClient(botOptions);
        using var cts = new CancellationTokenSource();

        var hostTask = host.RunAsync(cts.Token);

        var command = new UnlockerCommand(
            CommandId: 9300,
            Opcode: UnlockerOpcode.LuaDoString,
            PayloadJson: "{\"code\":\"print('pipe')\"}",
            TimestampUtc: DateTimeOffset.UtcNow);

        var ack = await client.SendAsync(command, CancellationToken.None);

        await backendServerTask;

        Assert.True(ack.Success);
        Assert.Equal("ACK:AdapterPipe", ack.Message);
        Assert.Equal("{\"bridge\":true}", ack.PayloadJson);
        Assert.NotNull(capturedRequest);
        Assert.Equal("LuaDoString", capturedRequest!.Opcode);

        cts.Cancel();
        await hostTask;
    }

    [Fact]
    public async Task UnlockerHost_AdapterExecutor_Works_With_AdapterBridge_Service()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $"TalosForge.Adapter.Bridge.{suffix}";

        var bridgeOptions = new BridgeOptions
        {
            PipeName = pipeName,
            Mode = "mock",
        };
        var bridgeService = new PipeBridgeService(
            bridgeOptions,
            new MockBridgeCommandExecutor(),
            NullLogger<PipeBridgeService>.Instance);
        using var bridgeCts = new CancellationTokenSource();
        var bridgeTask = bridgeService.RunAsync(bridgeCts.Token);

        var botOptions = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.AdapterBridge.{suffix}",
            EventMmfName = $"TalosForge.Evt.AdapterBridge.{suffix}",
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
            ExecutorMode = "adapter",
            AdapterBackendMode = "pipe",
            AdapterPipeName = pipeName,
            AdapterConnectTimeoutMs = 500,
            AdapterRequestTimeoutMs = 1_000,
        };

        using var host = new UnlockerHostService(
            hostOptions,
            new AdapterCommandExecutor(new NamedPipeAdapterBackend(hostOptions)),
            NullLogger<UnlockerHostService>.Instance);
        using var client = new SharedMemoryUnlockerClient(botOptions);
        using var hostCts = new CancellationTokenSource();

        var hostTask = host.RunAsync(hostCts.Token);

        var command = new UnlockerCommand(
            CommandId: 9400,
            Opcode: UnlockerOpcode.LuaDoString,
            PayloadJson: "{\"code\":\"print('bridge-service')\"}",
            TimestampUtc: DateTimeOffset.UtcNow);

        var ack = await client.SendAsync(command, CancellationToken.None);

        Assert.True(ack.Success);
        Assert.Equal("ACK:LuaDoString", ack.Message);
        using var payload = JsonDocument.Parse(ack.PayloadJson!);
        Assert.Equal("print('bridge-service')", payload.RootElement.GetProperty("code").GetString());

        hostCts.Cancel();
        await hostTask;

        bridgeCts.Cancel();
        await bridgeTask;
    }

    [Fact]
    public async Task UnlockerHost_AdapterExecutor_WowCli_Maps_All_Opcodes_EndToEnd()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $"TalosForge.Adapter.WowCli.{suffix}";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"TalosForge.WowCli.Mapping.{suffix}.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "param([Parameter(ValueFromRemainingArguments=$true)][string[]]$args)\nWrite-Output (\"ACK \" + ($args -join \"|\"))\nexit 0\n");

        var bridgeOptions = new BridgeOptions
        {
            PipeName = pipeName,
            Mode = "wow-cli",
            CommandPath = "powershell.exe",
            CommandArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            CommandTimeoutMs = 2_000,
        };
        var bridgeService = new PipeBridgeService(
            bridgeOptions,
            new WowCliBridgeCommandExecutor(bridgeOptions),
            NullLogger<PipeBridgeService>.Instance);
        using var bridgeCts = new CancellationTokenSource();
        var bridgeTask = bridgeService.RunAsync(bridgeCts.Token);

        var botOptions = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.WowCli.{suffix}",
            EventMmfName = $"TalosForge.Evt.WowCli.{suffix}",
            RingCapacityBytes = 8192,
            UnlockerTimeoutMs = 1_500,
            UnlockerRetryCount = 0,
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
            ExecutorMode = "adapter",
            AdapterBackendMode = "pipe",
            AdapterPipeName = pipeName,
            AdapterConnectTimeoutMs = 1_000,
            AdapterRequestTimeoutMs = 2_000,
        };

        using var host = new UnlockerHostService(
            hostOptions,
            new AdapterCommandExecutor(new NamedPipeAdapterBackend(hostOptions)),
            NullLogger<UnlockerHostService>.Instance);
        using var client = new SharedMemoryUnlockerClient(botOptions);
        using var hostCts = new CancellationTokenSource();
        var hostTask = host.RunAsync(hostCts.Token);

        try
        {
            var cases = new[]
            {
                (new UnlockerCommand(10_001, UnlockerOpcode.LuaDoString, "{\"code\":\"print('x')\"}", DateTimeOffset.UtcNow), "ACK lua|print('x')"),
                (new UnlockerCommand(10_002, UnlockerOpcode.CastSpellByName, "{\"spell\":\"Frostbolt\"}", DateTimeOffset.UtcNow), "ACK cast|Frostbolt"),
                (new UnlockerCommand(10_003, UnlockerOpcode.SetTargetGuid, "{\"guid\":\"0x10\"}", DateTimeOffset.UtcNow), "ACK target|16"),
                (new UnlockerCommand(10_004, UnlockerOpcode.Face, "{\"facing\":1.5,\"smoothing\":0.25}", DateTimeOffset.UtcNow), "ACK face|1.5|0.25"),
                (new UnlockerCommand(10_005, UnlockerOpcode.MoveTo, "{\"x\":1.25,\"y\":2.5,\"z\":3.75,\"overshootThreshold\":0.35}", DateTimeOffset.UtcNow), "ACK moveto|1.25|2.5|3.75|0.35"),
                (new UnlockerCommand(10_006, UnlockerOpcode.Interact, "{\"guid\":42}", DateTimeOffset.UtcNow), "ACK interact|42"),
                (new UnlockerCommand(10_007, UnlockerOpcode.Stop, "{}", DateTimeOffset.UtcNow), "ACK stop"),
            };

            foreach (var (command, expectedMessage) in cases)
            {
                var ack = await client.SendAsync(command, CancellationToken.None);
                Assert.True(ack.Success, $"{ack.Message} | {ack.PayloadJson}");
                Assert.Equal(expectedMessage, ack.Message);
            }
        }
        finally
        {
            hostCts.Cancel();
            await hostTask;

            bridgeCts.Cancel();
            await bridgeTask;
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task UnlockerHost_AdapterExecutor_WowCli_Failure_Propagates_Structured_Error()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pipeName = $"TalosForge.Adapter.WowCli.Fail.{suffix}";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"TalosForge.WowCli.Fail.{suffix}.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "param([Parameter(ValueFromRemainingArguments=$true)][string[]]$args)\nif ($args.Length -gt 0 -and $args[0] -eq 'cast') { [Console]::Error.WriteLine('simulated cast failure'); exit 23 }\nWrite-Output (\"ACK \" + ($args -join \"|\"))\nexit 0\n");

        var bridgeOptions = new BridgeOptions
        {
            PipeName = pipeName,
            Mode = "wow-cli",
            CommandPath = "powershell.exe",
            CommandArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            CommandTimeoutMs = 2_000,
        };
        var bridgeService = new PipeBridgeService(
            bridgeOptions,
            new WowCliBridgeCommandExecutor(bridgeOptions),
            NullLogger<PipeBridgeService>.Instance);
        using var bridgeCts = new CancellationTokenSource();
        var bridgeTask = bridgeService.RunAsync(bridgeCts.Token);

        var botOptions = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.WowCli.Fail.{suffix}",
            EventMmfName = $"TalosForge.Evt.WowCli.Fail.{suffix}",
            RingCapacityBytes = 8192,
            UnlockerTimeoutMs = 1_500,
            UnlockerRetryCount = 0,
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
            ExecutorMode = "adapter",
            AdapterBackendMode = "pipe",
            AdapterPipeName = pipeName,
            AdapterConnectTimeoutMs = 1_000,
            AdapterRequestTimeoutMs = 2_000,
        };

        using var host = new UnlockerHostService(
            hostOptions,
            new AdapterCommandExecutor(new NamedPipeAdapterBackend(hostOptions)),
            NullLogger<UnlockerHostService>.Instance);
        using var client = new SharedMemoryUnlockerClient(botOptions);
        using var hostCts = new CancellationTokenSource();
        var hostTask = host.RunAsync(hostCts.Token);

        try
        {
            var ack = await client.SendAsync(
                new UnlockerCommand(
                    CommandId: 11_001,
                    Opcode: UnlockerOpcode.CastSpellByName,
                    PayloadJson: "{\"spell\":\"Frostbolt\"}",
                    TimestampUtc: DateTimeOffset.UtcNow),
                CancellationToken.None);

            Assert.False(ack.Success);
            Assert.StartsWith("BRIDGE_WOWCLI_EXIT_CODE", ack.Message, StringComparison.Ordinal);
            Assert.NotNull(ack.PayloadJson);

            using var payload = JsonDocument.Parse(ack.PayloadJson!);
            Assert.Equal("BRIDGE_WOWCLI_EXIT_CODE", payload.RootElement.GetProperty("code").GetString());
            Assert.Equal(23, payload.RootElement.GetProperty("diagnostics").GetProperty("exitCode").GetInt32());
        }
        finally
        {
            hostCts.Cancel();
            await hostTask;

            bridgeCts.Cancel();
            await bridgeTask;
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task UnlockerHost_AdapterExecutor_WowAgent_EndToEnd_Acks_Success()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var bridgePipeName = $"TalosForge.Adapter.WowAgent.{suffix}";
        var agentPipeName = $"TalosForge.Agent.{suffix}";

        var agentOptions = new AgentHostOptions
        {
            PipeName = agentPipeName,
            WowProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName,
            RequestTimeoutMs = 1_000,
            RetryCount = 1,
            BackoffBaseMs = 10,
            BackoffMaxMs = 50,
            EvasionProfile = "off",
        };
        await using var agentRuntime = new SimulatedAgentRuntime(agentOptions);
        var agentSession = new AgentSessionManager(agentOptions, agentRuntime);
        var agentProcessor = new AgentCommandProcessor(
            agentOptions,
            agentSession,
            agentRuntime,
            NullLogger<AgentCommandProcessor>.Instance);
        var agentService = new AgentPipeService(
            agentOptions,
            agentProcessor,
            NullLogger<AgentPipeService>.Instance);
        using var agentCts = new CancellationTokenSource();
        var agentTask = agentService.RunAsync(agentCts.Token);

        var bridgeOptions = new BridgeOptions
        {
            PipeName = bridgePipeName,
            Mode = "wow-agent",
            AgentPipeName = agentPipeName,
            AgentConnectTimeoutMs = 1_000,
            AgentRequestTimeoutMs = 1_000,
            AgentEvasionProfile = "off",
        };
        var bridgeService = new PipeBridgeService(
            bridgeOptions,
            new WowAgentBridgeCommandExecutor(bridgeOptions),
            NullLogger<PipeBridgeService>.Instance);
        using var bridgeCts = new CancellationTokenSource();
        var bridgeTask = bridgeService.RunAsync(bridgeCts.Token);

        var botOptions = new BotOptions
        {
            CommandMmfName = $"TalosForge.Cmd.WowAgent.{suffix}",
            EventMmfName = $"TalosForge.Evt.WowAgent.{suffix}",
            RingCapacityBytes = 8192,
            UnlockerTimeoutMs = 1_000,
            UnlockerRetryCount = 0,
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
            ExecutorMode = "adapter",
            AdapterBackendMode = "pipe",
            AdapterPipeName = bridgePipeName,
            AdapterConnectTimeoutMs = 1_000,
            AdapterRequestTimeoutMs = 1_000,
        };

        using var host = new UnlockerHostService(
            hostOptions,
            new AdapterCommandExecutor(new NamedPipeAdapterBackend(hostOptions)),
            NullLogger<UnlockerHostService>.Instance);
        using var client = new SharedMemoryUnlockerClient(botOptions);
        using var hostCts = new CancellationTokenSource();
        var hostTask = host.RunAsync(hostCts.Token);

        try
        {
            var ack = await client.SendAsync(
                new UnlockerCommand(
                    CommandId: 12_001,
                    Opcode: UnlockerOpcode.LuaDoString,
                    PayloadJson: "{\"code\":\"print('agent')\"}",
                    TimestampUtc: DateTimeOffset.UtcNow),
                CancellationToken.None);

            Assert.True(ack.Success, $"{ack.Message} | {ack.PayloadJson}");
            Assert.Equal("ACK:LuaDoString", ack.Message);
            Assert.NotNull(ack.PayloadJson);
        }
        finally
        {
            hostCts.Cancel();
            await hostTask;

            bridgeCts.Cancel();
            await bridgeTask;

            agentCts.Cancel();
            await agentTask;
        }
    }
}
