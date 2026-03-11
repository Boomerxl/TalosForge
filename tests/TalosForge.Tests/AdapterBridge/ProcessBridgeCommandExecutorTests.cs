using TalosForge.AdapterBridge.Execution;
using TalosForge.AdapterBridge.Models;
using TalosForge.AdapterBridge.Runtime;
using Xunit;

namespace TalosForge.Tests.AdapterBridge;

public sealed class ProcessBridgeCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_Returns_Config_Error_When_Command_Path_Missing()
    {
        var options = new BridgeOptions
        {
            Mode = "process",
            CommandPath = null,
        };
        var executor = new ProcessBridgeCommandExecutor(options);

        var response = await executor.ExecuteAsync(
            new AdapterPipeRequest(1, 1, "Stop", 7, "{}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("BRIDGE_CONFIG_ERROR", response.Code);
    }

    [Fact]
    public async Task ExecuteAsync_Returns_Start_Error_When_Command_Does_Not_Exist()
    {
        var options = new BridgeOptions
        {
            Mode = "process",
            CommandPath = "this-command-does-not-exist-xyz.exe",
            CommandTimeoutMs = 200,
        };
        var executor = new ProcessBridgeCommandExecutor(options);

        var response = await executor.ExecuteAsync(
            new AdapterPipeRequest(1, 2, "Stop", 7, "{}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("BRIDGE_PROCESS_START_ERROR", response.Code);
    }
}
