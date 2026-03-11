using System.Diagnostics;
using TalosForge.UnlockerAgentHost.Models;

namespace TalosForge.UnlockerAgentHost.Runtime;

public sealed class AgentSessionManager
{
    private readonly AgentHostOptions _options;
    private readonly IAgentRuntime _runtime;
    private readonly object _sync = new();
    private string _state = "booting";

    public AgentSessionManager(AgentHostOptions options, IAgentRuntime runtime)
    {
        _options = options;
        _runtime = runtime;
    }

    public string State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public async ValueTask<SessionReadyResult> EnsureReadyAsync(string? requestedEvasionProfile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var processes = Process.GetProcessesByName(_options.WowProcessName);
        if (processes.Length == 0)
        {
            SetState("waiting_for_wow");
            return new SessionReadyResult(
                false,
                "WoW process not found.",
                AgentResultCodes.NotInGame);
        }

        var effectiveProfile = ResolveEvasionProfile(requestedEvasionProfile);
        var ready = await _runtime
            .EnsureReadyAsync(effectiveProfile, cancellationToken)
            .ConfigureAwait(false);

        if (!ready.Success)
        {
            var code = string.IsNullOrWhiteSpace(ready.Code)
                ? AgentResultCodes.HookNotReady
                : ready.Code;
            SetState("initialization_fault");
            return new SessionReadyResult(false, ready.Message, code);
        }

        SetState("ready");
        return new SessionReadyResult(true, ready.Message, AgentResultCodes.Ok);
    }

    private string ResolveEvasionProfile(string? requestedEvasionProfile)
    {
        if (_options.DisableEvasion)
        {
            return "off";
        }

        if (!string.IsNullOrWhiteSpace(requestedEvasionProfile))
        {
            return requestedEvasionProfile.Trim().ToLowerInvariant();
        }

        return _options.EvasionProfile.Trim().ToLowerInvariant();
    }

    private void SetState(string state)
    {
        lock (_sync)
        {
            _state = state;
        }
    }
}

public sealed record SessionReadyResult(
    bool Success,
    string Message,
    string Code);
