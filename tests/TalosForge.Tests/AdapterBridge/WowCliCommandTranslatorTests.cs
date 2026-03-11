using TalosForge.AdapterBridge.Execution;
using TalosForge.AdapterBridge.Models;
using Xunit;

namespace TalosForge.Tests.AdapterBridge;

public sealed class WowCliCommandTranslatorTests
{
    [Fact]
    public void TryTranslate_LuaDoString_Succeeds()
    {
        var request = new AdapterPipeRequest(
            1,
            1,
            "LuaDoString",
            1,
            "{\"code\":\"print('x')\"}",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var ok = WowCliCommandTranslator.TryTranslate(request, out var invocation, out var error);

        Assert.True(ok, error.Message);
        Assert.NotNull(invocation);
        Assert.Equal("lua", invocation!.Verb);
        Assert.Single(invocation.Arguments);
        Assert.Equal("print('x')", invocation.Arguments[0]);
    }

    [Fact]
    public void TryTranslate_Stop_With_Payload_Fails()
    {
        var request = new AdapterPipeRequest(
            1,
            1,
            "Stop",
            7,
            "{\"bad\":true}",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var ok = WowCliCommandTranslator.TryTranslate(request, out _, out var error);

        Assert.False(ok);
        Assert.Equal("BRIDGE_WOWCLI_INVALID_PAYLOAD", error.Code);
    }

    [Fact]
    public void TryTranslate_SetTargetGuid_Hex_String_Succeeds()
    {
        var request = new AdapterPipeRequest(
            1,
            1,
            "SetTargetGuid",
            3,
            "{\"guid\":\"0x10\"}",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var ok = WowCliCommandTranslator.TryTranslate(request, out var invocation, out var error);

        Assert.True(ok, error.Message);
        Assert.NotNull(invocation);
        Assert.Equal("target", invocation!.Verb);
        Assert.Single(invocation.Arguments);
        Assert.Equal("16", invocation.Arguments[0]);
    }
}
