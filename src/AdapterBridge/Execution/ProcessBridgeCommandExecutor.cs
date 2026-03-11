using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TalosForge.AdapterBridge.Models;
using TalosForge.AdapterBridge.Runtime;

namespace TalosForge.AdapterBridge.Execution;

/// <summary>
/// Invokes an external process per request. The process must read one request JSON line from stdin and write one
/// response JSON line to stdout using AdapterPipeResponse schema.
/// </summary>
public sealed class ProcessBridgeCommandExecutor : IBridgeCommandExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BridgeOptions _options;

    public ProcessBridgeCommandExecutor(BridgeOptions options)
    {
        _options = options;
    }

    public async ValueTask<AdapterPipeResponse> ExecuteAsync(AdapterPipeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(_options.CommandPath))
        {
            return BuildError("BRIDGE_CONFIG_ERROR", "command path is required.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.CommandPath,
            Arguments = _options.CommandArgs ?? string.Empty,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return BuildError(
                "BRIDGE_PROCESS_START_ERROR",
                $"failed to start process ({ex.GetType().Name}).",
                new { exception = ex.GetType().Name });
        }

        try
        {
            var requestLine = JsonSerializer.Serialize(request, JsonOptions);
            await process.StandardInput.WriteLineAsync(requestLine).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            TryKill(process);
            return BuildError(
                "BRIDGE_PROCESS_IO_ERROR",
                $"failed to write stdin ({ex.GetType().Name}).",
                new { exception = ex.GetType().Name });
        }

        var timeoutMs = Math.Max(1, _options.CommandTimeoutMs);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        var readTask = process.StandardOutput.ReadLineAsync();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return BuildError(
                "BRIDGE_PROCESS_TIMEOUT",
                $"process timed out after {timeoutMs}ms.",
                new { timeoutMs });
        }

        string? outputLine;
        try
        {
            outputLine = await readTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryKill(process);
            return BuildError(
                "BRIDGE_PROCESS_IO_ERROR",
                $"failed to read stdout ({ex.GetType().Name}).",
                new { exception = ex.GetType().Name });
        }

        if (string.IsNullOrWhiteSpace(outputLine))
        {
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            return BuildError(
                "BRIDGE_PROCESS_EMPTY_RESPONSE",
                "process returned empty stdout.",
                new { stderr = Trim(stderr, 300) });
        }

        try
        {
            var response = JsonSerializer.Deserialize<AdapterPipeResponse>(outputLine, JsonOptions);
            if (response is null || string.IsNullOrWhiteSpace(response.Message))
            {
                return BuildError("BRIDGE_PROCESS_INVALID_RESPONSE", "process returned invalid response payload.");
            }

            return response;
        }
        catch (JsonException ex)
        {
            return BuildError(
                "BRIDGE_PROCESS_INVALID_JSON",
                "stdout is not valid AdapterPipeResponse JSON.",
                new { parseError = ex.Message });
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static string Trim(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[..maxLength];
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
            PayloadJson: payload,
            Code: code);
    }
}
