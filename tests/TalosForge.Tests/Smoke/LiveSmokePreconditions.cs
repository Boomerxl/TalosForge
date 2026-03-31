using System.Diagnostics;

namespace TalosForge.Tests.Smoke;

internal static class LiveSmokePreconditions
{
    public const string TraitName = "Suite";
    public const string TraitValue = "LiveSmoke";

    public static string? GetWowSkipReason()
    {
        return Process.GetProcessesByName("Wow").FirstOrDefault() is null
            ? "Live smoke precondition unmet: Wow.exe is not running."
            : null;
    }

    public static string? GetInjectedSkipReason()
    {
        var wow = Process.GetProcessesByName("Wow").FirstOrDefault();
        if (wow is null)
        {
            return "Live smoke precondition unmet: Wow.exe is not running.";
        }

        var discoveryPath = Path.Combine(Path.GetTempPath(), $"TalosForge.pipe.{wow.Id}");
        if (!File.Exists(discoveryPath))
        {
            return $"Live smoke precondition unmet: discovery file missing at '{discoveryPath}'. Inject the agent first.";
        }

        var fullPipeName = File.ReadAllText(discoveryPath).Trim();
        if (string.IsNullOrWhiteSpace(fullPipeName))
        {
            return $"Live smoke precondition unmet: discovery file '{discoveryPath}' is empty.";
        }

        return null;
    }

    public static Process RequireWowProcess()
    {
        return Process.GetProcessesByName("Wow").FirstOrDefault()
            ?? throw new InvalidOperationException("Wow.exe was expected to be running after skip precheck.");
    }

    public static string RequireAgentPipeName(int wowPid)
    {
        var discoveryPath = Path.Combine(Path.GetTempPath(), $"TalosForge.pipe.{wowPid}");
        if (!File.Exists(discoveryPath))
        {
            throw new InvalidOperationException($"Expected discovery file: {discoveryPath}");
        }

        var fullPipeName = File.ReadAllText(discoveryPath).Trim();
        if (string.IsNullOrWhiteSpace(fullPipeName))
        {
            throw new InvalidOperationException($"Discovery file is empty: {discoveryPath}");
        }

        const string prefix = @"\\.\pipe\";
        return fullPipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullPipeName[prefix.Length..]
            : fullPipeName;
    }
}
