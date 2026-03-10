using TalosForge.Core.Models;

namespace TalosForge.Core.Navigation;

public interface IPathfinder
{
    IReadOnlyList<Vector3> FindPath(Vector3 start, Vector3 end);
}

public interface INavigationService
{
    IReadOnlyList<Vector3> BuildRoute(Vector3 start, Vector3 end);
}

/// <summary>
/// Placeholder for future TrinityCore MMap integration.
/// </summary>
public sealed class MmapNavigationStub : INavigationService, IPathfinder
{
    public IReadOnlyList<Vector3> BuildRoute(Vector3 start, Vector3 end)
    {
        return FindPath(start, end);
    }

    public IReadOnlyList<Vector3> FindPath(Vector3 start, Vector3 end)
    {
        return new[] { start, end };
    }
}
