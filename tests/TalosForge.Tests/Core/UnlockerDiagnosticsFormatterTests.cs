using TalosForge.Core.Models;
using Xunit;

namespace TalosForge.Tests.Core;

public sealed class UnlockerDiagnosticsFormatterTests
{
    [Fact]
    public void Build_Maps_Metrics_And_Null_Heartbeat()
    {
        var health = new UnlockerHealthSnapshot(
            UnlockerConnectionState.Degraded,
            "test",
            new UnlockerClientMetrics(
                Sends: 10,
                Acks: 9,
                Timeouts: 3,
                ConsecutiveTimeouts: 2,
                BackoffWaits: 4,
                LastBackoffMs: 500,
                LastSendUtc: null,
                LastAckUtc: null,
                LastTimeoutUtc: null,
                LastError: null),
            HostHeartbeatUtc: null,
            HostHeartbeatFresh: false);

        var diagnostics = UnlockerDiagnosticsFormatter.Build(
            health,
            new DateTimeOffset(2026, 3, 10, 20, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, diagnostics.ConsecutiveTimeouts);
        Assert.Equal(3, diagnostics.TotalTimeouts);
        Assert.Equal(4, diagnostics.BackoffWaits);
        Assert.Equal(500, diagnostics.LastBackoffMs);
        Assert.Equal("n/a", diagnostics.HeartbeatAge);
    }

    [Fact]
    public void Build_Formats_Heartbeat_Age()
    {
        var now = new DateTimeOffset(2026, 3, 10, 20, 0, 0, TimeSpan.Zero);
        var health = new UnlockerHealthSnapshot(
            UnlockerConnectionState.Connected,
            "test",
            new UnlockerClientMetrics(0, 0, 0, 0, 0, 0, null, null, null, null),
            HostHeartbeatUtc: now.AddMinutes(-2).AddSeconds(-5),
            HostHeartbeatFresh: true);

        var diagnostics = UnlockerDiagnosticsFormatter.Build(health, now);

        Assert.Equal("2m 5s", diagnostics.HeartbeatAge);
    }
}
