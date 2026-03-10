using TalosForge.Core.Configuration;
using TalosForge.Core.Models;

namespace TalosForge.Core.IPC;

/// <summary>
/// Test endpoint that simulates an unlocker by echoing ACKs.
/// </summary>
public sealed class MockUnlockerEndpoint : IDisposable
{
    private readonly SharedMemoryRingBuffer _commandRing;
    private readonly SharedMemoryRingBuffer _eventRing;
    private bool _disposed;

    public MockUnlockerEndpoint(BotOptions options)
    {
        _commandRing = new SharedMemoryRingBuffer(options.CommandMmfName, options.RingCapacityBytes);
        _eventRing = new SharedMemoryRingBuffer(options.EventMmfName, options.RingCapacityBytes);
    }

    public bool PumpOnce()
    {
        ThrowIfDisposed();

        if (!_commandRing.TryRead(out var commandBytes))
        {
            return false;
        }

        var command = SharedMemoryUnlockerClient.DeserializeCommand(commandBytes);
        var ack = new UnlockerAck(
            command.CommandId,
            Success: true,
            Message: $"ACK:{command.Opcode}",
            PayloadJson: command.PayloadJson,
            TimestampUtc: DateTimeOffset.UtcNow);

        var ackBytes = SharedMemoryUnlockerClient.SerializeAck(ack);
        if (!_eventRing.TryWrite(ackBytes))
        {
            throw new InvalidOperationException("Event ring is full; unable to write ack.");
        }

        return true;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var processed = PumpOnce();
            if (!processed)
            {
                await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _commandRing.Dispose();
        _eventRing.Dispose();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MockUnlockerEndpoint));
        }
    }
}
