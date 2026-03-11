using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TalosForge.AdapterBridge.Models;
using TalosForge.AdapterBridge.Runtime;

namespace TalosForge.AdapterBridge.Execution;

/// <summary>
/// Forwards adapter requests to a persistent unlocker agent host over a named pipe.
/// </summary>
public sealed class WowAgentBridgeCommandExecutor : IBridgeCommandExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BridgeOptions _options;

    public WowAgentBridgeCommandExecutor(BridgeOptions options)
    {
        _options = options;
    }

    public async ValueTask<AdapterPipeResponse> ExecuteAsync(AdapterPipeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pipeName = string.IsNullOrWhiteSpace(_options.AgentPipeName)
            ? "TalosForge.Agent.v1"
            : _options.AgentPipeName.Trim();
        var connectTimeoutMs = Math.Max(1, _options.AgentConnectTimeoutMs);
        var requestTimeoutMs = Math.Max(1, _options.AgentRequestTimeoutMs);

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(connectTimeoutMs);

        try
        {
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return BuildError(
                "BRIDGE_WOWAGENT_CONNECT_TIMEOUT",
                $"agent connect timeout after {connectTimeoutMs}ms.",
                new { pipe = pipeName, connectTimeoutMs });
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return BuildError(
                "BRIDGE_WOWAGENT_UNAVAILABLE",
                $"agent pipe unavailable ({ex.GetType().Name}).",
                new { pipe = pipeName, exception = ex.GetType().Name });
        }

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var agentRequest = new AgentPipeRequest(
            Version: 1,
            CommandId: request.CommandId,
            Opcode: request.Opcode,
            OpcodeValue: request.OpcodeValue,
            PayloadJson: request.PayloadJson,
            TimestampUnixMs: request.TimestampUnixMs,
            RequestTimeoutMs: requestTimeoutMs,
            EvasionProfile: _options.AgentEvasionProfile);

        var line = JsonSerializer.Serialize(agentRequest, JsonOptions);
        try
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return BuildError(
                "BRIDGE_WOWAGENT_WRITE_ERROR",
                $"failed writing request ({ex.GetType().Name}).",
                new { exception = ex.GetType().Name });
        }

        var responseLine = await ReadLineWithTimeoutAsync(reader, requestTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (responseLine.Error is not null)
        {
            return responseLine.Error;
        }

        AgentPipeResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<AgentPipeResponse>(responseLine.Line!, JsonOptions);
        }
        catch (JsonException ex)
        {
            return BuildError(
                "BRIDGE_WOWAGENT_INVALID_RESPONSE",
                "agent response JSON is invalid.",
                new { parseError = ex.Message });
        }

        if (response is null || string.IsNullOrWhiteSpace(response.Message))
        {
            return BuildError(
                "BRIDGE_WOWAGENT_INVALID_RESPONSE",
                "agent response is empty.");
        }

        var code = string.IsNullOrWhiteSpace(response.Code)
            ? (response.Success ? "OK" : "BRIDGE_WOWAGENT_EXECUTION_ERROR")
            : response.Code;
        var payloadJson = response.PayloadJson;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadJson = JsonSerializer.Serialize(new
            {
                code,
                message = response.Message,
                state = response.AgentState,
                diagnostics = response.DiagnosticsJson
            }, JsonOptions);
        }

        return new AdapterPipeResponse(
            response.Success,
            response.Message,
            payloadJson,
            code);
    }

    private static async Task<(string? Line, AdapterPipeResponse? Error)> ReadLineWithTimeoutAsync(
        StreamReader reader,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        var readTask = reader.ReadLineAsync();
        var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
        var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);

        if (completed == readTask)
        {
            try
            {
                var line = await readTask.ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    return (null, BuildError("BRIDGE_WOWAGENT_EMPTY_RESPONSE", "agent returned empty response."));
                }

                return (line, null);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return (null, BuildError(
                    "BRIDGE_WOWAGENT_READ_ERROR",
                    $"failed reading response ({ex.GetType().Name}).",
                    new { exception = ex.GetType().Name }));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (null, BuildError(
            "BRIDGE_WOWAGENT_TIMEOUT",
            $"agent response timeout after {timeoutMs}ms.",
            new { timeoutMs }));
    }

    private static AdapterPipeResponse BuildError(string code, string message, object? diagnostics = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            code,
            message,
            diagnostics
        });

        return new AdapterPipeResponse(
            false,
            $"{code}: {message}",
            payload,
            code);
    }
}
