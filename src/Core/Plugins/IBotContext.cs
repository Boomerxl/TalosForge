using TalosForge.Core.Models;

namespace TalosForge.Core.Plugins;

/// <summary>
/// High-level API for bot plugins to query world state and execute actions.
/// Passed to plugins on each tick, providing both synchronous queries and async actions.
/// </summary>
public interface IBotContext
{
    PlayerSnapshot? Me { get; }
    WowObjectSnapshot? Target { get; }
    WorldSnapshot Snapshot { get; }

    IReadOnlyList<WowObjectSnapshot> NearbyUnits(float range);
    IReadOnlyList<WowObjectSnapshot> NearbyEnemies(float range);
    IReadOnlyList<WowObjectSnapshot> NearbyFriendlies(float range);

    bool HasAura(int spellId);
    bool HasAura(string spellName);
    bool TargetHasAura(int spellId);

    float DistanceTo(WowObjectSnapshot unit);
    float DistanceTo(Vector3 position);

    Task CastSpellAsync(string spellName, CancellationToken ct);
    Task CastSpellByIdAsync(int spellId, CancellationToken ct);
    Task TargetUnitAsync(ulong guid, CancellationToken ct);
    Task MoveToAsync(Vector3 position, CancellationToken ct);
    Task FaceAsync(float radians, CancellationToken ct);
    Task InteractAsync(ulong guid, CancellationToken ct);
    Task InteractAsync(CancellationToken ct);
    Task StopMovingAsync(CancellationToken ct);
    Task ExecuteLuaAsync(string code, CancellationToken ct);
    Task<string?> QueryLuaAsync(string code, CancellationToken ct);
}
