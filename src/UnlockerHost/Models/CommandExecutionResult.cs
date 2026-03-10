namespace TalosForge.UnlockerHost.Models;

/// <summary>
/// Result returned by unlocker-host command executors.
/// </summary>
public sealed record CommandExecutionResult(
    bool Success,
    string Message,
    string? PayloadJson = null)
{
    public static CommandExecutionResult Ok(string message, string? payloadJson = null)
    {
        return new CommandExecutionResult(true, message, payloadJson);
    }

    public static CommandExecutionResult Fail(string message, string? payloadJson = null)
    {
        return new CommandExecutionResult(false, message, payloadJson);
    }
}
