using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;

namespace TalosForge.Core.IPC;

/// <summary>
/// No-op unlocker transport used for local testing and bootstrap.
/// </summary>
public sealed class NullUnlockerClient : IUnlockerClient
{
    public Task<UnlockerAck> SendAsync(UnlockerCommand command, CancellationToken cancellationToken)
    {
        var ack = new UnlockerAck(
            command.CommandId,
            Success: true,
            Message: "No-op transport",
            PayloadJson: command.PayloadJson,
            TimestampUtc: DateTimeOffset.UtcNow);
        return Task.FromResult(ack);
    }

    public void Dispose()
    {
    }
}
