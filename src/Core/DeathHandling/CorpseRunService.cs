using Microsoft.Extensions.Logging;
using TalosForge.Core.Models;
using TalosForge.Core.Navigation;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.DeathHandling;

/// <summary>
/// Handles death recovery: detects death, releases spirit, navigates back to corpse,
/// and resurrects. Falls back to spirit healer if corpse run fails repeatedly.
/// </summary>
public sealed class CorpseRunService
{
    private readonly NavigationEngine _navigation;
    private readonly ILogger<CorpseRunService>? _logger;

    private DeathState _state = DeathState.Alive;
    private int _deathCount;
    private int _failedCorpseRuns;
    private DateTimeOffset _deathTimeUtc;
    private Vector3 _deathPosition;

    public CorpseRunService(NavigationEngine navigation, ILogger<CorpseRunService>? logger = null)
    {
        _navigation = navigation;
        _logger = logger;
    }

    public DeathState State => _state;
    public int DeathCount => _deathCount;

    public async Task<bool> TickAsync(IBotContext ctx, CancellationToken ct)
    {
        if (ctx.Me == null) return false;

        switch (_state)
        {
            case DeathState.Alive:
                if (ctx.Me.Health == 0)
                {
                    _state = DeathState.Dead;
                    _deathCount++;
                    _deathTimeUtc = DateTimeOffset.UtcNow;
                    _deathPosition = ctx.Me.Position;
                    _logger?.LogWarning("Player died at {Position}, death #{Count}", _deathPosition, _deathCount);
                }
                return false;

            case DeathState.Dead:
                await ctx.ExecuteLuaAsync("RepopMe()", ct).ConfigureAwait(false);
                _state = DeathState.Ghost;
                _logger?.LogInformation("Released spirit, starting corpse run.");
                return true;

            case DeathState.Ghost:
                if (ctx.Me.Health > 0)
                {
                    _state = DeathState.Alive;
                    _failedCorpseRuns = 0;
                    _logger?.LogInformation("Resurrected successfully.");
                    return false;
                }

                var distToCorpse = Distance(ctx.Me.Position, _deathPosition);

                if (distToCorpse <= 30f)
                {
                    await ctx.ExecuteLuaAsync("RetrieveCorpse()", ct).ConfigureAwait(false);
                    _state = DeathState.Resurrecting;
                    return true;
                }

                if (_failedCorpseRuns >= 3)
                {
                    _logger?.LogWarning("Too many failed corpse runs, using spirit healer.");
                    await ctx.ExecuteLuaAsync(
                        "local f=StaticPopup_Visible('DEATH') if f then StaticPopup1Button1:Click() end",
                        ct).ConfigureAwait(false);
                    _state = DeathState.Alive;
                    _failedCorpseRuns = 0;
                    return true;
                }

                if (_navigation.Status != NavigationStatus.Moving)
                {
                    _navigation.StartNavigation(
                        new NavigationRequest(_deathPosition, 25f, 8f),
                        ctx.Me.Position);
                }

                var nav = _navigation.Update(ctx.Me.Position);
                if (nav.Status == NavigationStatus.Stuck)
                {
                    _failedCorpseRuns++;
                    _navigation.TryUnstick(ctx.Me.Position);
                }
                else if (nav.CurrentWaypoint.HasValue)
                {
                    await ctx.MoveToAsync(nav.CurrentWaypoint.Value, ct).ConfigureAwait(false);
                }

                return true;

            case DeathState.Resurrecting:
                if (ctx.Me.Health > 0)
                {
                    _state = DeathState.Alive;
                    _failedCorpseRuns = 0;
                    _logger?.LogInformation("Resurrection complete.");
                }
                else if ((DateTimeOffset.UtcNow - _deathTimeUtc).TotalSeconds > 120)
                {
                    _failedCorpseRuns++;
                    _state = DeathState.Ghost;
                }
                return true;
        }

        return false;
    }

    private static float Distance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

public enum DeathState
{
    Alive,
    Dead,
    Ghost,
    Resurrecting,
}
