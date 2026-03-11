namespace TalosForge.UnlockerAgentHost.Models;

public static class AgentResultCodes
{
    public const string Ok = "OK";
    public const string InvalidRequest = "AGENT_INVALID_REQUEST";
    public const string NotInGame = "AGENT_NOT_IN_GAME";
    public const string InjectionFailed = "AGENT_INJECTION_FAILED";
    public const string HookNotReady = "AGENT_HOOK_NOT_READY";
    public const string ExecutionTimeout = "AGENT_EXECUTION_TIMEOUT";
    public const string EvasionInitFailed = "AGENT_EVASION_INIT_FAILED";
    public const string BackendUnavailable = "AGENT_BACKEND_UNAVAILABLE";
    public const string InternalError = "AGENT_INTERNAL_ERROR";
}
