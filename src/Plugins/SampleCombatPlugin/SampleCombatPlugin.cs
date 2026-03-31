using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;
using TalosForge.Core.Plugins;

namespace SampleCombatPlugin;

/// <summary>
/// Sample mage combat rotation demonstrating the IBotContext API.
/// Casts Frostbolt as filler, Fire Blast on cooldown when in range,
/// and Frost Nova when enemies are within melee range.
/// </summary>
public sealed class SampleCombatPlugin : IPlugin
{
    private IPluginContext? _context;

    public string Name => "SampleCombatPlugin";
    public Version Version { get; } = new(2, 0, 0);
    public PluginCapabilities Capabilities => PluginCapabilities.Combat;

    public void Initialize(IPluginContext context)
    {
        _context = context;
    }

    public Task TickAsync(WorldSnapshot snapshot, IReadOnlyList<BotEvent> events, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task TickAsync(IBotContext ctx, IReadOnlyList<BotEvent> events, CancellationToken ct)
    {
        if (ctx.Me == null || ctx.Me.Health == 0)
            return;

        if (!ctx.Me.InCombat)
            return;

        var target = ctx.Target;
        if (target == null || target.IsDead)
            return;

        if (ctx.Me.IsCasting)
            return;

        var distance = ctx.DistanceTo(target);

        var meleeEnemies = ctx.NearbyEnemies(8f);
        if (meleeEnemies.Count >= 2)
        {
            await ctx.CastSpellAsync("Frost Nova", ct).ConfigureAwait(false);
            return;
        }

        if (distance <= 20f)
        {
            await ctx.CastSpellAsync("Fire Blast", ct).ConfigureAwait(false);
            return;
        }

        if (distance <= 30f)
        {
            await ctx.CastSpellAsync("Frostbolt", ct).ConfigureAwait(false);
            return;
        }
    }

    public void Dispose()
    {
        _context = null;
    }
}
