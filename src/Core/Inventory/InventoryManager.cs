using TalosForge.Core.Plugins;

namespace TalosForge.Core.Inventory;

/// <summary>
/// Manages inventory operations: bag slot counting, item usage, and vendor selling.
/// Uses Lua queries through the bot context for game state access.
/// </summary>
public sealed class InventoryManager
{
    public async Task<int> GetFreeBagSlotsAsync(IBotContext context, CancellationToken ct)
    {
        var result = await context.QueryLuaAsync(
            "return (function() local f=0 for b=0,4 do local s=GetContainerNumFreeSlots(b) f=f+s end return tostring(f) end)()",
            ct).ConfigureAwait(false);

        return int.TryParse(result, out var slots) ? slots : -1;
    }

    public async Task UseItemByNameAsync(IBotContext context, string itemName, CancellationToken ct)
    {
        var lua = $"local n='{EscapeLua(itemName)}' for b=0,4 do for s=1,GetContainerNumSlots(b) do " +
                  "local link=GetContainerItemLink(b,s) if link and link:find(n) then UseContainerItem(b,s) return end end end";
        await context.ExecuteLuaAsync(lua, ct).ConfigureAwait(false);
    }

    public async Task SellGreyItemsAsync(IBotContext context, CancellationToken ct)
    {
        var lua = "for b=0,4 do for s=1,GetContainerNumSlots(b) do " +
                  "local link=GetContainerItemLink(b,s) if link then " +
                  "local _,_,q=GetItemInfo(link) if q==0 then UseContainerItem(b,s) end end end end";
        await context.ExecuteLuaAsync(lua, ct).ConfigureAwait(false);
    }

    public async Task RepairAllItemsAsync(IBotContext context, CancellationToken ct)
    {
        await context.ExecuteLuaAsync("RepairAllItems()", ct).ConfigureAwait(false);
    }

    private static string EscapeLua(string text)
    {
        return text.Replace("\\", "\\\\").Replace("'", "\\'");
    }
}
