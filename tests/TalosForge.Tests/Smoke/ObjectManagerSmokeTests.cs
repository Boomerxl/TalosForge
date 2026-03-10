using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TalosForge.Core;
using TalosForge.Core.ObjectManager;
using Xunit;

namespace TalosForge.Tests.Smoke;

public sealed class ObjectManagerSmokeTests
{
    [Fact]
    public void Live_ObjectScan_Does_Not_Crash_When_Wow_Is_Running()
    {
        if (!Process.GetProcessesByName("Wow").Any())
        {
            return;
        }

        var reader = MemoryReader.Instance;
        if (!reader.Attach())
        {
            return;
        }

        var manager = new ObjectManagerService(reader, NullLogger<ObjectManagerService>.Instance);
        var snapshot = manager.GetSnapshot(100);

        Assert.NotNull(snapshot);
    }
}
