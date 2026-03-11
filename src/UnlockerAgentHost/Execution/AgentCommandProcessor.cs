using System.Text.Json;
using Microsoft.Extensions.Logging;
using TalosForge.UnlockerAgentHost.Models;
using TalosForge.UnlockerAgentHost.Runtime;

namespace TalosForge.UnlockerAgentHost.Execution;

public sealed class AgentCommandProcessor
{
    private readonly AgentHostOptions _options;
    private readonly AgentSessionManager _sessionManager;
    private readonly IAgentRuntime _runtime;
    private readonly ILogger<AgentCommandProcessor> _logger;

    public AgentCommandProcessor(
        AgentHostOptions options,
        AgentSessionManager sessionManager,
        IAgentRuntime runtime,
        ILogger<AgentCommandProcessor> logger)
    {
        _options = options;
        _sessionManager = sessionManager;
        _runtime = runtime;
        _logger = logger;
    }

    public async ValueTask<AgentPipeResponse> ProcessAsync(AgentPipeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ValidateRequest(request, out var validationError))
        {
            return BuildResponse(
                success: false,
                code: AgentResultCodes.InvalidRequest,
                message: $"{AgentResultCodes.InvalidRequest}: {validationError}",
                payloadJson: BuildPayload(AgentResultCodes.InvalidRequest, validationError));
        }

        var ready = await _sessionManager
            .EnsureReadyAsync(request.EvasionProfile, cancellationToken)
            .ConfigureAwait(false);
        if (!ready.Success)
        {
            return BuildResponse(
                success: false,
                code: ready.Code,
                message: $"{ready.Code}: {ready.Message}",
                payloadJson: BuildPayload(ready.Code, ready.Message));
        }

        var requestTimeoutMs = request.RequestTimeoutMs > 0
            ? request.RequestTimeoutMs
            : Math.Max(1, _options.RequestTimeoutMs);
        var maxAttempts = Math.Max(1, _options.RetryCount + 1);

        AgentRuntimeExecutionResult? lastFailure = null;
        var diagnostics = new ExecutionDiagnostics(maxAttempts, requestTimeoutMs);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            diagnostics.Attempts = attempt;
            var execRequest = new AgentExecutionRequest(
                request.CommandId,
                request.Opcode,
                request.PayloadJson,
                requestTimeoutMs);
            var result = await _runtime.ExecuteAsync(execRequest, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                diagnostics.LastCode = AgentResultCodes.Ok;
                return BuildResponse(
                    success: true,
                    code: AgentResultCodes.Ok,
                    message: result.Message,
                    payloadJson: result.PayloadJson ?? request.PayloadJson,
                    diagnosticsJson: JsonSerializer.Serialize(diagnostics));
            }

            lastFailure = result;
            diagnostics.LastCode = result.Code;
            _logger.LogDebug(
                "agent-exec command_id={CommandId} opcode={Opcode} attempt={Attempt}/{MaxAttempts} code={Code} transient={Transient}",
                request.CommandId,
                request.Opcode,
                attempt,
                maxAttempts,
                result.Code,
                result.TransientFailure);

            if (!result.TransientFailure || attempt >= maxAttempts)
            {
                break;
            }

            var backoff = ComputeBackoff(attempt);
            diagnostics.LastBackoffMs = backoff;
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
        }

        var code = string.IsNullOrWhiteSpace(lastFailure?.Code)
            ? AgentResultCodes.InternalError
            : lastFailure.Code;
        var message = lastFailure?.Message ?? "Execution failed.";
        var payload = string.IsNullOrWhiteSpace(lastFailure?.PayloadJson)
            ? BuildPayload(code, message)
            : lastFailure.PayloadJson;

        return BuildResponse(
            success: false,
            code: code,
            message: $"{code}: {message}",
            payloadJson: payload,
            diagnosticsJson: JsonSerializer.Serialize(diagnostics));
    }

    private static bool ValidateRequest(AgentPipeRequest request, out string error)
    {
        error = string.Empty;
        if (request.Version <= 0)
        {
            error = "Version must be >= 1.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Opcode))
        {
            error = "Opcode is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            error = "PayloadJson is required.";
            return false;
        }

        return true;
    }

    private int ComputeBackoff(int attempt)
    {
        var baseDelay = Math.Max(1, _options.BackoffBaseMs);
        var maxDelay = Math.Max(baseDelay, _options.BackoffMaxMs);
        var factor = 1 << Math.Min(8, Math.Max(0, attempt - 1));
        var value = baseDelay * factor;
        return Math.Min(value, maxDelay);
    }

    private AgentPipeResponse BuildResponse(
        bool success,
        string code,
        string message,
        string? payloadJson,
        string? diagnosticsJson = null)
    {
        return new AgentPipeResponse(
            success,
            message,
            payloadJson,
            code,
            diagnosticsJson,
            _sessionManager.State,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static string BuildPayload(string code, string message)
    {
        return JsonSerializer.Serialize(new { code, message });
    }

    private sealed class ExecutionDiagnostics
    {
        public ExecutionDiagnostics(int maxAttempts, int timeoutMs)
        {
            MaxAttempts = maxAttempts;
            TimeoutMs = timeoutMs;
        }

        public int MaxAttempts { get; }
        public int TimeoutMs { get; }
        public int Attempts { get; set; }
        public int LastBackoffMs { get; set; }
        public string LastCode { get; set; } = AgentResultCodes.Ok;
    }
}
