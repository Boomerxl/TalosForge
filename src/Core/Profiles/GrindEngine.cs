using Microsoft.Extensions.Logging;
using TalosForge.Core.Models;
using TalosForge.Core.Navigation;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.Profiles;

/// <summary>
/// State machine that drives the grind loop:
/// Travel -> Pull -> Combat -> Loot -> (Skin) -> (Rest) -> (Vendor) -> Travel
/// </summary>
public sealed class GrindEngine
{
    private readonly GrindProfile _profile;
    private readonly TargetSelector _targetSelector;
    private readonly NavigationEngine _navigation;
    private readonly ILogger<GrindEngine>? _logger;

    private int _currentHotspotIndex;
    private ulong _currentTargetGuid;
    private DateTimeOffset _restStartUtc;

    public GrindEngine(
        GrindProfile profile,
        NavigationEngine navigation,
        ILogger<GrindEngine>? logger = null)
    {
        _profile = profile;
        _targetSelector = new TargetSelector(profile.TargetFilter);
        _navigation = navigation;
        _logger = logger;
    }

    public GrindState State { get; private set; } = GrindState.Idle;
    public int KillCount { get; private set; }
    public int LootCount { get; private set; }

    public async Task TickAsync(IBotContext ctx, CancellationToken ct)
    {
        if (ctx.Me == null) return;

        if (ctx.Me.Health == 0)
        {
            State = GrindState.Dead;
            return;
        }

        switch (State)
        {
            case GrindState.Idle:
                TransitionTo(GrindState.Traveling);
                break;

            case GrindState.Traveling:
                await HandleTraveling(ctx, ct).ConfigureAwait(false);
                break;

            case GrindState.Pulling:
                await HandlePulling(ctx, ct).ConfigureAwait(false);
                break;

            case GrindState.Combat:
                await HandleCombat(ctx, ct).ConfigureAwait(false);
                break;

            case GrindState.Looting:
                await HandleLooting(ctx, ct).ConfigureAwait(false);
                break;

            case GrindState.Resting:
                HandleResting(ctx);
                break;

            case GrindState.Dead:
                if (ctx.Me.Health > 0)
                    TransitionTo(GrindState.Resting);
                break;
        }
    }

    private async Task HandleTraveling(IBotContext ctx, CancellationToken ct)
    {
        if (ctx.Me!.InCombat)
        {
            TransitionTo(GrindState.Combat);
            return;
        }

        if (ShouldRest(ctx))
        {
            TransitionTo(GrindState.Resting);
            return;
        }

        var target = _targetSelector.SelectTarget(ctx);
        if (target != null)
        {
            _currentTargetGuid = target.Guid;
            TransitionTo(GrindState.Pulling);
            return;
        }

        var hotspot = GetCurrentHotspot();
        if (hotspot == null) return;

        if (_navigation.Status != NavigationStatus.Moving)
        {
            _navigation.StartNavigation(
                new NavigationRequest(hotspot.Position, hotspot.Radius),
                ctx.Me.Position);
        }

        var navResult = _navigation.Update(ctx.Me.Position);

        if (navResult.Status == NavigationStatus.Arrived)
        {
            _currentHotspotIndex = (_currentHotspotIndex + 1) % _profile.Hotspots.Count;
            _navigation.Stop();
        }
        else if (navResult.Status == NavigationStatus.Stuck)
        {
            _navigation.TryUnstick(ctx.Me.Position);
        }
        else if (navResult.CurrentWaypoint.HasValue)
        {
            await ctx.MoveToAsync(navResult.CurrentWaypoint.Value, ct).ConfigureAwait(false);
        }
    }

    private async Task HandlePulling(IBotContext ctx, CancellationToken ct)
    {
        if (ctx.Me!.InCombat)
        {
            TransitionTo(GrindState.Combat);
            return;
        }

        var target = ctx.Snapshot.Objects.FirstOrDefault(o => o.Guid == _currentTargetGuid);
        if (target == null || target.IsDead)
        {
            TransitionTo(GrindState.Traveling);
            return;
        }

        await ctx.TargetUnitAsync(target.Guid, ct).ConfigureAwait(false);
    }

    private async Task HandleCombat(IBotContext ctx, CancellationToken ct)
    {
        if (!ctx.Me!.InCombat)
        {
            KillCount++;
            if (_profile.Loot.AutoLoot)
            {
                TransitionTo(GrindState.Looting);
            }
            else
            {
                TransitionTo(ShouldRest(ctx) ? GrindState.Resting : GrindState.Traveling);
            }
            return;
        }

        var target = ctx.Target;
        if (target == null || target.IsDead)
        {
            var newTarget = _targetSelector.SelectTarget(ctx);
            if (newTarget != null)
            {
                await ctx.TargetUnitAsync(newTarget.Guid, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleLooting(IBotContext ctx, CancellationToken ct)
    {
        var lootable = ctx.Snapshot.Objects
            .Where(o => o.IsDead && o.DynamicFlags.HasValue &&
                        (o.DynamicFlags.Value & Offsets.UNIT_DYNAMIC_FLAG_LOOTABLE) != 0 &&
                        ctx.DistanceTo(o) <= _profile.Loot.LootRange + 5f)
            .OrderBy(o => ctx.DistanceTo(o))
            .FirstOrDefault();

        if (lootable == null)
        {
            LootCount++;
            TransitionTo(ShouldRest(ctx) ? GrindState.Resting : GrindState.Traveling);
            return;
        }

        if (ctx.DistanceTo(lootable) > _profile.Loot.LootRange)
        {
            await ctx.MoveToAsync(lootable.Position, ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.InteractAsync(lootable.Guid, ct).ConfigureAwait(false);
        }
    }

    private void HandleResting(IBotContext ctx)
    {
        var hpPct = ctx.Me!.MaxHealth > 0
            ? 100f * ctx.Me.Health.GetValueOrDefault() / ctx.Me.MaxHealth.Value
            : 100;
        var manaPct = ctx.Me.MaxMana > 0
            ? 100f * ctx.Me.Mana.GetValueOrDefault() / ctx.Me.MaxMana.Value
            : 100;

        if (hpPct >= _profile.Rest.HpPercentToResume && manaPct >= _profile.Rest.ManaPercentToResume)
        {
            TransitionTo(GrindState.Traveling);
        }
    }

    private bool ShouldRest(IBotContext ctx)
    {
        var hpPct = ctx.Me!.MaxHealth > 0
            ? 100f * ctx.Me.Health.GetValueOrDefault() / ctx.Me.MaxHealth.Value
            : 100;
        var manaPct = ctx.Me.MaxMana > 0
            ? 100f * ctx.Me.Mana.GetValueOrDefault() / ctx.Me.MaxMana.Value
            : 100;

        return hpPct < _profile.Rest.HpPercentToEat || manaPct < _profile.Rest.ManaPercentToDrink;
    }

    private Hotspot? GetCurrentHotspot()
    {
        if (_profile.Hotspots.Count == 0) return null;
        return _profile.Hotspots[_currentHotspotIndex % _profile.Hotspots.Count];
    }

    private void TransitionTo(GrindState newState)
    {
        if (State != newState)
        {
            _logger?.LogDebug("Grind state: {OldState} -> {NewState}", State, newState);
            State = newState;
        }
    }
}
