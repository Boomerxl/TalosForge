using TalosForge.Core.Models;

namespace TalosForge.Core.Navigation;

public interface IPathfinder
{
    IReadOnlyList<Vector3> FindPath(Vector3 start, Vector3 end);
    bool IsLoaded(int mapId);
}

public interface INavigationService
{
    IReadOnlyList<Vector3> BuildRoute(Vector3 start, Vector3 end);
    bool IsReady { get; }
}

/// <summary>
/// Straight-line fallback when no mesh data is available.
/// </summary>
public sealed class StraightLinePathfinder : IPathfinder
{
    public IReadOnlyList<Vector3> FindPath(Vector3 start, Vector3 end)
    {
        return new[] { start, end };
    }

    public bool IsLoaded(int mapId) => false;
}

public enum NavigationStatus
{
    Idle,
    Moving,
    Stuck,
    Arrived,
    Failed,
}

public sealed record NavigationRequest(
    Vector3 Destination,
    float ArrivalDistance = 2.0f,
    float StuckTimeoutSec = 5.0f);

public sealed record NavigationResult(
    NavigationStatus Status,
    Vector3? CurrentWaypoint,
    int WaypointsRemaining,
    float DistanceToDestination);
