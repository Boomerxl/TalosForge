using TalosForge.Core.Warden;
using TalosForge.Tests.Core.Fakes;
using Xunit;

namespace TalosForge.Tests.Warden;

public sealed class WardenMonitorTests
{
    private const uint WardenStructurePTR = 0x00D31A4C;
    private const uint ClientConnection = 0x00C79CE0;
    private const uint LoadWardenModule = 0x00872350;
    private const uint VTableOffset = 0x228;

    private static FakeMemoryReader BuildReader()
    {
        var reader = new FakeMemoryReader { BaseAddress = new IntPtr(0x400000) };
        reader.Set(new IntPtr(LoadWardenModule), 0x55_8B_ECu);
        return reader;
    }

    [Fact]
    public void TakeSnapshot_Reports_NotLoaded_When_ClientConnection_Is_Zero()
    {
        var reader = BuildReader();
        reader.Set(new IntPtr(unchecked((int)ClientConnection)), 0u);
        reader.Set(new IntPtr(unchecked((int)WardenStructurePTR)), 0u);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);
        var snapshot = monitor.TakeSnapshot();

        Assert.Equal(WardenState.NotLoaded, snapshot.State);
        Assert.Contains("not connected", snapshot.StateDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TakeSnapshot_Reports_NotLoaded_When_WardenPtr_Is_Zero()
    {
        var reader = BuildReader();
        reader.Set(new IntPtr(unchecked((int)ClientConnection)), 0x500000u);
        reader.Set(new IntPtr(unchecked((int)WardenStructurePTR)), 0u);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);
        var snapshot = monitor.TakeSnapshot();

        Assert.Equal(WardenState.NotLoaded, snapshot.State);
        Assert.Contains("0x00000000", snapshot.StateDetail);
    }

    [Fact]
    public void TakeSnapshot_Reports_Loaded_When_VTable_Is_Zero()
    {
        var reader = BuildReader();
        var wardenPtr = new IntPtr(0x800000);

        reader.Set(new IntPtr(unchecked((int)ClientConnection)), 0x500000u);
        reader.Set(new IntPtr(unchecked((int)WardenStructurePTR)), (uint)wardenPtr.ToInt32());
        reader.Set(IntPtr.Add(wardenPtr, (int)VTableOffset), 0u);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);
        var snapshot = monitor.TakeSnapshot();

        Assert.Equal(WardenState.Loaded, snapshot.State);
        Assert.Contains("0x00800000", snapshot.StateDetail);
        Assert.Contains("not yet active", snapshot.StateDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TakeSnapshot_Reports_Active_When_VTable_Is_NonZero()
    {
        var reader = BuildReader();
        var wardenPtr = new IntPtr(0x800000);

        reader.Set(new IntPtr(unchecked((int)ClientConnection)), 0x500000u);
        reader.Set(new IntPtr(unchecked((int)WardenStructurePTR)), (uint)wardenPtr.ToInt32());
        reader.Set(IntPtr.Add(wardenPtr, (int)VTableOffset), 0x900000u);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);
        var snapshot = monitor.TakeSnapshot();

        Assert.Equal(WardenState.Active, snapshot.State);
        Assert.Contains("0x00800000", snapshot.StateDetail);
        Assert.Contains("0x00900000", snapshot.StateDetail);
    }

    [Fact]
    public void TakeSnapshot_Reports_Unknown_When_Memory_Is_Unreadable()
    {
        var reader = new FakeMemoryReader { BaseAddress = new IntPtr(0x400000) };
        var monitor = new WardenMonitor(reader, MemoryReaderMode.Internal);

        var snapshot = monitor.TakeSnapshot();

        Assert.Equal(WardenState.Unknown, snapshot.State);
    }

    [Fact]
    public void TakeSnapshot_Reflects_Internal_ReaderMode()
    {
        var reader = BuildReader();
        reader.Set(new IntPtr(unchecked((int)ClientConnection)), 0u);
        reader.Set(new IntPtr(unchecked((int)WardenStructurePTR)), 0u);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.Internal);
        var snapshot = monitor.TakeSnapshot();

        Assert.Equal(MemoryReaderMode.Internal, snapshot.ReaderMode);
    }

    [Fact]
    public void TakeSnapshot_Reflects_External_ReaderMode()
    {
        var reader = BuildReader();
        reader.Set(new IntPtr(unchecked((int)ClientConnection)), 0u);
        reader.Set(new IntPtr(unchecked((int)WardenStructurePTR)), 0u);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);
        var snapshot = monitor.TakeSnapshot();

        Assert.Equal(MemoryReaderMode.External, snapshot.ReaderMode);
    }

    [Fact]
    public void TakeSnapshot_Returns_Empty_Canary_When_Not_Initialized()
    {
        var reader = BuildReader();
        reader.Set(new IntPtr(unchecked((int)ClientConnection)), 0u);
        reader.Set(new IntPtr(unchecked((int)WardenStructurePTR)), 0u);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);
        var snapshot = monitor.TakeSnapshot();

        Assert.Empty(snapshot.CanaryAlerts);
    }

    [Fact]
    public void TakeSnapshot_Contains_Valid_Timestamp()
    {
        var reader = BuildReader();
        reader.Set(new IntPtr(unchecked((int)ClientConnection)), 0u);
        reader.Set(new IntPtr(unchecked((int)WardenStructurePTR)), 0u);

        var before = DateTimeOffset.UtcNow;
        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);
        var snapshot = monitor.TakeSnapshot();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(snapshot.Timestamp, before, after);
    }

    [Fact]
    public void TakeSnapshot_Warden_State_Transitions_Are_Detected()
    {
        var reader = BuildReader();
        var wardenPtr = new IntPtr(0x800000);

        reader.Set(new IntPtr(unchecked((int)ClientConnection)), 0x500000u);
        reader.Set(new IntPtr(unchecked((int)WardenStructurePTR)), 0u);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);

        var s1 = monitor.TakeSnapshot();
        Assert.Equal(WardenState.NotLoaded, s1.State);

        reader.Set(new IntPtr(unchecked((int)WardenStructurePTR)), (uint)wardenPtr.ToInt32());
        reader.Set(IntPtr.Add(wardenPtr, (int)VTableOffset), 0u);

        var s2 = monitor.TakeSnapshot();
        Assert.Equal(WardenState.Loaded, s2.State);

        reader.Set(IntPtr.Add(wardenPtr, (int)VTableOffset), 0x900000u);

        var s3 = monitor.TakeSnapshot();
        Assert.Equal(WardenState.Active, s3.State);
    }
}
