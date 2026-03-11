using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TalosForge.AdapterBridge.Execution;
using TalosForge.AdapterBridge.Models;
using TalosForge.AdapterBridge.Runtime;
using Xunit;

namespace TalosForge.Tests.AdapterBridge;

public sealed class WowAgentBridgeCommandExecutorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExecuteAsync_Returns_ConnectTimeout_When_Agent_Unavailable()
    {
        var options = new BridgeOptions
        {
            Mode = "wow-agent",
            AgentPipeName = $"TalosForge.Agent.Tests.{Guid.NewGuid():N}",
            AgentConnectTimeoutMs = 80,
            AgentRequestTimeoutMs = 120,
        };
        var executor = new WowAgentBridgeCommandExecutor(options);
        var request = new AdapterPipeRequest(
            1, 42, "Stop", 7, "{}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("BRIDGE_WOWAGENT_CONNECT_TIMEOUT", response.Code);
        Assert.NotNull(response.PayloadJson);
    }

    [Fact]
    public async Task ExecuteAsync_Forwards_Request_To_Agent_And_Maps_Response()
    {
        var pipeName = $"TalosForge.Agent.Tests.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var requestLine = await reader.ReadLineAsync();
            Assert.False(string.IsNullOrWhiteSpace(requestLine));

            var request = JsonSerializer.Deserialize<AgentPipeRequest>(requestLine!, JsonOptions);
            Assert.NotNull(request);
            Assert.Equal("LuaDoString", request!.Opcode);

            var response = new AgentPipeResponse(
                Success: true,
                Message: "ACK:LuaDoString",
                PayloadJson: null,
                Code: "OK",
                DiagnosticsJson: "{\"attempts\":1}",
                AgentState: "ready",
                CompletedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
        });

        var options = new BridgeOptions
        {
            Mode = "wow-agent",
            AgentPipeName = pipeName,
            AgentConnectTimeoutMs = 1_000,
            AgentRequestTimeoutMs = 1_000,
            AgentEvasionProfile = "full"
        };
        var executor = new WowAgentBridgeCommandExecutor(options);
        var adapterRequest = new AdapterPipeRequest(
            Version: 1,
            CommandId: 88,
            Opcode: "LuaDoString",
            OpcodeValue: 1,
            PayloadJson: "{\"code\":\"print('x')\"}",
            TimestampUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var responseFromBridge = await executor.ExecuteAsync(adapterRequest, CancellationToken.None);
        await serverTask;

        Assert.True(responseFromBridge.Success, responseFromBridge.Message);
        Assert.Equal("OK", responseFromBridge.Code);
        Assert.Equal("ACK:LuaDoString", responseFromBridge.Message);
        Assert.NotNull(responseFromBridge.PayloadJson);
        Assert.Contains("\"state\":\"ready\"", responseFromBridge.PayloadJson!, StringComparison.Ordinal);
    }
}
