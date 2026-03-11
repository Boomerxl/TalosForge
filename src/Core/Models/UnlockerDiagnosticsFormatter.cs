namespace TalosForge.Core.Models;

/// <summary>
/// Shapes unlocker health metrics into UI-friendly diagnostics values.
/// </summary>
public static class UnlockerDiagnosticsFormatter
{
    public static UnlockerDiagnosticsView Build(UnlockerHealthSnapshot health, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(health);

        return new UnlockerDiagnosticsView(
            health.ClientMetrics.ConsecutiveTimeouts,
            health.ClientMetrics.Timeouts,
            health.ClientMetrics.BackoffWaits,
            health.ClientMetrics.LastBackoffMs,
            FormatHeartbeatAge(health.HostHeartbeatUtc, nowUtc));
    }

    internal static string FormatHeartbeatAge(DateTimeOffset? heartbeatUtc, DateTimeOffset nowUtc)
    {
        if (heartbeatUtc is null)
        {
            return "n/a";
        }

        var age = nowUtc - heartbeatUtc.Value;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 1)
        {
            return "<1s";
        }

        if (age.TotalMinutes < 1)
        {
            return $"{(int)age.TotalSeconds}s";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m {(age.Seconds)}s";
        }

        return $"{(int)age.TotalHours}h {age.Minutes}m";
    }
}

public sealed record UnlockerDiagnosticsView(
    long ConsecutiveTimeouts,
    long TotalTimeouts,
    long BackoffWaits,
    int LastBackoffMs,
    string HeartbeatAge);
