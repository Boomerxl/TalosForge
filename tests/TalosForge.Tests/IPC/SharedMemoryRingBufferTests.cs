using System.IO.MemoryMappedFiles;
using System.Text;
using TalosForge.Core.IPC;
using Xunit;

namespace TalosForge.Tests.IPC;

public sealed class SharedMemoryRingBufferTests
{
    [Fact]
    public void Preserves_Message_Order_And_Wraparound()
    {
        var name = $"TalosForge.Test.Cmd.{Guid.NewGuid():N}";

        using var ring = new SharedMemoryRingBuffer(name, 512);

        var messages = new[]
        {
            Encoding.UTF8.GetBytes(new string('A', 120)),
            Encoding.UTF8.GetBytes(new string('B', 120)),
            Encoding.UTF8.GetBytes(new string('C', 120)),
        };

        foreach (var message in messages)
        {
            Assert.True(ring.TryWrite(message));
        }

        for (var i = 0; i < messages.Length; i++)
        {
            Assert.True(ring.TryRead(out var payload));
            Assert.Equal(messages[i], payload);
        }
    }

    [Fact]
    public void Reinitializes_When_Header_Is_Corrupted()
    {
        var name = $"TalosForge.Test.Corrupt.{Guid.NewGuid():N}";
        const int capacity = 1024;

        using (var ring = new SharedMemoryRingBuffer(name, capacity))
        {
            Assert.Contains("version=1", ring.DebugHeader());
        }

        using (var mmf = MemoryMappedFile.CreateOrOpen(name, 20 + capacity))
        using (var accessor = mmf.CreateViewAccessor())
        {
            accessor.Write(0, unchecked((int)0xDEADBEEF));
        }

        using (var ring2 = new SharedMemoryRingBuffer(name, capacity))
        {
            var header = ring2.DebugHeader();
            Assert.Contains("version=1", header);
            Assert.Contains("write=0", header);
            Assert.Contains("read=0", header);
        }
    }
}
