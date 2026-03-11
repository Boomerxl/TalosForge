namespace TalosForge.UnlockerAgentHost.Runtime;

public sealed class AgentHostOptions
{
    public string PipeName { get; set; } = "TalosForge.Agent.v1";
    public string WowProcessName { get; set; } = "Wow";
    public string RuntimeMode { get; set; } = "auto";
    public int RequestTimeoutMs { get; set; } = 2_500;
    public int RetryCount { get; set; } = 2;
    public int BackoffBaseMs { get; set; } = 100;
    public int BackoffMaxMs { get; set; } = 1_000;
    public string NativePipePrefix { get; set; } = "TalosForge.Agent.Native";
    public string? NativeDllPath { get; set; }
    public int NativeConnectTimeoutMs { get; set; } = 2_000;
    public bool SmokeMode { get; set; }
    public int SmokeDurationSeconds { get; set; } = 10;
    public bool DisableEvasion { get; set; }
    public bool SimulateEvasionInitFailure { get; set; }
#if DEBUG
    public string EvasionProfile { get; set; } = "off";
#else
    public string EvasionProfile { get; set; } = "full";
#endif
}
