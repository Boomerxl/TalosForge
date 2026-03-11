using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TalosForge.AdapterBridge.Execution;
using TalosForge.AdapterBridge.Models;
using TalosForge.AdapterBridge.Runtime;
using Xunit;

namespace TalosForge.Tests.AdapterBridge;

public sealed class PipeBridgeServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PipeBridgeService_MockMode_Responds_To_Request()
    {
        var options = new BridgeOptions
        {
            PipeName = $"TalosForge.AdapterBridge.Tests.{Guid.NewGuid():N}",
            Mode = "mock",
        };

        var service = new PipeBridgeService(
            options,
            new MockBridgeCommandExecutor(),
            NullLogger<PipeBridgeService>.Instance);

        using var cts = new CancellationTokenSource();
        var serviceTask = service.RunAsync(cts.Token);

        await using var client = new NamedPipeClientStream(
            ".",
            options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var request = new AdapterPipeRequest(
            Version: 1,
            CommandId: 100,
            Opcode: "LuaDoString",
            OpcodeValue: 1,
            PayloadJson: "{\"code\":\"print('bridge')\"}",
            TimestampUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        var responseLine = await reader.ReadLineAsync();
        Assert.False(string.IsNullOrWhiteSpace(responseLine));

        var response = JsonSerializer.Deserialize<AdapterPipeResponse>(responseLine!, JsonOptions);
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.Equal("ACK:LuaDoString", response.Message);

        cts.Cancel();
        await serviceTask;
    }

    [Fact]
    public async Task PipeBridgeService_Returns_InvalidRequest_For_Bad_Json()
    {
        var options = new BridgeOptions
        {
            PipeName = $"TalosForge.AdapterBridge.Tests.Bad.{Guid.NewGuid():N}",
            Mode = "mock",
        };

        var service = new PipeBridgeService(
            options,
            new MockBridgeCommandExecutor(),
            NullLogger<PipeBridgeService>.Instance);

        using var cts = new CancellationTokenSource();
        var serviceTask = service.RunAsync(cts.Token);

        await using var client = new NamedPipeClientStream(
            ".",
            options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        await writer.WriteLineAsync("{not-json");
        var responseLine = await reader.ReadLineAsync();
        Assert.False(string.IsNullOrWhiteSpace(responseLine));

        var response = JsonSerializer.Deserialize<AdapterPipeResponse>(responseLine!, JsonOptions);
        Assert.NotNull(response);
        Assert.False(response!.Success);
        Assert.Equal("BRIDGE_INVALID_REQUEST", response.Code);

        cts.Cancel();
        await serviceTask;
    }
}
