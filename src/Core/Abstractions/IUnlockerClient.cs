using TalosForge.Core.Models;

namespace TalosForge.Core.Abstractions;

public interface IUnlockerClient : IDisposable
{
    Task<UnlockerAck> SendAsync(UnlockerCommand command, CancellationToken cancellationToken);
}
