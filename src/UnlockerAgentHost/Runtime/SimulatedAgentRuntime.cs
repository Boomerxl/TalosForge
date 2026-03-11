using System.Text.Json;
using System.Threading.Channels;
using TalosForge.UnlockerAgentHost.Models;

namespace TalosForge.UnlockerAgentHost.Runtime;

/// <summary>
/// Safe in-process simulation runtime that models game-thread command execution.
/// </summary>
public sealed class SimulatedAgentRuntime : IAgentRuntime
{
    private readonly AgentHostOptions _options;
    private readonly Channel<QueuedCommand> _queue;
    private readonly CancellationTokenSource _workerCts = new();
    private readonly object _sync = new();

    private Task? _workerTask;
    private bool _ready;

    public SimulatedAgentRuntime(AgentHostOptions options)
    {
        _options = options;
        _queue = Channel.CreateUnbounded<QueuedCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public ValueTask<AgentRuntimeReadyResult> EnsureReadyAsync(string evasionProfile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedProfile = string.IsNullOrWhiteSpace(evasionProfile)
            ? "off"
            : evasionProfile.Trim().ToLowerInvariant();

        if (_options.SimulateEvasionInitFailure && normalizedProfile != "off")
        {
            return ValueTask.FromResult(
                new AgentRuntimeReadyResult(
                    false,
                    "Evasion initialization failed in simulation mode.",
                    AgentResultCodes.EvasionInitFailed));
        }

        lock (_sync)
        {
            if (_workerTask == null)
            {
                _workerTask = Task.Run(() => WorkerLoopAsync(_workerCts.Token), _workerCts.Token);
            }

            _ready = true;
        }

        return ValueTask.FromResult(
            new AgentRuntimeReadyResult(
                true,
                $"Runtime ready (mode=sim, evasion={normalizedProfile}).",
                AgentResultCodes.Ok));
    }

    public async ValueTask<AgentRuntimeExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_ready)
        {
            return new AgentRuntimeExecutionResult(
                false,
                "Runtime not ready.",
                null,
                AgentResultCodes.HookNotReady,
                TransientFailure: true);
        }

        var completion = new TaskCompletionSource<AgentRuntimeExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new QueuedCommand(request, completion);

        if (!_queue.Writer.TryWrite(queued))
        {
            return new AgentRuntimeExecutionResult(
                false,
                "Command queue unavailable.",
                null,
                AgentResultCodes.BackendUnavailable,
                TransientFailure: true);
        }

        var timeoutMs = Math.Max(1, request.RequestTimeoutMs);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new AgentRuntimeExecutionResult(
                false,
                $"Command timed out after {timeoutMs}ms.",
                BuildErrorPayload(AgentResultCodes.ExecutionTimeout, $"Command timed out after {timeoutMs}ms."),
                AgentResultCodes.ExecutionTimeout,
                TransientFailure: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _workerCts.Cancel();
        _queue.Writer.TryComplete();
        if (_workerTask != null)
        {
            try
            {
                await _workerTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _workerCts.Dispose();
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var queued in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                var ack = BuildAck(queued.Request.Opcode);
                queued.Completion.TrySetResult(
                    new AgentRuntimeExecutionResult(
                        true,
                        ack,
                        queued.Request.PayloadJson,
                        AgentResultCodes.Ok,
                        TransientFailure: false));
            }
            catch (OperationCanceledException)
            {
                queued.Completion.TrySetResult(
                    new AgentRuntimeExecutionResult(
                        false,
                        "Command canceled.",
                        null,
                        AgentResultCodes.InternalError,
                        TransientFailure: true));
            }
            catch (Exception ex)
            {
                queued.Completion.TrySetResult(
                    new AgentRuntimeExecutionResult(
                        false,
                        $"{AgentResultCodes.InternalError}: {ex.GetType().Name}",
                        BuildErrorPayload(AgentResultCodes.InternalError, ex.Message),
                        AgentResultCodes.InternalError,
                        TransientFailure: true));
            }
        }
    }

    private static string BuildAck(string opcode)
    {
        return $"ACK:{opcode}";
    }

    private static string BuildErrorPayload(string code, string message)
    {
        return JsonSerializer.Serialize(new { code, message });
    }

    private sealed record QueuedCommand(
        AgentExecutionRequest Request,
        TaskCompletionSource<AgentRuntimeExecutionResult> Completion);
}
