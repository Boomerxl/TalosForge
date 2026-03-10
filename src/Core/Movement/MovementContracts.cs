using TalosForge.Core.Models;

namespace TalosForge.Core.Movement;

public interface IMovementController
{
    Task FaceToAsync(float facingRadians, CancellationToken cancellationToken);
    Task MoveToAsync(Vector3 destination, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Placeholder movement policy for future unlocker motion refinements.
/// </summary>
public sealed class MovementPolicy
{
    public float TurnSmoothingFactor { get; init; } = 0.15f;
    public int RandomizedDelayMinMs { get; init; } = 12;
    public int RandomizedDelayMaxMs { get; init; } = 35;
    public float OvershootCorrectionThreshold { get; init; } = 0.35f;
}
