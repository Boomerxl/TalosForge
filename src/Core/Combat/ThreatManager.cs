using TalosForge.Core.Models;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.Combat;

/// <summary>
/// Tracks nearby hostile units targeting the player and manages multi-target awareness.
/// </summary>
public sealed class ThreatManager
{
    public IReadOnlyList<WowObjectSnapshot> GetUnitsTargetingMe(IBotContext context)
    {
        if (context.Me == null) return Array.Empty<WowObjectSnapshot>();

        var myGuid = context.Me.Guid;
        return context.Snapshot.Objects
            .Where(o => o.Type == 3 && !o.IsDead && o.TargetGuid == myGuid)
            .OrderBy(o => context.DistanceTo(o))
            .ToList();
    }

    public int GetThreatCount(IBotContext context)
    {
        return GetUnitsTargetingMe(context).Count;
    }

    public WowObjectSnapshot? GetClosestThreat(IBotContext context)
    {
        return GetUnitsTargetingMe(context).FirstOrDefault();
    }

    public bool IsInDanger(IBotContext context, int maxAcceptableAdds = 3)
    {
        return GetThreatCount(context) > maxAcceptableAdds;
    }

    public WowObjectSnapshot? GetLowestHealthTarget(IBotContext context)
    {
        return GetUnitsTargetingMe(context)
            .Where(o => o.Health.HasValue && o.Health > 0)
            .OrderBy(o => o.Health)
            .FirstOrDefault();
    }
}
