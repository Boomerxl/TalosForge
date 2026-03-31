using TalosForge.Core.Plugins;

namespace TalosForge.Core.Humanization;

/// <summary>
/// Adds human-like randomness to bot actions: timing jitter, occasional pauses,
/// mouse movement simulation, and anti-AFK behavior.
/// </summary>
public sealed class HumanBehaviorService
{
    private readonly Random _rng;
    private readonly HumanizationSettings _settings;
    private DateTimeOffset _lastAfkPreventionUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastPauseUtc = DateTimeOffset.UtcNow;
    private int _actionsSinceLastPause;

    public HumanBehaviorService(HumanizationSettings? settings = null, int? seed = null)
    {
        _settings = settings ?? new HumanizationSettings();
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public int RandomizeDelayMs(int baseMs)
    {
        var jitter = (int)(baseMs * _settings.TimingJitterFraction);
        return baseMs + _rng.Next(-jitter, jitter + 1);
    }

    public TimeSpan RandomizeDelay(TimeSpan baseDelay)
    {
        var ms = RandomizeDelayMs((int)baseDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(0, ms));
    }

    public bool ShouldTakeBreak()
    {
        _actionsSinceLastPause++;

        if (_actionsSinceLastPause < _settings.MinActionsBetweenPauses)
            return false;

        if (_rng.NextDouble() < _settings.PauseProbabilityPerTick)
        {
            _actionsSinceLastPause = 0;
            _lastPauseUtc = DateTimeOffset.UtcNow;
            return true;
        }

        return false;
    }

    public TimeSpan GetBreakDuration()
    {
        var min = _settings.MinPauseDurationMs;
        var max = _settings.MaxPauseDurationMs;
        return TimeSpan.FromMilliseconds(_rng.Next(min, max + 1));
    }

    public bool ShouldPreventAfk()
    {
        var elapsed = DateTimeOffset.UtcNow - _lastAfkPreventionUtc;
        if (elapsed.TotalSeconds < _settings.AfkPreventionIntervalSec)
            return false;

        _lastAfkPreventionUtc = DateTimeOffset.UtcNow;
        return true;
    }

    public async Task PreventAfkAsync(IBotContext context, CancellationToken ct)
    {
        var actions = new[]
        {
            "RunMacroText('/sit')",
            "RunMacroText('/stand')",
            "CameraZoomIn(1) CameraZoomOut(1)",
            "JumpOrAscendStart() JumpOrAscendStop()",
        };

        var lua = actions[_rng.Next(actions.Length)];
        await context.ExecuteLuaAsync(lua, ct).ConfigureAwait(false);
    }

    public float AddPositionJitter(float value)
    {
        var jitter = (float)(_rng.NextDouble() * 2 - 1) * _settings.PositionJitterYards;
        return value + jitter;
    }
}

public sealed class HumanizationSettings
{
    public float TimingJitterFraction { get; init; } = 0.15f;
    public double PauseProbabilityPerTick { get; init; } = 0.005;
    public int MinActionsBetweenPauses { get; init; } = 20;
    public int MinPauseDurationMs { get; init; } = 500;
    public int MaxPauseDurationMs { get; init; } = 3000;
    public int AfkPreventionIntervalSec { get; init; } = 240;
    public float PositionJitterYards { get; init; } = 1.5f;
}
