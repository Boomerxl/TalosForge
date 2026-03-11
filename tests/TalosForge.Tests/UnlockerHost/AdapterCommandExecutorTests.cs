using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Abstractions;
using TalosForge.UnlockerHost.Execution;
using TalosForge.UnlockerHost.Models;
using Xunit;

namespace TalosForge.Tests.UnlockerHost;

public sealed class AdapterCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_Valid_Lua_Payload_Dispatches_To_Backend()
    {
        var backend = new RecordingBackend(CommandExecutionResult.Ok("backend-ok"));
        var executor = new AdapterCommandExecutor(backend);
        var command = new UnlockerCommand(
            1,
            UnlockerOpcode.LuaDoString,
            "{\"code\":\"print('hi')\"}",
            DateTimeOffset.UtcNow);

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("backend-ok", result.Message);
        Assert.NotNull(result.PayloadJson);
        Assert.Contains("\"code\":\"OK\"", result.PayloadJson, StringComparison.Ordinal);
        var dispatched = Assert.Single(backend.Commands);
        using var payload = System.Text.Json.JsonDocument.Parse(dispatched.PayloadJson);
        Assert.Equal("print('hi')", payload.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_Invalid_Stop_Payload_Returns_InvalidPayload()
    {
        var backend = new RecordingBackend(CommandExecutionResult.Ok("unused"));
        var executor = new AdapterCommandExecutor(backend);
        var command = new UnlockerCommand(
            2,
            UnlockerOpcode.Stop,
            "{\"stop\":true}",
            DateTimeOffset.UtcNow);

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith(AdapterResultCodes.InvalidPayload, result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.PayloadJson);
        Assert.Contains($"\"code\":\"{AdapterResultCodes.InvalidPayload}\"", result.PayloadJson, StringComparison.Ordinal);
        Assert.Empty(backend.Commands);
    }

    [Fact]
    public async Task ExecuteAsync_Unsupported_Opcode_Returns_UnsupportedOpcode_Code()
    {
        var backend = new RecordingBackend(CommandExecutionResult.Ok("unused"));
        var executor = new AdapterCommandExecutor(backend);
        var command = new UnlockerCommand(
            3,
            (UnlockerOpcode)999,
            "{}",
            DateTimeOffset.UtcNow);

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith(AdapterResultCodes.UnsupportedOpcode, result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.PayloadJson);
        Assert.Contains($"\"code\":\"{AdapterResultCodes.UnsupportedOpcode}\"", result.PayloadJson, StringComparison.Ordinal);
        Assert.Empty(backend.Commands);
    }

    [Fact]
    public async Task ExecuteAsync_Backend_Exception_Returns_BackendError()
    {
        var backend = new ThrowingBackend();
        var executor = new AdapterCommandExecutor(backend);
        var command = new UnlockerCommand(
            4,
            UnlockerOpcode.Stop,
            "{}",
            DateTimeOffset.UtcNow);

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith(AdapterResultCodes.BackendError, result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.PayloadJson);
        Assert.Contains($"\"code\":\"{AdapterResultCodes.BackendError}\"", result.PayloadJson, StringComparison.Ordinal);
    }

    private sealed class RecordingBackend : IAdapterBackend
    {
        private readonly CommandExecutionResult _result;

        public RecordingBackend(CommandExecutionResult result)
        {
            _result = result;
        }

        public List<UnlockerCommand> Commands { get; } = new();

        public ValueTask<CommandExecutionResult> ExecuteAsync(UnlockerCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class ThrowingBackend : IAdapterBackend
    {
        public ValueTask<CommandExecutionResult> ExecuteAsync(UnlockerCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("boom");
        }
    }
}
