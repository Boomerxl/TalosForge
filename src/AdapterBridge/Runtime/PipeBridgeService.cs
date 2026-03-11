using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TalosForge.AdapterBridge.Execution;
using TalosForge.AdapterBridge.Models;

namespace TalosForge.AdapterBridge.Runtime;

public sealed class PipeBridgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BridgeOptions _options;
    private readonly IBridgeCommandExecutor _executor;
    private readonly ILogger<PipeBridgeService> _logger;

    public PipeBridgeService(
        BridgeOptions options,
        IBridgeCommandExecutor executor,
        ILogger<PipeBridgeService> logger)
    {
        _options = options;
        _executor = executor;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "AdapterBridge started. pipe={Pipe} mode={Mode}",
            _options.PipeName,
            _options.Mode);

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _options.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await HandleClientAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AdapterBridge client handler failed.");
            }
        }

        _logger.LogInformation("AdapterBridge stopped.");
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        while (server.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pipe read failed.");
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            AdapterPipeResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<AdapterPipeRequest>(line, JsonOptions);
                if (request is null || request.Version <= 0)
                {
                    response = new AdapterPipeResponse(
                        false,
                        "BRIDGE_INVALID_REQUEST: invalid request envelope.",
                        Code: "BRIDGE_INVALID_REQUEST");
                }
                else
                {
                    response = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(response.Message))
                    {
                        response = response with
                        {
                            Success = false,
                            Message = "BRIDGE_EXECUTOR_ERROR: empty response message.",
                            Code = response.Code ?? "BRIDGE_EXECUTOR_ERROR"
                        };
                    }

                    if (string.IsNullOrWhiteSpace(response.PayloadJson) && !string.IsNullOrWhiteSpace(response.Code))
                    {
                        response = response with
                        {
                            PayloadJson = JsonSerializer.Serialize(new
                            {
                                code = response.Code,
                                message = response.Message
                            }, JsonOptions)
                        };
                    }
                }
            }
            catch (JsonException ex)
            {
                response = new AdapterPipeResponse(
                    false,
                    $"BRIDGE_INVALID_REQUEST: JSON parse error ({ex.Message}).",
                    Code: "BRIDGE_INVALID_REQUEST");
            }
            catch (Exception ex)
            {
                response = new AdapterPipeResponse(
                    false,
                    $"BRIDGE_EXECUTOR_ERROR: {ex.GetType().Name}",
                    Code: "BRIDGE_EXECUTOR_ERROR");
            }

            var responseLine = JsonSerializer.Serialize(response, JsonOptions);
            try
            {
                await writer.WriteLineAsync(responseLine).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                return;
            }
        }
    }
}
