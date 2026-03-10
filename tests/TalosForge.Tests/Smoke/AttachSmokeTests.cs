using System.Diagnostics;
using TalosForge.Core;
using Xunit;

namespace TalosForge.Tests.Smoke;

public sealed class AttachSmokeTests
{
    [Fact]
    public void Live_Attach_Succeeds_When_Wow_Is_Running()
    {
        if (!Process.GetProcessesByName("Wow").Any())
        {
            return;
        }

        var reader = MemoryReader.Instance;
        var attached = reader.Attach();

        Assert.True(attached);
        Assert.True(reader.IsAttached);
        Assert.NotEqual(IntPtr.Zero, reader.BaseAddress);
    }
}
