using TalosForge.Core.Models;

namespace TalosForge.Core.Profiles;

/// <summary>
/// Defines a grinding profile with hotspots, target filters, and rest settings.
/// Loadable from JSON files.
/// </summary>
public sealed class GrindProfile
{
    public string Name { get; set; } = "Unnamed";
    public string Description { get; set; } = "";
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; } = 80;

    public List<Hotspot> Hotspots { get; set; } = new();
    public TargetFilter TargetFilter { get; set; } = new();
    public RestSettings Rest { get; set; } = new();
    public VendorSettings? Vendor { get; set; }
    public LootSettings Loot { get; set; } = new();
}

public sealed class Hotspot
{
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; } = 40f;
    public int Priority { get; set; }

    public Vector3 Position => new(X, Y, Z);
}

public sealed class TargetFilter
{
    public int MinLevel { get; set; } = 1;
    public int MaxLevel { get; set; } = 80;
    public float MaxPullDistance { get; set; } = 30f;
    public float MaxChaseDistance { get; set; } = 60f;
    public List<uint> BlacklistedEntryIds { get; set; } = new();
    public List<uint> WhitelistedEntryIds { get; set; } = new();
    public bool IgnoreElites { get; set; } = true;
    public bool IgnoreTapped { get; set; } = true;
}

public sealed class RestSettings
{
    public int HpPercentToEat { get; set; } = 50;
    public int ManaPercentToDrink { get; set; } = 40;
    public int HpPercentToResume { get; set; } = 85;
    public int ManaPercentToResume { get; set; } = 80;
    public string? FoodItemName { get; set; }
    public string? DrinkItemName { get; set; }
}

public sealed class VendorSettings
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public ulong NpcGuid { get; set; }
    public uint NpcEntryId { get; set; }
    public int BagSlotsToTrigger { get; set; } = 3;
    public bool SellGrey { get; set; } = true;
    public bool Repair { get; set; } = true;

    public Vector3 Position => new(X, Y, Z);
}

public sealed class LootSettings
{
    public bool AutoLoot { get; set; } = true;
    public bool SkinAfterLoot { get; set; }
    public float LootRange { get; set; } = 5f;
}
