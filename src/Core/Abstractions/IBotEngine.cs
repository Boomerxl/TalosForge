namespace TalosForge.Core.Abstractions;

public interface IBotEngine
{
    Task RunAsync(CancellationToken cancellationToken);
}
