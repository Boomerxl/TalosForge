using Microsoft.Extensions.Logging;
using TalosForge.Core.Models;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.Navigation;

/// <summary>
/// High-level navigation engine that follows a path of waypoints using
/// the movement controller. Handles stuck detection and path recalculation.
/// </summary>
public sealed class NavigationEngine
{
    private readonly IPathfinder _pathfinder;
    private readonly ILogger<NavigationEngine>? _logger;
    private readonly float _waypointReachedDistance;

    private List<Vector3>? _currentPath;
    private int _currentWaypointIndex;
    private NavigationRequest? _activeRequest;
    private Vector3 _lastPosition;
    private DateTimeOffset _lastMoveCheckUtc = DateTimeOffset.MinValue;
    private float _stuckAccumulator;

    public NavigationEngine(IPathfinder pathfinder, ILogger<NavigationEngine>? logger = null, float waypointReachedDistance = 3.0f)
    {
        _pathfinder = pathfinder;
        _logger = logger;
        _waypointReachedDistance = waypointReachedDistance;
    }

    public NavigationStatus Status { get; private set; } = NavigationStatus.Idle;
    public Vector3? CurrentWaypoint => _currentPath != null && _currentWaypointIndex < _currentPath.Count
        ? _currentPath[_currentWaypointIndex]
        : null;
    public int WaypointsRemaining => _currentPath != null
        ? Math.Max(0, _currentPath.Count - _currentWaypointIndex)
        : 0;

    public bool StartNavigation(NavigationRequest request, Vector3 currentPosition)
    {
        _activeRequest = request;
        _stuckAccumulator = 0;
        _lastPosition = currentPosition;
        _lastMoveCheckUtc = DateTimeOffset.UtcNow;

        var path = _pathfinder.FindPath(currentPosition, request.Destination);
        if (path.Count < 2)
        {
            Status = NavigationStatus.Failed;
            _logger?.LogWarning("Pathfinding failed from {Start} to {End}", currentPosition, request.Destination);
            return false;
        }

        _currentPath = new List<Vector3>(path);
        _currentWaypointIndex = 1;
        Status = NavigationStatus.Moving;

        _logger?.LogInformation("Navigation started: {WaypointCount} waypoints to {Dest}",
            _currentPath.Count, request.Destination);
        return true;
    }

    public void Stop()
    {
        _currentPath = null;
        _activeRequest = null;
        _currentWaypointIndex = 0;
        Status = NavigationStatus.Idle;
    }

    public NavigationResult Update(Vector3 currentPosition)
    {
        if (_activeRequest == null || _currentPath == null || Status != NavigationStatus.Moving)
        {
            return new NavigationResult(Status, null, 0, float.MaxValue);
        }

        var distToDest = Distance(currentPosition, _activeRequest.Destination);

        if (distToDest <= _activeRequest.ArrivalDistance)
        {
            Status = NavigationStatus.Arrived;
            _currentPath = null;
            _logger?.LogInformation("Navigation arrived at destination.");
            return new NavigationResult(NavigationStatus.Arrived, null, 0, distToDest);
        }

        if (_currentWaypointIndex < _currentPath.Count)
        {
            var wp = _currentPath[_currentWaypointIndex];
            var distToWp = Distance(currentPosition, wp);

            if (distToWp <= _waypointReachedDistance)
            {
                _currentWaypointIndex++;
                _stuckAccumulator = 0;
            }
        }

        if (_currentWaypointIndex >= _currentPath.Count)
        {
            Status = NavigationStatus.Arrived;
            _currentPath = null;
            return new NavigationResult(NavigationStatus.Arrived, null, 0, distToDest);
        }

        var now = DateTimeOffset.UtcNow;
        var elapsed = (float)(now - _lastMoveCheckUtc).TotalSeconds;
        if (elapsed > 0.5f)
        {
            var moved = Distance(currentPosition, _lastPosition);
            if (moved < 0.5f)
            {
                _stuckAccumulator += elapsed;
            }
            else
            {
                _stuckAccumulator = Math.Max(0, _stuckAccumulator - elapsed * 0.5f);
            }

            _lastPosition = currentPosition;
            _lastMoveCheckUtc = now;

            if (_stuckAccumulator >= _activeRequest.StuckTimeoutSec)
            {
                Status = NavigationStatus.Stuck;
                _logger?.LogWarning("Navigation stuck at {Position}, accumulator={Acc:F1}s",
                    currentPosition, _stuckAccumulator);
                return new NavigationResult(NavigationStatus.Stuck, CurrentWaypoint, WaypointsRemaining, distToDest);
            }
        }

        return new NavigationResult(NavigationStatus.Moving, CurrentWaypoint, WaypointsRemaining, distToDest);
    }

    public bool TryUnstick(Vector3 currentPosition)
    {
        if (_activeRequest == null)
            return false;

        _stuckAccumulator = 0;
        _lastPosition = currentPosition;
        Status = NavigationStatus.Moving;

        var path = _pathfinder.FindPath(currentPosition, _activeRequest.Destination);
        if (path.Count < 2)
        {
            Status = NavigationStatus.Failed;
            return false;
        }

        _currentPath = new List<Vector3>(path);
        _currentWaypointIndex = 1;
        _logger?.LogInformation("Recalculated path after stuck: {WaypointCount} waypoints", _currentPath.Count);
        return true;
    }

    private static float Distance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
