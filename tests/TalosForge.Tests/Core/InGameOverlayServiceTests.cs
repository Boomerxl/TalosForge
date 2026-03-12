using System.Text.Json;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Configuration;
using TalosForge.Core.Drawing;
using TalosForge.Core.Models;
using Xunit;

namespace TalosForge.Tests.Core;

public sealed class InGameOverlayServiceTests
{
    [Fact]
    public async Task TryPublishAsync_When_Disabled_Does_Not_Send_Command()
    {
        var client = new RecordingUnlockerClient();
        var options = new BotOptions
        {
            EnableInGameOverlay = false,
            InGameOverlayEveryTicks = 1,
        };
        var service = new InGameOverlayService(client, options);

        var commands = await service.TryPublishAsync(
            tickId: 10,
            state: BotState.Combat,
            snapshot: CreateSnapshot(success: true),
            queuedCommands: 2,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, commands);
        Assert.Empty(client.Commands);
    }

    [Fact]
    public async Task TryPublishAsync_Respects_Tick_Interval_After_First_Visible_Publish()
    {
        var client = new RecordingUnlockerClient();
        var options = new BotOptions
        {
            EnableInGameOverlay = true,
            InGameOverlayEveryTicks = 3,
        };
        var service = new InGameOverlayService(client, options);

        var firstCommands = await service.TryPublishAsync(
            tickId: 4,
            state: BotState.Idle,
            snapshot: CreateSnapshot(success: true),
            queuedCommands: 0,
            cancellationToken: CancellationToken.None);
        var secondCommands = await service.TryPublishAsync(
            tickId: 4,
            state: BotState.Idle,
            snapshot: CreateSnapshot(success: true),
            queuedCommands: 0,
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, firstCommands);
        Assert.Equal(0, secondCommands);
        Assert.Single(client.Commands);
    }

    [Fact]
    public async Task TryPublishAsync_Builds_Lua_Overlay_Command()
    {
        var client = new RecordingUnlockerClient();
        var options = new BotOptions
        {
            EnableInGameOverlay = true,
            InGameOverlayEveryTicks = 2,
        };
        var service = new InGameOverlayService(client, options);

        var commands = await service.TryPublishAsync(
            tickId: 6,
            state: BotState.Movement,
            snapshot: CreateSnapshot(success: true),
            queuedCommands: 5,
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, commands);
        var command = Assert.Single(client.Commands);
        Assert.Equal(UnlockerOpcode.LuaDoString, command.Opcode);

        using var payload = JsonDocument.Parse(command.PayloadJson);
        var lua = payload.RootElement.GetProperty("code").GetString();

        Assert.NotNull(lua);
        Assert.Contains("frame.TalosForgeText:SetText", lua!, StringComparison.Ordinal);
        Assert.Contains("TalosForge [ok] Tick:6 State:Movement Obj:2 Target:0x000000000000004D Cmd:5", lua!, StringComparison.Ordinal);
        Assert.Contains("TalosForgeDiag", lua!, StringComparison.Ordinal);
        Assert.Contains("LuaErr:", lua!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryPublishAsync_When_Snapshot_Invalid_Does_Not_Send_Command()
    {
        var client = new RecordingUnlockerClient();
        var options = new BotOptions
        {
            EnableInGameOverlay = true,
            InGameOverlayEveryTicks = 1,
        };
        var service = new InGameOverlayService(client, options);

        var commands = await service.TryPublishAsync(
            tickId: 7,
            state: BotState.Idle,
            snapshot: CreateSnapshot(success: false),
            queuedCommands: 0,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, commands);
        Assert.Empty(client.Commands);
    }

    [Fact]
    public async Task TryPublishAsync_When_Player_Missing_But_Snapshot_Succeeds_Does_Not_Send_Command()
    {
        var client = new RecordingUnlockerClient();
        var options = new BotOptions
        {
            EnableInGameOverlay = true,
            InGameOverlayEveryTicks = 1,
        };
        var service = new InGameOverlayService(client, options);

        var snapshot = new WorldSnapshot(
            TickId: 1,
            TimestampUtc: DateTimeOffset.UtcNow,
            Objects: Array.Empty<WowObjectSnapshot>(),
            Player: null,
            Success: true,
            ErrorMessage: null);

        var commands = await service.TryPublishAsync(
            tickId: 8,
            state: BotState.Idle,
            snapshot: snapshot,
            queuedCommands: 0,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, commands);
        Assert.Empty(client.Commands);
    }

    [Fact]
    public async Task TryPublishAsync_Hides_Overlay_Once_When_State_Becomes_Invalid()
    {
        var client = new RecordingUnlockerClient();
        var options = new BotOptions
        {
            EnableInGameOverlay = true,
            InGameOverlayEveryTicks = 1,
        };
        var service = new InGameOverlayService(client, options);

        var firstCommands = await service.TryPublishAsync(
            tickId: 1,
            state: BotState.Idle,
            snapshot: CreateSnapshot(success: true),
            queuedCommands: 0,
            cancellationToken: CancellationToken.None);
        var secondCommands = await service.TryPublishAsync(
            tickId: 2,
            state: BotState.Idle,
            snapshot: CreateSnapshot(success: false),
            queuedCommands: 0,
            cancellationToken: CancellationToken.None);
        var thirdCommands = await service.TryPublishAsync(
            tickId: 3,
            state: BotState.Idle,
            snapshot: CreateSnapshot(success: false),
            queuedCommands: 0,
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, firstCommands);
        Assert.Equal(1, secondCommands);
        Assert.Equal(0, thirdCommands);
        Assert.Equal(2, client.Commands.Count);

        using var payload = JsonDocument.Parse(client.Commands[1].PayloadJson);
        var lua = payload.RootElement.GetProperty("code").GetString();
        Assert.NotNull(lua);
        Assert.Contains("TalosForgeStatusFrame:Hide()", lua!, StringComparison.Ordinal);
    }

    private static WorldSnapshot CreateSnapshot(bool success)
    {
        return new WorldSnapshot(
            TickId: 1,
            TimestampUtc: DateTimeOffset.UtcNow,
            Objects:
            [
                new WowObjectSnapshot(IntPtr.Zero, 1, 4, new Vector3(1, 2, 3), 0f, true, null),
                new WowObjectSnapshot(IntPtr.Zero, 2, 5, new Vector3(4, 5, 6), 0f, false, null)
            ],
            Player: new PlayerSnapshot(
                Guid: 77,
                Position: new Vector3(10, 20, 30),
                Facing: 1.25f,
                TargetGuid: 77,
                InCombat: false,
                IsCasting: false,
                LootReady: false,
                IsMoving: true),
            Success: success,
            ErrorMessage: success ? null : "snapshot failed");
    }

    private sealed class RecordingUnlockerClient : IUnlockerClient
    {
        public List<UnlockerCommand> Commands { get; } = new();

        public Task<UnlockerAck> SendAsync(UnlockerCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(new UnlockerAck(command.CommandId, true, "ACK:LuaDoString", command.PayloadJson, DateTimeOffset.UtcNow));
        }

        public void Dispose()
        {
        }
    }
}
