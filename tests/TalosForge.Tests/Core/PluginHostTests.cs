using Microsoft.Extensions.Logging.Abstractions;
using SampleCombatPlugin;
using System.Text.Json;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;
using TalosForge.Core.Plugins;
using Xunit;

namespace TalosForge.Tests.Core;

public sealed class PluginHostTests
{
    [Fact]
    public async Task Loads_Plugin_And_Dispatches_Command_When_Target_Is_Valid()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TalosForge.PluginTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var assemblyPath = typeof(SampleCombatPlugin.SampleCombatPlugin).Assembly.Location;
            var assemblyFile = Path.GetFileName(assemblyPath);
            File.Copy(assemblyPath, Path.Combine(tempDir, assemblyFile), overwrite: true);

            var manifest = new
            {
                name = "SampleCombatPlugin",
                assembly = assemblyFile,
                type = "SampleCombatPlugin.SampleCombatPlugin",
                minimumCoreVersion = "1.0.0",
            };
            File.WriteAllText(
                Path.Combine(tempDir, "sample.plugin.json"),
                JsonSerializer.Serialize(manifest));

            using var host = new PluginHost(tempDir, NullLogger<PluginHost>.Instance);
            using var unlocker = new CollectingUnlockerClient();
            host.LoadPlugins();

            var snapshot = new WorldSnapshot(
                TickId: 1,
                TimestampUtc: DateTimeOffset.UtcNow,
                Objects: Array.Empty<WowObjectSnapshot>(),
                Player: new PlayerSnapshot(1, new Vector3(0, 0, 0), 0, 999, false, false, false, false),
                Success: true,
                ErrorMessage: null);

            var sent = await host.TickAsync(snapshot, Array.Empty<BotEvent>(), unlocker, CancellationToken.None);

            Assert.True(host.LoadedPluginNames.Count > 0);
            Assert.True(sent >= 1);
            Assert.Single(unlocker.Commands);
            Assert.Equal(UnlockerOpcode.LuaDoString, unlocker.Commands[0].Opcode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class CollectingUnlockerClient : IUnlockerClient
    {
        public List<UnlockerCommand> Commands { get; } = new();

        public Task<UnlockerAck> SendAsync(UnlockerCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new UnlockerAck(command.CommandId, true, "ok", command.PayloadJson, DateTimeOffset.UtcNow));
        }

        public void Dispose()
        {
        }
    }
}
