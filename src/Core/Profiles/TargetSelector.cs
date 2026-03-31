using TalosForge.Core.Models;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.Profiles;

/// <summary>
/// Selects the best target to pull based on profile filters, distance, and level.
/// </summary>
public sealed class TargetSelector
{
    private readonly TargetFilter _filter;
    private readonly HashSet<ulong> _blacklistedGuids = new();

    public TargetSelector(TargetFilter filter)
    {
        _filter = filter;
    }

    public void BlacklistGuid(ulong guid, TimeSpan duration)
    {
        _blacklistedGuids.Add(guid);
    }

    public void ClearBlacklist()
    {
        _blacklistedGuids.Clear();
    }

    public WowObjectSnapshot? SelectTarget(IBotContext context)
    {
        if (context.Me == null) return null;

        var candidates = context.Snapshot.Objects
            .Where(o => IsValidTarget(o, context))
            .OrderBy(o => context.DistanceTo(o))
            .ToList();

        return candidates.FirstOrDefault();
    }

    private bool IsValidTarget(WowObjectSnapshot obj, IBotContext context)
    {
        if (obj.Type != 3)
            return false;

        if (obj.IsDead || obj.IsLocalPlayer)
            return false;

        if (_blacklistedGuids.Contains(obj.Guid))
            return false;

        var level = obj.Level ?? 0;
        if (level < _filter.MinLevel || level > _filter.MaxLevel)
            return false;

        var distance = context.DistanceTo(obj);
        if (distance > _filter.MaxPullDistance)
            return false;

        if (_filter.WhitelistedEntryIds.Count > 0)
        {
            if (obj.EntryId == null || !_filter.WhitelistedEntryIds.Contains(obj.EntryId.Value))
                return false;
        }

        if (obj.EntryId != null && _filter.BlacklistedEntryIds.Contains(obj.EntryId.Value))
            return false;

        if (_filter.IgnoreTapped && obj.DynamicFlags.HasValue)
        {
            const uint tapped = 0x00000004;
            const uint tappedByMe = 0x00000008;
            if ((obj.DynamicFlags.Value & tapped) != 0 && (obj.DynamicFlags.Value & tappedByMe) == 0)
                return false;
        }

        if (obj.UnitFlags.HasValue)
        {
            const uint nonAttackable = 0x00000002 | 0x00000100;
            if ((obj.UnitFlags.Value & nonAttackable) != 0)
                return false;
        }

        return true;
    }
}
