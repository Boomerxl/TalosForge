using System.Text;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Configuration;
using TalosForge.Core.Models;

namespace TalosForge.Core.IPC;

/// <summary>
/// Unlocker client backed by shared-memory command/event rings.
/// </summary>
public sealed class SharedMemoryUnlockerClient : IUnlockerClient
{
    private readonly BotOptions _options;
    private readonly SharedMemoryRingBuffer _commandRing;
    private readonly SharedMemoryRingBuffer _eventRing;
    private readonly Dictionary<long, UnlockerAck> _pendingAcks = new();

    public SharedMemoryUnlockerClient(BotOptions options)
    {
        _options = options;
        _commandRing = new SharedMemoryRingBuffer(options.CommandMmfName, options.RingCapacityBytes);
        _eventRing = new SharedMemoryRingBuffer(options.EventMmfName, options.RingCapacityBytes);
    }

    public async Task<UnlockerAck> SendAsync(UnlockerCommand command, CancellationToken cancellationToken)
    {
        var commandBytes = SerializeCommand(command);

        for (var attempt = 0; attempt <= _options.UnlockerRetryCount; attempt++)
        {
            if (!_commandRing.TryWrite(commandBytes))
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var ack = await WaitForAckAsync(command.CommandId, cancellationToken).ConfigureAwait(false);
            if (ack != null)
            {
                return ack;
            }
        }

        throw new TimeoutException($"No unlocker ack for command {command.CommandId}.");
    }

    public void Dispose()
    {
        _commandRing.Dispose();
        _eventRing.Dispose();
    }

    private async Task<UnlockerAck?> WaitForAckAsync(long commandId, CancellationToken cancellationToken)
    {
        if (_pendingAcks.TryGetValue(commandId, out var pending))
        {
            _pendingAcks.Remove(commandId);
            return pending;
        }

        var timeoutMs = Math.Max(1, _options.UnlockerTimeoutMs);
        var deadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (_eventRing.TryRead(out var ackBytes))
            {
                var ack = DeserializeAck(ackBytes);
                if (ack.CommandId == commandId)
                {
                    return ack;
                }

                _pendingAcks[ack.CommandId] = ack;
            }

            if (DateTime.UtcNow >= deadlineUtc)
            {
                return null;
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    public static byte[] SerializeCommand(UnlockerCommand command)
    {
        var payload = Encoding.UTF8.GetBytes(command.PayloadJson ?? string.Empty);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(command.CommandId);
        writer.Write((int)command.Opcode);
        writer.Write(payload.Length);
        writer.Write(command.TimestampUtc.ToUnixTimeMilliseconds());
        writer.Write(payload);

        writer.Flush();
        return stream.ToArray();
    }

    public static UnlockerCommand DeserializeCommand(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        var commandId = reader.ReadInt64();
        var opcode = (UnlockerOpcode)reader.ReadInt32();
        var payloadLength = reader.ReadInt32();
        var timestampMs = reader.ReadInt64();
        var payload = reader.ReadBytes(payloadLength);

        return new UnlockerCommand(
            commandId,
            opcode,
            Encoding.UTF8.GetString(payload),
            DateTimeOffset.FromUnixTimeMilliseconds(timestampMs));
    }

    public static byte[] SerializeAck(UnlockerAck ack)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(ack.PayloadJson ?? string.Empty);
        var messageBytes = Encoding.UTF8.GetBytes(ack.Message ?? string.Empty);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(ack.CommandId);
        writer.Write(ack.Success ? 1 : 0);
        writer.Write(payloadBytes.Length);
        writer.Write(ack.TimestampUtc.ToUnixTimeMilliseconds());
        writer.Write(messageBytes.Length);
        writer.Write(messageBytes);
        writer.Write(payloadBytes);

        writer.Flush();
        return stream.ToArray();
    }

    public static UnlockerAck DeserializeAck(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        var commandId = reader.ReadInt64();
        var success = reader.ReadInt32() == 1;
        var payloadLength = reader.ReadInt32();
        var timestampMs = reader.ReadInt64();
        var messageLength = reader.ReadInt32();
        var message = Encoding.UTF8.GetString(reader.ReadBytes(messageLength));
        var payloadBytes = reader.ReadBytes(payloadLength);
        var payload = payloadBytes.Length == 0 ? null : Encoding.UTF8.GetString(payloadBytes);

        return new UnlockerAck(
            commandId,
            success,
            message,
            payload,
            DateTimeOffset.FromUnixTimeMilliseconds(timestampMs));
    }
}
