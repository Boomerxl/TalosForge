using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;
using TalosForge.Core.Movement;
using Xunit;

namespace TalosForge.Tests.Core;

public sealed class UnlockerMovementControllerTests
{
    [Fact]
    public async Task StopAsync_Sends_Stop_Opcode()
    {
        var client = new CollectingUnlockerClient();
        var controller = new UnlockerMovementController(
            client,
            new MovementPolicy
            {
                RandomizedDelayMinMs = 0,
                RandomizedDelayMaxMs = 0,
            },
            new Random(123));

        await controller.StopAsync(CancellationToken.None);

        var command = Assert.Single(client.Commands);
        Assert.Equal(UnlockerOpcode.Stop, command.Opcode);
        Assert.Equal("{}", command.PayloadJson);
    }

    private sealed class CollectingUnlockerClient : IUnlockerClient
    {
        public List<UnlockerCommand> Commands { get; } = new();

        public void Dispose()
        {
        }

        public Task<UnlockerAck> SendAsync(UnlockerCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new UnlockerAck(
                command.CommandId,
                true,
                "ok",
                command.PayloadJson,
                DateTimeOffset.UtcNow));
        }
    }
}
