using TalosForge.UnlockerAgentHost.Models;
using TalosForge.UnlockerAgentHost.Runtime;
using Xunit;

namespace TalosForge.Tests.UnlockerAgentHost;

public sealed class SimulatedAgentRuntimeTests
{
    [Fact]
    public async Task ExecuteAsync_Returns_HookNotReady_When_Not_Initialized()
    {
        var options = new AgentHostOptions();
        await using var runtime = new SimulatedAgentRuntime(options);

        var result = await runtime.ExecuteAsync(
            new AgentExecutionRequest(1, "Stop", "{}", 100),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AgentResultCodes.HookNotReady, result.Code);
    }

    [Fact]
    public async Task ExecuteAsync_Processes_Queued_Commands_After_Ready()
    {
        var options = new AgentHostOptions();
        await using var runtime = new SimulatedAgentRuntime(options);
        var ready = await runtime.EnsureReadyAsync("off", CancellationToken.None);
        Assert.True(ready.Success, ready.Message);

        var first = runtime.ExecuteAsync(new AgentExecutionRequest(1, "LuaDoString", "{\"code\":\"x\"}", 250), CancellationToken.None).AsTask();
        var second = runtime.ExecuteAsync(new AgentExecutionRequest(2, "Stop", "{}", 250), CancellationToken.None).AsTask();
        await Task.WhenAll(first, second);

        Assert.True(first.Result.Success, first.Result.Message);
        Assert.True(second.Result.Success, second.Result.Message);
        Assert.Equal("ACK:LuaDoString", first.Result.Message);
        Assert.Equal("ACK:Stop", second.Result.Message);
    }

    [Fact]
    public async Task EnsureReadyAsync_Returns_Evasion_Init_Failure_When_Configured()
    {
        var options = new AgentHostOptions
        {
            SimulateEvasionInitFailure = true
        };
        await using var runtime = new SimulatedAgentRuntime(options);

        var ready = await runtime.EnsureReadyAsync("full", CancellationToken.None);

        Assert.False(ready.Success);
        Assert.Equal(AgentResultCodes.EvasionInitFailed, ready.Code);
    }
}
