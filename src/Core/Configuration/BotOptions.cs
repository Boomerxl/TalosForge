namespace TalosForge.Core.Configuration;

public sealed class BotOptions
{
    public string ProcessName { get; set; } = "Wow";
    public int CombatTickMs { get; set; } = 35;
    public int MovementTickMs { get; set; } = 70;
    public int IdleTickMs { get; set; } = 120;
    public int MinTickMs { get; set; } = 25;
    public int MaxTickMs { get; set; } = 150;
    public int WatchdogTimeoutMs { get; set; } = 2_000;
    public int ShortCacheTtlMs { get; set; } = 100;
    public int LongCacheTtlMs { get; set; } = 15_000;
    public string CommandMmfName { get; set; } = "TalosForge.Cmd.v1";
    public string EventMmfName { get; set; } = "TalosForge.Evt.v1";
    public int RingCapacityBytes { get; set; } = 1_048_576;
    public int UnlockerTimeoutMs { get; set; } = 250;
    public int UnlockerRetryCount { get; set; } = 2;
    public int SnapshotTelemetryEveryTicks { get; set; } = 10;
    public bool EnableSnapshotTelemetry { get; set; } = true;
}
