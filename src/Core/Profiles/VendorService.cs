using Microsoft.Extensions.Logging;
using TalosForge.Core.Inventory;
using TalosForge.Core.Navigation;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.Profiles;

/// <summary>
/// Handles vendor runs: navigating to vendor, selling grey items, repairing, buying supplies.
/// </summary>
public sealed class VendorService
{
    private readonly VendorSettings _settings;
    private readonly NavigationEngine _navigation;
    private readonly InventoryManager _inventory;
    private readonly ILogger<VendorService>? _logger;

    private VendorPhase _phase = VendorPhase.Idle;

    public VendorService(
        VendorSettings settings,
        NavigationEngine navigation,
        InventoryManager inventory,
        ILogger<VendorService>? logger = null)
    {
        _settings = settings;
        _navigation = navigation;
        _inventory = inventory;
        _logger = logger;
    }

    public VendorPhase Phase => _phase;

    public async Task<bool> ShouldVendorAsync(IBotContext ctx, CancellationToken ct)
    {
        var freeSlots = await _inventory.GetFreeBagSlotsAsync(ctx, ct).ConfigureAwait(false);
        return freeSlots >= 0 && freeSlots <= _settings.BagSlotsToTrigger;
    }

    public async Task<bool> TickAsync(IBotContext ctx, CancellationToken ct)
    {
        if (ctx.Me == null) return false;

        switch (_phase)
        {
            case VendorPhase.Idle:
                _phase = VendorPhase.Traveling;
                _navigation.StartNavigation(
                    new NavigationRequest(_settings.Position, 4f),
                    ctx.Me.Position);
                _logger?.LogInformation("Starting vendor run.");
                return true;

            case VendorPhase.Traveling:
                var nav = _navigation.Update(ctx.Me.Position);
                if (nav.Status == NavigationStatus.Arrived)
                {
                    _phase = VendorPhase.Interacting;
                }
                else if (nav.Status == NavigationStatus.Stuck)
                {
                    _navigation.TryUnstick(ctx.Me.Position);
                }
                else if (nav.CurrentWaypoint.HasValue)
                {
                    await ctx.MoveToAsync(nav.CurrentWaypoint.Value, ct).ConfigureAwait(false);
                }
                return true;

            case VendorPhase.Interacting:
                if (_settings.NpcGuid != 0)
                    await ctx.InteractAsync(_settings.NpcGuid, ct).ConfigureAwait(false);
                else
                    await ctx.InteractAsync(ct).ConfigureAwait(false);

                _phase = VendorPhase.Selling;
                return true;

            case VendorPhase.Selling:
                if (_settings.SellGrey)
                    await _inventory.SellGreyItemsAsync(ctx, ct).ConfigureAwait(false);
                if (_settings.Repair)
                    await _inventory.RepairAllItemsAsync(ctx, ct).ConfigureAwait(false);

                _phase = VendorPhase.Done;
                _logger?.LogInformation("Vendor run complete.");
                return true;

            case VendorPhase.Done:
                _phase = VendorPhase.Idle;
                return false;
        }

        return false;
    }

    public void Reset()
    {
        _phase = VendorPhase.Idle;
    }
}

public enum VendorPhase
{
    Idle,
    Traveling,
    Interacting,
    Selling,
    Done,
}
