using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Abstractions;
using TalosForge.UnlockerHost.Configuration;
using TalosForge.UnlockerHost.Models;

namespace TalosForge.UnlockerHost.Execution;

/// <summary>
/// Adapter backend that forwards normalized commands to an external bridge over named pipes.
/// </summary>
public sealed class NamedPipeAdapterBackend : IAdapterBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;
    private readonly int _requestTimeoutMs;

    public NamedPipeAdapterBackend(UnlockerHostOptions options)
    {
        _pipeName = string.IsNullOrWhiteSpace(options.AdapterPipeName)
            ? "TalosForge.UnlockerAdapter.v1"
            : options.AdapterPipeName.Trim();
        _connectTimeoutMs = Math.Max(1, options.AdapterConnectTimeoutMs);
        _requestTimeoutMs = Math.Max(1, options.AdapterRequestTimeoutMs);
    }

    public async ValueTask<CommandExecutionResult> ExecuteAsync(UnlockerCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        var connectResult = await TryConnectAsync(pipe, cancellationToken).ConfigureAwait(false);
        if (connectResult is not null)
        {
            return connectResult;
        }

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var request = new AdapterPipeRequest(
            Version: 1,
            CommandId: command.CommandId,
            Opcode: command.Opcode.ToString(),
            OpcodeValue: (int)command.Opcode,
            PayloadJson: command.PayloadJson,
            TimestampUnixMs: command.TimestampUtc.ToUnixTimeMilliseconds());

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        try
        {
            await writer.WriteLineAsync(requestJson).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return CommandExecutionResult.Fail(
                $"{AdapterResultCodes.BackendError}: Pipe write failed ({ex.GetType().Name}).",
                AdapterCommandExecutor.BuildCodePayload(AdapterResultCodes.BackendError, ex.Message));
        }

        var readResult = await TryReadLineWithTimeoutAsync(reader, _requestTimeoutMs, cancellationToken).ConfigureAwait(false);
        if (readResult.Error is not null)
        {
            return readResult.Error;
        }

        AdapterPipeResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<AdapterPipeResponse>(readResult.Line!, JsonOptions);
        }
        catch (JsonException ex)
        {
            return CommandExecutionResult.Fail(
                $"{AdapterResultCodes.BackendError}: Invalid pipe response JSON.",
                AdapterCommandExecutor.BuildCodePayload(AdapterResultCodes.BackendError, ex.Message));
        }

        if (response is null || string.IsNullOrWhiteSpace(response.Message))
        {
            return CommandExecutionResult.Fail(
                $"{AdapterResultCodes.BackendError}: Empty pipe response.",
                AdapterCommandExecutor.BuildCodePayload(
                    AdapterResultCodes.BackendError,
                    "Adapter backend returned an empty response."));
        }

        var payload = response.PayloadJson;
        if (string.IsNullOrWhiteSpace(payload) && !string.IsNullOrWhiteSpace(response.Code))
        {
            payload = AdapterCommandExecutor.BuildCodePayload(response.Code, response.Message);
        }

        if (response.Success)
        {
            return CommandExecutionResult.Ok(response.Message, payload);
        }

        return CommandExecutionResult.Fail(
            response.Message,
            payload ?? AdapterCommandExecutor.BuildCodePayload(AdapterResultCodes.BackendError, response.Message));
    }

    private async Task<CommandExecutionResult?> TryConnectAsync(
        NamedPipeClientStream pipe,
        CancellationToken cancellationToken)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_connectTimeoutMs);

        try
        {
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CommandExecutionResult.Fail(
                $"{AdapterResultCodes.BackendUnavailable}: Pipe '{_pipeName}' connect timeout after {_connectTimeoutMs}ms.",
                AdapterCommandExecutor.BuildCodePayload(
                    AdapterResultCodes.BackendUnavailable,
                    $"Pipe '{_pipeName}' connect timeout."));
        }
        catch (TimeoutException ex)
        {
            return CommandExecutionResult.Fail(
                $"{AdapterResultCodes.BackendUnavailable}: Pipe '{_pipeName}' connect timeout.",
                AdapterCommandExecutor.BuildCodePayload(AdapterResultCodes.BackendUnavailable, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandExecutionResult.Fail(
                $"{AdapterResultCodes.BackendUnavailable}: Pipe '{_pipeName}' unavailable ({ex.GetType().Name}).",
                AdapterCommandExecutor.BuildCodePayload(AdapterResultCodes.BackendUnavailable, ex.Message));
        }
    }

    private static async Task<(string? Line, CommandExecutionResult? Error)> TryReadLineWithTimeoutAsync(
        StreamReader reader,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        var readTask = reader.ReadLineAsync();
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
        var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);

        if (completed == readTask)
        {
            try
            {
                var line = await readTask.ConfigureAwait(false);
                if (line is null)
                {
                    return (
                        null,
                        CommandExecutionResult.Fail(
                            $"{AdapterResultCodes.BackendError}: Pipe response ended unexpectedly.",
                            AdapterCommandExecutor.BuildCodePayload(
                                AdapterResultCodes.BackendError,
                                "Adapter backend closed the pipe without a response line.")));
                }

                return (line, null);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return (
                    null,
                    CommandExecutionResult.Fail(
                        $"{AdapterResultCodes.BackendError}: Pipe read failed ({ex.GetType().Name}).",
                        AdapterCommandExecutor.BuildCodePayload(AdapterResultCodes.BackendError, ex.Message)));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (
            null,
            CommandExecutionResult.Fail(
                $"{AdapterResultCodes.BackendError}: Pipe response timeout after {timeoutMs}ms.",
                AdapterCommandExecutor.BuildCodePayload(
                    AdapterResultCodes.BackendError,
                    $"Adapter backend response timeout after {timeoutMs}ms.")));
    }
}
