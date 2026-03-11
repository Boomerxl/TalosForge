using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TalosForge.AdapterBridge.Models;
using TalosForge.AdapterBridge.Runtime;

namespace TalosForge.AdapterBridge.Execution;

/// <summary>
/// Executes per-opcode commands against a configured unlocker CLI binary.
/// </summary>
public sealed class WowCliBridgeCommandExecutor : IBridgeCommandExecutor
{
    private readonly BridgeOptions _options;

    public WowCliBridgeCommandExecutor(BridgeOptions options)
    {
        _options = options;
    }

    public async ValueTask<AdapterPipeResponse> ExecuteAsync(AdapterPipeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!WowCliCommandTranslator.TryTranslate(request, out var invocation, out var errorResponse))
        {
            return EnsureStructuredErrorPayload(errorResponse);
        }

        if (string.IsNullOrWhiteSpace(_options.CommandPath))
        {
            return BuildError("BRIDGE_CONFIG_ERROR", "command path is required for wow-cli mode.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.CommandPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var prefixArg in SplitCommandArgs(_options.CommandArgs))
        {
            startInfo.ArgumentList.Add(prefixArg);
        }

        startInfo.ArgumentList.Add(invocation!.Verb);
        foreach (var arg in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return BuildError(
                "BRIDGE_WOWCLI_START_ERROR",
                $"failed to start CLI ({ex.GetType().Name}).",
                new { exception = ex.GetType().Name });
        }

        var timeoutMs = Math.Max(1, _options.CommandTimeoutMs);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

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
                "BRIDGE_WOWCLI_TIMEOUT",
                $"CLI timed out after {timeoutMs}ms.",
                new { timeoutMs });
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? Trim(stdout, 300) : Trim(stderr, 300);
            return BuildError(
                "BRIDGE_WOWCLI_EXIT_CODE",
                $"CLI exited with code {process.ExitCode}.",
                new
                {
                    exitCode = process.ExitCode,
                    detail
                });
        }

        var message = string.IsNullOrWhiteSpace(stdout) ? invocation.AckMessage : FirstLine(stdout);
        return new AdapterPipeResponse(
            true,
            message,
            PayloadJson: request.PayloadJson,
            Code: "OK");
    }

    public static IReadOnlyList<string> SplitCommandArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in args)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var index = text.IndexOfAny(new[] { '\r', '\n' });
        return index >= 0 ? text[..index].Trim() : text.Trim();
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

    private static AdapterPipeResponse EnsureStructuredErrorPayload(AdapterPipeResponse response)
    {
        if (response.Success || string.IsNullOrWhiteSpace(response.Code) || !string.IsNullOrWhiteSpace(response.PayloadJson))
        {
            return response;
        }

        var payload = JsonSerializer.Serialize(new
        {
            code = response.Code,
            message = response.Message
        });

        return response with { PayloadJson = payload };
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
