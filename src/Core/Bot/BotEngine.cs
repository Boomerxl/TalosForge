using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Configuration;
using TalosForge.Core.Models;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.Bot;

/// <summary>
/// Main bot loop with adaptive tick scheduling.
/// </summary>
public sealed class BotEngine : IBotEngine
{
    private readonly IObjectManager _objectManager;
    private readonly IEventBus _eventBus;
    private readonly IUnlockerClient _unlockerClient;
    private readonly PluginHost? _pluginHost;
    private readonly ILogger<BotEngine> _logger;
    private readonly BotOptions _options;

    private long _tickId;

    public BotEngine(
        IObjectManager objectManager,
        IEventBus eventBus,
        IUnlockerClient unlockerClient,
        BotOptions options,
        ILogger<BotEngine> logger,
        PluginHost? pluginHost = null)
    {
        _objectManager = objectManager;
        _eventBus = eventBus;
        _unlockerClient = unlockerClient;
        _options = options;
        _logger = logger;
        _pluginHost = pluginHost;
    }

    public BotTickMetrics? LastMetrics { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _tickId++;
            var tickWatch = Stopwatch.StartNew();

            var snapshotWatch = Stopwatch.StartNew();
            var snapshot = _objectManager.GetSnapshot(_tickId);
            snapshotWatch.Stop();

            var events = _eventBus.ProcessSnapshot(snapshot);
            var state = DetermineState(snapshot, events);
            var commandsCount = 0;

            if (_pluginHost != null)
            {
                commandsCount = await _pluginHost
                    .TickAsync(snapshot, events, _unlockerClient, cancellationToken)
                    .ConfigureAwait(false);
            }

            tickWatch.Stop();
            var tickMs = (int)tickWatch.ElapsedMilliseconds;
            var snapshotMs = (int)snapshotWatch.ElapsedMilliseconds;

            LastMetrics = new BotTickMetrics(
                _tickId,
                tickMs,
                snapshotMs,
                events.Count,
                commandsCount,
                DateTimeOffset.UtcNow);

            _logger.LogInformation(
                "tick={TickId} state={State} tick_ms={TickMs} snapshot_ms={SnapshotMs} events_count={EventsCount} commands_count={CommandsCount}",
                _tickId,
                state,
                tickMs,
                snapshotMs,
                events.Count,
                commandsCount);

            if (ShouldEmitSnapshotTelemetry(snapshot))
            {
                _logger.LogInformation(
                    "snapshot tick={TickId} success={Success} objects={ObjectCount} player_guid={PlayerGuid} target_guid={TargetGuid} error={Error}",
                    _tickId,
                    snapshot.Success,
                    snapshot.Objects.Count,
                    FormatGuid(snapshot.Player?.Guid),
                    FormatGuid(snapshot.Player?.TargetGuid),
                    snapshot.ErrorMessage ?? "none");
            }

            if (tickMs > _options.WatchdogTimeoutMs)
            {
                _logger.LogWarning(
                    "Tick {TickId} exceeded watchdog timeout. tick_ms={TickMs} watchdog_ms={WatchdogMs}",
                    _tickId,
                    tickMs,
                    _options.WatchdogTimeoutMs);
            }

            var intervalMs = ComputeTickIntervalMs(state, events);
            var delay = intervalMs - tickMs;
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public int ComputeTickIntervalMs(BotState state, IReadOnlyList<BotEvent> lastEvents)
    {
        var baseInterval = state switch
        {
            BotState.Combat => _options.CombatTickMs,
            BotState.Movement => _options.MovementTickMs,
            _ => _options.IdleTickMs,
        };

        // Adaptive trim when event pressure is high.
        if (lastEvents.Count >= 4)
        {
            baseInterval -= 5;
        }

        return Math.Clamp(baseInterval, _options.MinTickMs, _options.MaxTickMs);
    }

    private bool ShouldEmitSnapshotTelemetry(WorldSnapshot snapshot)
    {
        if (!_options.EnableSnapshotTelemetry)
        {
            return false;
        }

        if (!snapshot.Success)
        {
            return true;
        }

        if (_options.SnapshotTelemetryEveryTicks <= 0)
        {
            return false;
        }

        return _tickId % _options.SnapshotTelemetryEveryTicks == 0;
    }

    private static string FormatGuid(ulong? guid)
    {
        return guid.HasValue && guid.Value != 0 ? $"0x{guid.Value:X16}" : "none";
    }

    private static BotState DetermineState(WorldSnapshot snapshot, IReadOnlyList<BotEvent> events)
    {
        if (snapshot.Player?.InCombat == true || events.OfType<CombatStartedEvent>().Any())
        {
            return BotState.Combat;
        }

        if (snapshot.Player?.IsMoving == true)
        {
            return BotState.Movement;
        }

        return BotState.Idle;
    }
}
