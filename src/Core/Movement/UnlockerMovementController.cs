using System.Text.Json;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;

namespace TalosForge.Core.Movement;

/// <summary>
/// Movement primitives translated into unlocker commands.
/// </summary>
public sealed class UnlockerMovementController : IMovementController
{
    private readonly IUnlockerClient _unlockerClient;
    private readonly MovementPolicy _policy;
    private readonly Random _random;

    public UnlockerMovementController(
        IUnlockerClient unlockerClient,
        MovementPolicy? policy = null,
        Random? random = null)
    {
        _unlockerClient = unlockerClient;
        _policy = policy ?? new MovementPolicy();
        _random = random ?? new Random();
    }

    public async Task FaceToAsync(float facingRadians, CancellationToken cancellationToken)
    {
        await DelayHumanized(cancellationToken).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(new
        {
            facing = facingRadians,
            smoothing = _policy.TurnSmoothingFactor,
        });

        await _unlockerClient.SendAsync(
            new UnlockerCommand(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UnlockerOpcode.Face,
                payload,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveToAsync(Vector3 destination, CancellationToken cancellationToken)
    {
        await DelayHumanized(cancellationToken).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(new
        {
            x = destination.X,
            y = destination.Y,
            z = destination.Z,
            overshootThreshold = _policy.OvershootCorrectionThreshold,
        });

        await _unlockerClient.SendAsync(
            new UnlockerCommand(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UnlockerOpcode.MoveTo,
                payload,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DelayHumanized(cancellationToken).ConfigureAwait(false);

        await _unlockerClient.SendAsync(
            new UnlockerCommand(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UnlockerOpcode.Interact,
                "{\"stop\":true}",
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DelayHumanized(CancellationToken cancellationToken)
    {
        var min = Math.Min(_policy.RandomizedDelayMinMs, _policy.RandomizedDelayMaxMs);
        var max = Math.Max(_policy.RandomizedDelayMinMs, _policy.RandomizedDelayMaxMs);
        var delay = _random.Next(min, max + 1);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
