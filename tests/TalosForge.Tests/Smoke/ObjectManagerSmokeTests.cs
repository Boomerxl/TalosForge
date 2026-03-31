using Microsoft.Extensions.Logging.Abstractions;
using TalosForge.Core;
using TalosForge.Core.ObjectManager;
using Xunit;

namespace TalosForge.Tests.Smoke;

[Trait(LiveSmokePreconditions.TraitName, LiveSmokePreconditions.TraitValue)]
public sealed class ObjectManagerSmokeTests
{
    [LiveWowFact]
    public void Live_ObjectScan_Does_Not_Crash_When_Wow_Is_Running()
    {
        _ = LiveSmokePreconditions.RequireWowProcess();

        var reader = MemoryReader.Instance;
        var attached = reader.Attach();
        Assert.True(attached);

        var manager = new ObjectManagerService(reader, NullLogger<ObjectManagerService>.Instance);
        var snapshot = manager.GetSnapshot(100);

        Assert.NotNull(snapshot);
    }
}
