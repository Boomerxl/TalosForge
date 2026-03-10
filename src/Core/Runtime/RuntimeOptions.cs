namespace TalosForge.Core.Runtime;

public sealed class RuntimeOptions
{
    public bool SmokeMode { get; set; }
    public int SmokeDurationSeconds { get; set; } = 2;
    public string? PluginDirectoryOverride { get; set; }
}
