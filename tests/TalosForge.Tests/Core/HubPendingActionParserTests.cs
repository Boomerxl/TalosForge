using TalosForge.Core.Drawing;
using Xunit;

namespace TalosForge.Tests.Core;

public sealed class HubPendingActionParserTests
{
    [Fact]
    public void TryParse_Empty_Returns_False()
    {
        Assert.False(HubPendingActionParser.TryParse("", out _, out _));
        Assert.False(HubPendingActionParser.TryParse(null, out _, out _));
    }

    [Fact]
    public void TryParse_AckPrefix_Returns_False()
    {
        Assert.False(HubPendingActionParser.TryParse("ACK:LuaQuery", out _, out _));
    }

    [Fact]
    public void TryParse_Run_With_Payload()
    {
        var raw = "run" + '\u0001' + "print(1)";
        Assert.True(HubPendingActionParser.TryParse(raw, out var kind, out var payload));
        Assert.Equal("run", kind);
        Assert.Equal("print(1)", payload);
    }

    [Fact]
    public void TryParse_No_Delimiter_Returns_False()
    {
        Assert.False(HubPendingActionParser.TryParse("nodelim", out _, out _));
    }
}
