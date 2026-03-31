using System.Text.Json;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;

namespace TalosForge.Core.Plugins;

/// <summary>
/// Default implementation of IBotContext, wrapping a WorldSnapshot and IUnlockerClient.
/// Rebuilt each tick by PluginHost.
/// </summary>
public sealed class BotContext : IBotContext
{
    private readonly IUnlockerClient _client;

    public BotContext(WorldSnapshot snapshot, IUnlockerClient client)
    {
        Snapshot = snapshot;
        _client = client;
    }

    public PlayerSnapshot? Me => Snapshot.Player;
    public WorldSnapshot Snapshot { get; }

    public WowObjectSnapshot? Target
    {
        get
        {
            var guid = Me?.TargetGuid;
            if (guid == null || guid == 0) return null;
            return Snapshot.Objects.FirstOrDefault(o => o.Guid == guid);
        }
    }

    public IReadOnlyList<WowObjectSnapshot> NearbyUnits(float range)
    {
        if (Me == null) return Array.Empty<WowObjectSnapshot>();
        return Snapshot.Objects
            .Where(o => (o.Type is 3 or 4) && !o.IsLocalPlayer && DistanceTo(o) <= range)
            .ToList();
    }

    public IReadOnlyList<WowObjectSnapshot> NearbyEnemies(float range)
    {
        if (Me == null) return Array.Empty<WowObjectSnapshot>();
        return Snapshot.Objects
            .Where(o => o.Type == 3 && !o.IsDead && !o.IsLocalPlayer && DistanceTo(o) <= range)
            .ToList();
    }

    public IReadOnlyList<WowObjectSnapshot> NearbyFriendlies(float range)
    {
        if (Me == null) return Array.Empty<WowObjectSnapshot>();
        return Snapshot.Objects
            .Where(o => o.Type == 4 && !o.IsDead && !o.IsLocalPlayer && DistanceTo(o) <= range)
            .ToList();
    }

    public bool HasAura(int spellId)
    {
        return Me?.Auras?.Any(a => a.SpellId == spellId) == true;
    }

    public bool HasAura(string spellName)
    {
        return Me?.Auras != null && Me.Auras.Count > 0;
    }

    public bool TargetHasAura(int spellId)
    {
        var target = Target;
        return target?.Auras?.Any(a => a.SpellId == spellId) == true;
    }

    public float DistanceTo(WowObjectSnapshot unit)
    {
        if (Me == null) return float.MaxValue;
        return DistanceTo(unit.Position);
    }

    public float DistanceTo(Vector3 position)
    {
        if (Me == null) return float.MaxValue;
        var dx = position.X - Me.Position.X;
        var dy = position.Y - Me.Position.Y;
        var dz = position.Z - Me.Position.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public Task CastSpellAsync(string spellName, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { spell = spellName });
        return SendCommandAsync(UnlockerOpcode.CastSpellByName, payload, ct);
    }

    public Task CastSpellByIdAsync(int spellId, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { spellId });
        return SendCommandAsync(UnlockerOpcode.CastSpellByID, payload, ct);
    }

    public Task TargetUnitAsync(ulong guid, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { guid = guid.ToString() });
        return SendCommandAsync(UnlockerOpcode.SetTargetGuid, payload, ct);
    }

    public Task MoveToAsync(Vector3 position, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            x = position.X,
            y = position.Y,
            z = position.Z,
            overshootThreshold = 0.35f
        });
        return SendCommandAsync(UnlockerOpcode.MoveTo, payload, ct);
    }

    public Task FaceAsync(float radians, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { facing = radians, smoothing = 0.15f });
        return SendCommandAsync(UnlockerOpcode.Face, payload, ct);
    }

    public Task InteractAsync(ulong guid, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { guid = guid.ToString() });
        return SendCommandAsync(UnlockerOpcode.Interact, payload, ct);
    }

    public Task InteractAsync(CancellationToken ct)
    {
        return SendCommandAsync(UnlockerOpcode.Interact, "{}", ct);
    }

    public Task StopMovingAsync(CancellationToken ct)
    {
        return SendCommandAsync(UnlockerOpcode.Stop, "{}", ct);
    }

    public Task ExecuteLuaAsync(string code, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { code });
        return SendCommandAsync(UnlockerOpcode.LuaDoString, payload, ct);
    }

    public async Task<string?> QueryLuaAsync(string code, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { code });
        var command = new UnlockerCommand(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UnlockerOpcode.LuaQuery,
            payload,
            DateTimeOffset.UtcNow);
        var ack = await _client.SendAsync(command, ct).ConfigureAwait(false);
        return ack.Success ? ack.Message : null;
    }

    private async Task SendCommandAsync(UnlockerOpcode opcode, string payload, CancellationToken ct)
    {
        var command = new UnlockerCommand(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            opcode,
            payload,
            DateTimeOffset.UtcNow);
        await _client.SendAsync(command, ct).ConfigureAwait(false);
    }
}
