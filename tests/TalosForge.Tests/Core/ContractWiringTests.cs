using TalosForge.Core;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Configuration;
using TalosForge.Core.Models;
using Xunit;

namespace TalosForge.Tests.Core;

public sealed class ContractWiringTests
{
    [Fact]
    public void MemoryReader_Implements_Interface()
    {
        IMemoryReader reader = MemoryReader.Instance;
        Assert.NotNull(reader);
    }

    [Fact]
    public void BotOptions_Defaults_Are_Initialized()
    {
        var options = new BotOptions();

        Assert.Equal("Wow", options.ProcessName);
        Assert.Equal(35, options.CombatTickMs);
        Assert.Equal(70, options.MovementTickMs);
        Assert.Equal(120, options.IdleTickMs);
        Assert.Equal("TalosForge.Cmd.v1", options.CommandMmfName);
    }

    [Fact]
    public void UnlockerCommand_Record_Is_Usable()
    {
        var command = new UnlockerCommand(1, UnlockerOpcode.LuaDoString, "{}", DateTimeOffset.UtcNow);
        Assert.Equal(UnlockerOpcode.LuaDoString, command.Opcode);
    }
}
