using TalosForge.Core;
using TalosForge.Core.Warden;
using Xunit;

namespace TalosForge.Tests.Smoke;

[Trait(LiveSmokePreconditions.TraitName, LiveSmokePreconditions.TraitValue)]
public sealed class WardenMonitorSmokeTests
{
    [LiveWowFact]
    public void Live_WardenMonitor_Returns_Valid_Snapshot_When_Wow_Is_Running()
    {
        _ = LiveSmokePreconditions.RequireWowProcess();

        var reader = MemoryReader.Instance;
        var attached = reader.Attach();
        Assert.True(attached);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);
        monitor.InitializeCanary();
        var snapshot = monitor.TakeSnapshot();

        Assert.NotEqual(WardenState.Unknown, snapshot.State);
        Assert.Equal(MemoryReaderMode.External, snapshot.ReaderMode);
        Assert.True(snapshot.RwxPageCount >= 0);
        Assert.True(snapshot.HiddenModuleCount >= 0);
        Assert.NotNull(snapshot.CanaryAlerts);
    }

    [LiveWowFact]
    public void Live_WardenMonitor_Detects_Agent_Pipe_When_Injected()
    {
        _ = LiveSmokePreconditions.RequireWowProcess();

        var reader = MemoryReader.Instance;
        var attached = reader.Attach();
        Assert.True(attached);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);
        var snapshot = monitor.TakeSnapshot();

        if (snapshot.AgentPipeAlive)
        {
            Assert.NotEqual("N/A", snapshot.AgentPipeName);
            Assert.NotEqual("Not found", snapshot.AgentPipeName);
        }
    }

    [LiveWowFact]
    public void Live_WardenMonitor_Multiple_Snapshots_Remain_Consistent()
    {
        _ = LiveSmokePreconditions.RequireWowProcess();

        var reader = MemoryReader.Instance;
        var attached = reader.Attach();
        Assert.True(attached);

        var monitor = new WardenMonitor(reader, MemoryReaderMode.External);
        monitor.InitializeCanary();

        var s1 = monitor.TakeSnapshot();
        Thread.Sleep(100);
        var s2 = monitor.TakeSnapshot();

        Assert.Equal(s1.State, s2.State);
        Assert.Equal(s1.ReaderMode, s2.ReaderMode);
    }
}
