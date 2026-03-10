using TalosForge.Core;
using Xunit;

namespace TalosForge.Tests.Core;

public sealed class PointerChainResolverTests
{
    [Fact]
    public void Resolve_Returns_Final_Address_For_Valid_Chain()
    {
        var pointers = new Dictionary<long, IntPtr>
        {
            [0x1010] = new IntPtr(0x2000),
            [0x2020] = new IntPtr(0x3000),
        };

        IntPtr Reader(IntPtr address) => pointers[address.ToInt64()];

        var resolved = PointerChainResolver.Resolve(new IntPtr(0x1000), Reader, 0x10, 0x20, 0x30);

        Assert.Equal(new IntPtr(0x3030), resolved);
    }

    [Fact]
    public void Resolve_Fails_Fast_When_Chain_Breaks()
    {
        var pointers = new Dictionary<long, IntPtr>
        {
            [0x1010] = IntPtr.Zero,
        };

        IntPtr Reader(IntPtr address) => pointers[address.ToInt64()];

        Assert.Throws<InvalidOperationException>(() =>
            PointerChainResolver.Resolve(new IntPtr(0x1000), Reader, 0x10, 0x20));
    }
}
