using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TalosForge.UnlockerAgentHost.Execution;
using TalosForge.UnlockerAgentHost.Models;

namespace TalosForge.UnlockerAgentHost.Runtime;

public sealed class AgentPipeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AgentHostOptions _options;
    private readonly AgentCommandProcessor _processor;
    private readonly ILogger<AgentPipeService> _logger;

    public AgentPipeService(
        AgentHostOptions options,
        AgentCommandProcessor processor,
        ILogger<AgentPipeService> logger)
    {
        _options = options;
        _processor = processor;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("UnlockerAgentHost started. pipe={Pipe}", _options.PipeName);

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
                _logger.LogWarning(ex, "Agent pipe client handler failed.");
            }
        }

        _logger.LogInformation("UnlockerAgentHost stopped.");
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        StreamReader? reader = null;
        StreamWriter? writer = null;
        try
        {
            reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

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

                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                AgentPipeResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<AgentPipeRequest>(line, JsonOptions);
                    if (request is null)
                    {
                        response = BuildInvalidRequest("Request payload is null.");
                    }
                    else
                    {
                        response = await _processor.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (JsonException ex)
                {
                    response = BuildInvalidRequest($"JSON parse error ({ex.Message}).");
                }
                catch (Exception ex)
                {
                    response = new AgentPipeResponse(
                        false,
                        $"{AgentResultCodes.InternalError}: {ex.GetType().Name}",
                        JsonSerializer.Serialize(new
                        {
                            code = AgentResultCodes.InternalError,
                            message = ex.Message
                        }),
                        AgentResultCodes.InternalError,
                        null,
                        "internal_fault",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
        finally
        {
            try
            {
                writer?.Dispose();
            }
            catch (IOException)
            {
            }

            try
            {
                reader?.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }

    private static AgentPipeResponse BuildInvalidRequest(string message)
    {
        return new AgentPipeResponse(
            false,
            $"{AgentResultCodes.InvalidRequest}: {message}",
            JsonSerializer.Serialize(new
            {
                code = AgentResultCodes.InvalidRequest,
                message
            }),
            AgentResultCodes.InvalidRequest,
            null,
            "invalid_request",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
