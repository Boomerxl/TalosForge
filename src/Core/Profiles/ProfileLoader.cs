using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TalosForge.Core.Profiles;

/// <summary>
/// Loads and validates GrindProfile JSON files.
/// </summary>
public sealed class ProfileLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<ProfileLoader>? _logger;

    public ProfileLoader(ILogger<ProfileLoader>? logger = null)
    {
        _logger = logger;
    }

    public GrindProfile? LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _logger?.LogWarning("Profile file not found: {Path}", path);
                return null;
            }

            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<GrindProfile>(json, JsonOptions);

            if (profile == null)
            {
                _logger?.LogWarning("Failed to deserialize profile: {Path}", path);
                return null;
            }

            var errors = Validate(profile);
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                    _logger?.LogWarning("Profile validation error: {Error}", error);
                return null;
            }

            _logger?.LogInformation("Loaded profile '{Name}' with {HotspotCount} hotspots from {Path}",
                profile.Name, profile.Hotspots.Count, path);
            return profile;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error loading profile from {Path}", path);
            return null;
        }
    }

    public static List<string> Validate(GrindProfile profile)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Name))
            errors.Add("Profile name is required.");

        if (profile.Hotspots.Count == 0)
            errors.Add("At least one hotspot is required.");

        foreach (var hs in profile.Hotspots)
        {
            if (hs.Radius <= 0)
                errors.Add($"Hotspot '{hs.Name}' has invalid radius: {hs.Radius}");
        }

        if (profile.Rest.HpPercentToEat < 0 || profile.Rest.HpPercentToEat > 100)
            errors.Add("Rest HP eat threshold must be 0-100.");

        if (profile.Rest.ManaPercentToDrink < 0 || profile.Rest.ManaPercentToDrink > 100)
            errors.Add("Rest Mana drink threshold must be 0-100.");

        if (profile.TargetFilter.MaxPullDistance <= 0)
            errors.Add("Target filter pull distance must be positive.");

        return errors;
    }
}
