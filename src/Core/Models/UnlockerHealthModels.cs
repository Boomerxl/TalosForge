namespace TalosForge.Core.Models;

public enum UnlockerConnectionState
{
    Unknown = 0,
    Connected = 1,
    Degraded = 2,
    Disconnected = 3,
}

/// <summary>
/// Runtime transport telemetry collected by the shared-memory unlocker client.
/// </summary>
public sealed record UnlockerClientMetrics(
    long Sends,
    long Acks,
    long Timeouts,
    long ConsecutiveTimeouts,
    long BackoffWaits,
    int LastBackoffMs,
    DateTimeOffset? LastSendUtc,
    DateTimeOffset? LastAckUtc,
    DateTimeOffset? LastTimeoutUtc,
    string? LastError);

/// <summary>
/// Host heartbeat payload persisted by UnlockerHost for external health checks.
/// </summary>
public sealed record UnlockerHostStatusFile(
    DateTimeOffset TimestampUtc,
    DateTimeOffset StartedUtc,
    int ProcessId,
    string ExecutorMode,
    long CommandsRead,
    long AcksWritten,
    long AcksDropped,
    long DecodeFailures,
    long ExecutorFailures,
    bool Running);

/// <summary>
/// Combined unlocker health view used by runtime/UI.
/// </summary>
public sealed record UnlockerHealthSnapshot(
    UnlockerConnectionState State,
    string Summary,
    UnlockerClientMetrics ClientMetrics,
    DateTimeOffset? HostHeartbeatUtc,
    bool HostHeartbeatFresh);
