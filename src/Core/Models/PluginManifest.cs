namespace TalosForge.Core.Models;

public sealed record PluginManifest(
    string Name,
    string Assembly,
    string Type,
    string MinimumCoreVersion);
