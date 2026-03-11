using System.IO.Pipes;
using System.Text.Json;
using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Configuration;
using TalosForge.UnlockerHost.Execution;
using TalosForge.UnlockerHost.Models;
using Xunit;

namespace TalosForge.Tests.UnlockerHost;

public sealed class NamedPipeAdapterBackendTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExecuteAsync_Forwards_Request_And_Parses_Success_Response()
    {
        var pipeName = $"TalosForge.Adapter.Tests.{Guid.NewGuid():N}";
        var options = new UnlockerHostOptions
        {
            AdapterPipeName = pipeName,
            AdapterConnectTimeoutMs = 500,
            AdapterRequestTimeoutMs = 1000,
        };
        var backend = new NamedPipeAdapterBackend(options);
        AdapterPipeRequest? capturedRequest = null;

        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server);
            using var writer = new StreamWriter(server) { AutoFlush = true };

            var line = await reader.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                capturedRequest = JsonSerializer.Deserialize<AdapterPipeRequest>(line!, JsonOptions);
            }

            var response = new AdapterPipeResponse(
                Success: true,
                Message: "bridge-ok",
                PayloadJson: "{\"handled\":true}",
                Code: AdapterResultCodes.Ok);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        });

        var result = await backend.ExecuteAsync(
            new UnlockerCommand(1, UnlockerOpcode.LuaDoString, "{\"code\":\"print('ok')\"}", DateTimeOffset.UtcNow),
            CancellationToken.None);

        await serverTask;

        Assert.True(result.Success, $"{result.Message} | {result.PayloadJson}");
        Assert.Equal("bridge-ok", result.Message);
        Assert.Equal("{\"handled\":true}", result.PayloadJson);
        Assert.NotNull(capturedRequest);
        Assert.Equal("LuaDoString", capturedRequest!.Opcode);
        Assert.Equal(1, capturedRequest.Version);
    }

    [Fact]
    public async Task ExecuteAsync_Returns_BackendUnavailable_When_Pipe_Not_Available()
    {
        var options = new UnlockerHostOptions
        {
            AdapterPipeName = $"TalosForge.Adapter.Missing.{Guid.NewGuid():N}",
            AdapterConnectTimeoutMs = 50,
            AdapterRequestTimeoutMs = 100,
        };
        var backend = new NamedPipeAdapterBackend(options);

        var result = await backend.ExecuteAsync(
            new UnlockerCommand(2, UnlockerOpcode.Stop, "{}", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith(AdapterResultCodes.BackendUnavailable, result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.PayloadJson);
        Assert.Contains($"\"code\":\"{AdapterResultCodes.BackendUnavailable}\"", result.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_Returns_BackendError_On_Invalid_Response()
    {
        var pipeName = $"TalosForge.Adapter.Tests.Bad.{Guid.NewGuid():N}";
        var options = new UnlockerHostOptions
        {
            AdapterPipeName = pipeName,
            AdapterConnectTimeoutMs = 500,
            AdapterRequestTimeoutMs = 500,
        };
        var backend = new NamedPipeAdapterBackend(options);

        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server);
            using var writer = new StreamWriter(server) { AutoFlush = true };
            _ = await reader.ReadLineAsync();
            await writer.WriteLineAsync("not-json");
        });

        var result = await backend.ExecuteAsync(
            new UnlockerCommand(3, UnlockerOpcode.Stop, "{}", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith(AdapterResultCodes.BackendError, result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.PayloadJson);
        Assert.Contains($"\"code\":\"{AdapterResultCodes.BackendError}\"", result.PayloadJson, StringComparison.Ordinal);

        await serverTask;
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_Then_Recovery_Succeeds_On_Next_Request()
    {
        var pipeName = $"TalosForge.Adapter.Tests.TimeoutRecovery.{Guid.NewGuid():N}";
        var options = new UnlockerHostOptions
        {
            AdapterPipeName = pipeName,
            AdapterConnectTimeoutMs = 500,
            AdapterRequestTimeoutMs = 100,
        };
        var backend = new NamedPipeAdapterBackend(options);

        var firstServerTask = Task.Run(async () =>
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };
                _ = await reader.ReadLineAsync();
                await Task.Delay(300);

                var response = new AdapterPipeResponse(
                    Success: true,
                    Message: "late-response",
                    PayloadJson: "{\"late\":true}",
                    Code: AdapterResultCodes.Ok);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch (IOException)
            {
            }
        });

        var timeoutResult = await backend.ExecuteAsync(
            new UnlockerCommand(10, UnlockerOpcode.Stop, "{}", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.False(timeoutResult.Success);
        Assert.StartsWith(AdapterResultCodes.BackendError, timeoutResult.Message, StringComparison.Ordinal);
        Assert.NotNull(timeoutResult.PayloadJson);
        Assert.Contains($"\"code\":\"{AdapterResultCodes.BackendError}\"", timeoutResult.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("timeout", timeoutResult.PayloadJson, StringComparison.OrdinalIgnoreCase);

        await firstServerTask;

        var secondServerTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server);
            using var writer = new StreamWriter(server) { AutoFlush = true };
            _ = await reader.ReadLineAsync();
            var response = new AdapterPipeResponse(
                Success: true,
                Message: "recovered",
                PayloadJson: "{\"recovered\":true}",
                Code: AdapterResultCodes.Ok);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        });

        var recoveredResult = await backend.ExecuteAsync(
            new UnlockerCommand(11, UnlockerOpcode.Stop, "{}", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.True(recoveredResult.Success, $"{recoveredResult.Message} | {recoveredResult.PayloadJson}");
        Assert.Equal("recovered", recoveredResult.Message);
        Assert.Equal("{\"recovered\":true}", recoveredResult.PayloadJson);

        await secondServerTask;
    }
}
