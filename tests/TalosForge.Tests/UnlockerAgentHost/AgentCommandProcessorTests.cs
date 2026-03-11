using Microsoft.Extensions.Logging.Abstractions;
using TalosForge.UnlockerAgentHost.Execution;
using TalosForge.UnlockerAgentHost.Models;
using TalosForge.UnlockerAgentHost.Runtime;
using Xunit;

namespace TalosForge.Tests.UnlockerAgentHost;

public sealed class AgentCommandProcessorTests
{
    [Fact]
    public async Task ProcessAsync_Retries_Transient_Failures_With_Backoff_Then_Succeeds()
    {
        var options = new AgentHostOptions
        {
            WowProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName,
            RetryCount = 2,
            BackoffBaseMs = 10,
            BackoffMaxMs = 10,
            RequestTimeoutMs = 300
        };
        await using var runtime = new FakeRuntime(
            failCountBeforeSuccess: 1,
            transientFailure: true);
        var sessionManager = new AgentSessionManager(options, runtime);
        var processor = new AgentCommandProcessor(
            options,
            sessionManager,
            runtime,
            NullLogger<AgentCommandProcessor>.Instance);

        var response = await processor.ProcessAsync(
            new AgentPipeRequest(
                Version: 1,
                CommandId: 9,
                Opcode: "Stop",
                OpcodeValue: 7,
                PayloadJson: "{}",
                TimestampUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RequestTimeoutMs: 100,
                EvasionProfile: "off"),
            CancellationToken.None);

        Assert.True(response.Success, response.Message);
        Assert.Equal("OK", response.Code);
        Assert.Equal(2, runtime.ExecuteCalls);
        Assert.Contains("\"Attempts\":2", response.DiagnosticsJson!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAsync_Returns_NotInGame_When_Wow_Process_Missing()
    {
        var options = new AgentHostOptions
        {
            WowProcessName = $"WoW-Missing-{Guid.NewGuid():N}"
        };
        await using var runtime = new FakeRuntime(
            failCountBeforeSuccess: 0,
            transientFailure: false);
        var sessionManager = new AgentSessionManager(options, runtime);
        var processor = new AgentCommandProcessor(
            options,
            sessionManager,
            runtime,
            NullLogger<AgentCommandProcessor>.Instance);

        var response = await processor.ProcessAsync(
            new AgentPipeRequest(
                Version: 1,
                CommandId: 15,
                Opcode: "LuaDoString",
                OpcodeValue: 1,
                PayloadJson: "{\"code\":\"print('x')\"}",
                TimestampUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RequestTimeoutMs: 100,
                EvasionProfile: "full"),
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal(AgentResultCodes.NotInGame, response.Code);
        Assert.Equal(0, runtime.ExecuteCalls);
    }

    private sealed class FakeRuntime : IAgentRuntime
    {
        private readonly int _failCountBeforeSuccess;
        private readonly bool _transientFailure;
        private int _calls;

        public FakeRuntime(int failCountBeforeSuccess, bool transientFailure)
        {
            _failCountBeforeSuccess = failCountBeforeSuccess;
            _transientFailure = transientFailure;
        }

        public int ExecuteCalls => _calls;

        public ValueTask<AgentRuntimeReadyResult> EnsureReadyAsync(string evasionProfile, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new AgentRuntimeReadyResult(true, "ready", AgentResultCodes.Ok));
        }

        public ValueTask<AgentRuntimeExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls <= _failCountBeforeSuccess)
            {
                return ValueTask.FromResult(
                    new AgentRuntimeExecutionResult(
                        false,
                        "transient",
                        null,
                        AgentResultCodes.BackendUnavailable,
                        _transientFailure));
            }

            return ValueTask.FromResult(
                new AgentRuntimeExecutionResult(
                    true,
                    $"ACK:{request.Opcode}",
                    request.PayloadJson,
                    AgentResultCodes.Ok,
                    TransientFailure: false));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
