using TalosForge.Core;
using Xunit;

namespace TalosForge.Tests.Smoke;

[Trait(LiveSmokePreconditions.TraitName, LiveSmokePreconditions.TraitValue)]
public sealed class AttachSmokeTests
{
    [LiveWowFact]
    public void Live_Attach_Succeeds_When_Wow_Is_Running()
    {
        _ = LiveSmokePreconditions.RequireWowProcess();

        var reader = MemoryReader.Instance;
        var attached = reader.Attach();

        Assert.True(attached);
        Assert.True(reader.IsAttached);
        Assert.NotEqual(IntPtr.Zero, reader.BaseAddress);
    }
}
