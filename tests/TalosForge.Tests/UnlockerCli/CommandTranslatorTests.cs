using TalosForge.UnlockerCli;
using Xunit;

namespace TalosForge.Tests.UnlockerCli;

public sealed class CommandTranslatorTests
{
    [Fact]
    public void TryTranslate_Lua_Succeeds()
    {
        var ok = CommandTranslator.TryTranslate(
            "lua",
            new[] { "print('ok')" },
            out var command,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("ACK:LuaDoString", command.AckMessage);
        Assert.Equal("print('ok')", command.LuaCode);
    }

    [Fact]
    public void TryTranslate_Cast_Escapes_Single_Quotes()
    {
        var ok = CommandTranslator.TryTranslate(
            "cast",
            new[] { "Kidney Shot's Edge" },
            out var command,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("ACK:CastSpellByName", command.AckMessage);
        Assert.Contains("CastSpellByName('Kidney Shot\\'s Edge')", command.LuaCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslate_Face_Invalid_Number_Fails()
    {
        var ok = CommandTranslator.TryTranslate(
            "face",
            new[] { "abc", "0.2" },
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("face verb requires finite numbers", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslate_MoveTo_Formats_With_Invariant_Culture()
    {
        var ok = CommandTranslator.TryTranslate(
            "moveto",
            new[] { "1.25", "2.5", "3.75", "0.35" },
            out var command,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("ACK:MoveTo", command.AckMessage);
        Assert.Contains("MoveTo(1.25,2.5,3.75,0.35)", command.LuaCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslate_Stop_Succeeds()
    {
        var ok = CommandTranslator.TryTranslate(
            "stop",
            Array.Empty<string>(),
            out var command,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("ACK:Stop", command.AckMessage);
        Assert.Contains("MoveForwardStop", command.LuaCode, StringComparison.Ordinal);
    }
}
