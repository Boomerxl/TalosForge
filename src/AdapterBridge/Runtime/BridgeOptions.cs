namespace TalosForge.AdapterBridge.Runtime;

public sealed class BridgeOptions
{
    public string PipeName { get; set; } = "TalosForge.UnlockerAdapter.v1";
    public string Mode { get; set; } = "mock";
    public string? CommandPath { get; set; }
    public string CommandArgs { get; set; } = string.Empty;
    public int CommandTimeoutMs { get; set; } = 2_500;
    public string AgentPipeName { get; set; } = "TalosForge.Agent.v1";
    public int AgentConnectTimeoutMs { get; set; } = 1_200;
    public int AgentRequestTimeoutMs { get; set; } = 2_500;
    public string? AgentEvasionProfile { get; set; }
    public bool SmokeMode { get; set; }
    public int SmokeDurationSeconds { get; set; } = 10;
}
