using System.Text;
using TalosForge.Core.Models;

using TalosForge.Core;

namespace TalosForge.Core.Drawing;

/// <summary>Builds Lua <c>S(...)</c> line bodies for the hub overlay (host → game).</summary>
internal static class HubOverlayViewBuilder
{
    public static string BuildDebugSection(
        WorldSnapshot snapshot,
        BotTickMetrics? metrics,
        BotState state,
        long tickId,
        int queuedCommands)
    {
        var sb = new StringBuilder();
        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw("|cff66ccff--- Summary ---|r"));
        var status = snapshot.Success ? "ok" : "err";
        sb.AppendLine(TalosForgeFrameLua.FormatLine("State", state.ToString()));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Tick", tickId.ToString()));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Snapshot", status));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Objects", snapshot.Objects.Count.ToString()));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Cmd queue", queuedCommands.ToString()));
        if (!string.IsNullOrEmpty(snapshot.ErrorMessage))
            sb.AppendLine(TalosForgeFrameLua.FormatLine("Error", snapshot.ErrorMessage));

        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw(""));
        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw("|cff66ccff--- Player ---|r"));
        sb.Append(BuildPlayerInfo(snapshot));

        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw(""));
        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw("|cff66ccff--- Target ---|r"));
        sb.Append(BuildTargetInfo(snapshot));

        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw(""));
        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw("|cff66ccff--- Nearby ---|r"));
        sb.Append(BuildNearbyInfo(snapshot));

        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw(""));
        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw("|cff66ccff--- Bot ---|r"));
        sb.Append(BuildBotInfo(metrics));

        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw(""));
        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw("|cff66ccff--- Auras ---|r"));
        sb.Append(BuildAuraInfo(snapshot));

        return sb.ToString();
    }

    private static string BuildPlayerInfo(WorldSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var p = snapshot.Player;
        if (p == null)
        {
            sb.AppendLine(TalosForgeFrameLua.FormatLine("Status", "No player data"));
            return sb.ToString();
        }

        var hpPct = p.MaxHealth > 0 ? (int)(100.0 * p.Health.GetValueOrDefault() / p.MaxHealth!.Value) : 0;
        var mpPct = p.MaxMana > 0 ? (int)(100.0 * p.Mana.GetValueOrDefault() / p.MaxMana!.Value) : 0;

        sb.AppendLine(TalosForgeFrameLua.FormatLine("HP", $"{p.Health ?? 0}/{p.MaxHealth ?? 0} ({hpPct}%)"));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Mana", $"{p.Mana ?? 0}/{p.MaxMana ?? 0} ({mpPct}%)"));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Level", (p.Level ?? 0).ToString()));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Pos",
            $"{p.Position.X:F0}, {p.Position.Y:F0}, {p.Position.Z:F0}"));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Facing", $"{p.Facing:F2} rad"));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Combat", p.InCombat ? "|cffff4444Yes|r" : "No"));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Casting", p.IsCasting ? "|cffffcc00Yes|r" : "No"));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Target",
            p.TargetGuid.HasValue && p.TargetGuid != 0
                ? $"0x{p.TargetGuid.Value:X}"
                : "None"));

        return sb.ToString();
    }

    private static string BuildTargetInfo(WorldSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var p = snapshot.Player;
        if (p?.TargetGuid == null || p.TargetGuid == 0)
        {
            sb.AppendLine(TalosForgeFrameLua.FormatLine("Status", "No target"));
            return sb.ToString();
        }

        var target = snapshot.Objects.FirstOrDefault(o => o.Guid == p.TargetGuid);
        if (target == null)
        {
            sb.AppendLine(TalosForgeFrameLua.FormatLine("Status", "Target not in range"));
            return sb.ToString();
        }

        var name = target.Name ?? $"ID:{target.EntryId ?? 0}";
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Name", name));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("HP",
            $"{target.Health ?? 0}/{target.MaxHealth ?? 0}"));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Level", (target.Level ?? 0).ToString()));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Dead", target.IsDead ? "|cffff4444Yes|r" : "No"));

        var dist = MathF.Sqrt(
            MathF.Pow(target.Position.X - p.Position.X, 2) +
            MathF.Pow(target.Position.Y - p.Position.Y, 2) +
            MathF.Pow(target.Position.Z - p.Position.Z, 2));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Distance", $"{dist:F1} yd"));

        return sb.ToString();
    }

    private static string BuildNearbyInfo(WorldSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var units = snapshot.Objects
            .Where(o => o.Type is 3 or 4 && !o.IsDead && !o.IsLocalPlayer)
            .ToList();

        sb.AppendLine(TalosForgeFrameLua.FormatLine("Units", units.Count.ToString()));

        var inCombat = units.Count(u => ((u.UnitFlags ?? 0) & Offsets.UNIT_FLAG_IN_COMBAT) != 0);
        sb.AppendLine(TalosForgeFrameLua.FormatLine("In combat flag", inCombat.ToString()));

        return sb.ToString();
    }

    private static string BuildBotInfo(BotTickMetrics? metrics)
    {
        var sb = new StringBuilder();
        if (metrics == null)
        {
            sb.AppendLine(TalosForgeFrameLua.FormatLine("Status", "No metrics (first tick)"));
            return sb.ToString();
        }

        sb.AppendLine(TalosForgeFrameLua.FormatLine("Tick", $"#{metrics.TickId}"));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Tick ms", $"{metrics.TickMs}ms"));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Snapshot ms", $"{metrics.SnapshotMs}ms"));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Events", metrics.EventsCount.ToString()));
        sb.AppendLine(TalosForgeFrameLua.FormatLine("Commands", metrics.CommandsCount.ToString()));

        return sb.ToString();
    }

    private static string BuildAuraInfo(WorldSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var auras = snapshot.Player?.Auras;
        if (auras == null || auras.Count == 0)
        {
            sb.AppendLine(TalosForgeFrameLua.FormatLine("Buffs", "None"));
            return sb.ToString();
        }

        sb.AppendLine(TalosForgeFrameLua.FormatLine("Count", auras.Count.ToString()));
        var shown = 0;
        foreach (var aura in auras.Take(5))
        {
            var stacks = aura.Stacks > 1 ? $" x{aura.Stacks}" : "";
            sb.AppendLine(TalosForgeFrameLua.FormatLine($"  #{++shown}", $"Spell {aura.SpellId}{stacks}"));
        }

        if (auras.Count > 5)
            sb.AppendLine(TalosForgeFrameLua.FormatLine("  ...", $"+{auras.Count - 5} more"));

        return sb.ToString();
    }

    public static string BuildPluginsSection(IReadOnlyList<string> names)
    {
        var sb = new StringBuilder();
        sb.AppendLine(TalosForgeFrameLua.FormatLineRaw("|cff66ccffLoaded plugins|r"));
        if (names.Count == 0)
        {
            sb.AppendLine(TalosForgeFrameLua.FormatLine("Status", "None"));
            return sb.ToString();
        }

        for (var i = 0; i < names.Count; i++)
            sb.AppendLine(TalosForgeFrameLua.FormatLine($"#{i + 1}", names[i]));

        return sb.ToString();
    }

}
