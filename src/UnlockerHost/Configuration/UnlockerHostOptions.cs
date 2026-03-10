namespace TalosForge.UnlockerHost.Configuration;

/// <summary>
/// Runtime configuration for the shared-memory unlocker host process.
/// </summary>
public sealed class UnlockerHostOptions
{
    public string CommandRingName { get; set; } = "TalosForge.Cmd.v1";
    public string EventRingName { get; set; } = "TalosForge.Evt.v1";
    public int RingCapacityBytes { get; set; } = 1_048_576;

    public int PollDelayMs { get; set; } = 2;
    public int AckWriteRetryCount { get; set; } = 20;
    public int AckWriteDelayMs { get; set; } = 2;
    public int StatsIntervalSeconds { get; set; } = 10;
    public string StatusFilePath { get; set; } =
        Path.Combine(Path.GetTempPath(), "TalosForge.UnlockerHost.status.json");
    public int StatusWriteIntervalMs { get; set; } = 1_000;

    public bool SmokeMode { get; set; }
    public int SmokeDurationSeconds { get; set; } = 5;

    public string ExecutorMode { get; set; } = "mock";
}
