using TalosForge.AdapterBridge.Execution;
using TalosForge.AdapterBridge.Models;
using TalosForge.AdapterBridge.Runtime;
using Xunit;

namespace TalosForge.Tests.AdapterBridge;

public sealed class WowCliBridgeCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_Returns_Config_Error_When_CommandPath_Missing()
    {
        var options = new BridgeOptions
        {
            Mode = "wow-cli",
            CommandPath = null,
        };
        var executor = new WowCliBridgeCommandExecutor(options);
        var request = new AdapterPipeRequest(
            1, 1, "Stop", 7, "{}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("BRIDGE_CONFIG_ERROR", response.Code);
    }

    [Fact]
    public async Task ExecuteAsync_Invokes_Powershell_Script_And_Returns_First_Stdout_Line()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"TalosForge.WowCli.{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "param([Parameter(ValueFromRemainingArguments=$true)][string[]]$args)\nWrite-Output (\"OK \" + ($args -join \"|\"))\nexit 0\n");

        try
        {
            var options = new BridgeOptions
            {
                Mode = "wow-cli",
                CommandPath = "powershell.exe",
                CommandArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                CommandTimeoutMs = 2000,
            };
            var executor = new WowCliBridgeCommandExecutor(options);
            var request = new AdapterPipeRequest(
                1,
                2,
                "MoveTo",
                5,
                "{\"x\":1.25,\"y\":2.5,\"z\":3.75,\"overshootThreshold\":0.35}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var response = await executor.ExecuteAsync(request, CancellationToken.None);

            Assert.True(response.Success, response.Message);
            Assert.Equal("OK moveto|1.25|2.5|3.75|0.35", response.Message);
            Assert.Equal("OK", response.Code);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Slow_Command_Within_Timeout_Succeeds()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"TalosForge.WowCli.SlowOk.{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "Start-Sleep -Milliseconds 250\nWrite-Output 'SLOW-OK'\nexit 0\n");

        try
        {
            var options = new BridgeOptions
            {
                Mode = "wow-cli",
                CommandPath = "powershell.exe",
                CommandArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                CommandTimeoutMs = 1_000,
            };
            var executor = new WowCliBridgeCommandExecutor(options);
            var request = new AdapterPipeRequest(
                1,
                3,
                "Stop",
                7,
                "{}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var response = await executor.ExecuteAsync(request, CancellationToken.None);

            Assert.True(response.Success, response.Message);
            Assert.Equal("SLOW-OK", response.Message);
            Assert.Equal("OK", response.Code);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Command_Timeout_Returns_Structured_Error()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"TalosForge.WowCli.Timeout.{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(
            scriptPath,
            "Start-Sleep -Milliseconds 800\nWrite-Output 'TOO-LATE'\nexit 0\n");

        try
        {
            var options = new BridgeOptions
            {
                Mode = "wow-cli",
                CommandPath = "powershell.exe",
                CommandArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                CommandTimeoutMs = 100,
            };
            var executor = new WowCliBridgeCommandExecutor(options);
            var request = new AdapterPipeRequest(
                1,
                4,
                "Stop",
                7,
                "{}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var response = await executor.ExecuteAsync(request, CancellationToken.None);

            Assert.False(response.Success);
            Assert.Equal("BRIDGE_WOWCLI_TIMEOUT", response.Code);
            Assert.NotNull(response.PayloadJson);
            Assert.Contains("\"code\":\"BRIDGE_WOWCLI_TIMEOUT\"", response.PayloadJson!, StringComparison.Ordinal);
            Assert.Contains("\"timeoutMs\":100", response.PayloadJson!, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void SplitCommandArgs_Respects_Quoted_Arguments()
    {
        var args = WowCliBridgeCommandExecutor.SplitCommandArgs("-NoProfile -File \"C:/A B/script.ps1\"");

        Assert.Equal(3, args.Count);
        Assert.Equal("-NoProfile", args[0]);
        Assert.Equal("-File", args[1]);
        Assert.Equal("C:/A B/script.ps1", args[2]);
    }
}
