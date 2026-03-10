using Microsoft.Extensions.Logging;
using TalosForge.Core.IPC;
using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Abstractions;
using TalosForge.UnlockerHost.Configuration;
using TalosForge.UnlockerHost.Models;
using System.Text.Json;

namespace TalosForge.UnlockerHost.Host;

/// <summary>
/// Shared-memory command endpoint that consumes TalosForge commands and writes correlated ACK events.
/// </summary>
public sealed class UnlockerHostService : IDisposable
{
    private readonly UnlockerHostOptions _options;
    private readonly ICommandExecutor _executor;
    private readonly ILogger<UnlockerHostService> _logger;
    private readonly SharedMemoryRingBuffer _commandRing;
    private readonly SharedMemoryRingBuffer _eventRing;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    private bool _disposed;
    private long _commandsRead;
    private long _acksWritten;
    private long _acksDropped;
    private long _executorFailures;
    private long _decodeFailures;

    public UnlockerHostService(
        UnlockerHostOptions options,
        ICommandExecutor executor,
        ILogger<UnlockerHostService> logger)
    {
        _options = options;
        _executor = executor;
        _logger = logger;

        _commandRing = new SharedMemoryRingBuffer(options.CommandRingName, options.RingCapacityBytes);
        _eventRing = new SharedMemoryRingBuffer(options.EventRingName, options.RingCapacityBytes);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var pollDelayMs = Math.Max(1, _options.PollDelayMs);
        var statsInterval = TimeSpan.FromSeconds(Math.Max(1, _options.StatsIntervalSeconds));
        var nextStatsAt = DateTimeOffset.UtcNow.Add(statsInterval);
        var statusInterval = TimeSpan.FromMilliseconds(Math.Max(100, _options.StatusWriteIntervalMs));
        var nextStatusAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "UnlockerHost started. cmd_ring={CommandRing} evt_ring={EventRing} executor={Executor}",
            _options.CommandRingName,
            _options.EventRingName,
            _options.ExecutorMode);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_commandRing.TryRead(out var commandBytes))
                {
                    WriteStatusIfDue(ref nextStatusAt, statusInterval, running: true);
                    EmitStatsIfDue(ref nextStatsAt, statsInterval);
                    await Task.Delay(pollDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                Interlocked.Increment(ref _commandsRead);

                UnlockerCommand? command;
                try
                {
                    command = SharedMemoryUnlockerClient.DeserializeCommand(commandBytes);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _decodeFailures);
                    _logger.LogWarning(ex, "Failed to decode command frame. bytes={ByteCount}", commandBytes.Length);
                    WriteStatusIfDue(ref nextStatusAt, statusInterval, running: true);
                    EmitStatsIfDue(ref nextStatsAt, statsInterval);
                    continue;
                }

                var result = await ExecuteSafelyAsync(command, cancellationToken).ConfigureAwait(false);

                var ack = new UnlockerAck(
                    command.CommandId,
                    result.Success,
                    result.Message,
                    result.PayloadJson,
                    DateTimeOffset.UtcNow);

                var writeOk = await TryWriteAckAsync(ack, cancellationToken).ConfigureAwait(false);
                if (writeOk)
                {
                    Interlocked.Increment(ref _acksWritten);
                }
                else
                {
                    Interlocked.Increment(ref _acksDropped);
                    _logger.LogWarning(
                        "Dropped ack after retries. command_id={CommandId} opcode={Opcode}",
                        command.CommandId,
                        command.Opcode);
                }

                WriteStatusIfDue(ref nextStatusAt, statusInterval, running: true);
                EmitStatsIfDue(ref nextStatsAt, statsInterval);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
        finally
        {
            WriteStatus(running: false);
            EmitStats(force: true);
            _logger.LogInformation("UnlockerHost stopped.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _eventRing.Dispose();
        _commandRing.Dispose();
        _disposed = true;
    }

    private async ValueTask<CommandExecutionResult> ExecuteSafelyAsync(
        UnlockerCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _executorFailures);
            _logger.LogWarning(
                ex,
                "Executor failed. command_id={CommandId} opcode={Opcode} payload_len={PayloadLength}",
                command.CommandId,
                command.Opcode,
                command.PayloadJson?.Length ?? 0);

            // Keep this host stable by returning a failed ACK instead of crashing the loop.
            return CommandExecutionResult.Fail($"Executor exception: {ex.GetType().Name}");
        }
    }

    private async Task<bool> TryWriteAckAsync(UnlockerAck ack, CancellationToken cancellationToken)
    {
        var bytes = SharedMemoryUnlockerClient.SerializeAck(ack);
        var retries = Math.Max(0, _options.AckWriteRetryCount);
        var delayMs = Math.Max(1, _options.AckWriteDelayMs);

        for (var attempt = 0; attempt <= retries; attempt++)
        {
            if (_eventRing.TryWrite(bytes))
            {
                return true;
            }

            if (attempt < retries)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private void EmitStatsIfDue(ref DateTimeOffset nextStatsAt, TimeSpan interval)
    {
        if (DateTimeOffset.UtcNow < nextStatsAt)
        {
            return;
        }

        EmitStats(force: false);
        nextStatsAt = DateTimeOffset.UtcNow.Add(interval);
    }

    private void EmitStats(bool force)
    {
        _logger.LogInformation(
            "host-stats commands_read={CommandsRead} acks_written={AcksWritten} acks_dropped={AcksDropped} decode_failures={DecodeFailures} executor_failures={ExecutorFailures} force={Force}",
            Interlocked.Read(ref _commandsRead),
            Interlocked.Read(ref _acksWritten),
            Interlocked.Read(ref _acksDropped),
            Interlocked.Read(ref _decodeFailures),
            Interlocked.Read(ref _executorFailures),
            force);
    }

    private void WriteStatusIfDue(ref DateTimeOffset nextStatusAt, TimeSpan interval, bool running)
    {
        if (DateTimeOffset.UtcNow < nextStatusAt)
        {
            return;
        }

        WriteStatus(running);
        nextStatusAt = DateTimeOffset.UtcNow.Add(interval);
    }

    private void WriteStatus(bool running)
    {
        try
        {
            var status = new UnlockerHostStatusFile(
                DateTimeOffset.UtcNow,
                _startedUtc,
                Environment.ProcessId,
                _options.ExecutorMode,
                Interlocked.Read(ref _commandsRead),
                Interlocked.Read(ref _acksWritten),
                Interlocked.Read(ref _acksDropped),
                Interlocked.Read(ref _decodeFailures),
                Interlocked.Read(ref _executorFailures),
                running);

            var directory = Path.GetDirectoryName(_options.StatusFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(status);
            var tempFile = _options.StatusFilePath + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Copy(tempFile, _options.StatusFilePath, overwrite: true);
            File.Delete(tempFile);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to write status file {StatusFile}", _options.StatusFilePath);
        }
    }
}
