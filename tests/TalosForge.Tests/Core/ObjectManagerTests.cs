using Microsoft.Extensions.Logging.Abstractions;
using TalosForge.Core;
using TalosForge.Core.ObjectManager;
using TalosForge.Tests.Core.Fakes;
using Xunit;

namespace TalosForge.Tests.Core;

public sealed class ObjectManagerTests
{
    [Fact]
    public void GetSnapshot_Returns_Parsed_Objects_And_Player()
    {
        var reader = new FakeMemoryReader { BaseAddress = new IntPtr(0x400000) };

        var clientConnection = new IntPtr(0x500000);
        var objectManager = new IntPtr(0x600000);
        var objectOne = new IntPtr(0x700000);
        var objectTwo = new IntPtr(0x710000);

        const ulong localGuid = 0x1111222233334444;
        const ulong otherGuid = 0xAAAABBBBCCCCDDDD;
        const ulong targetGuid = 0x0102030405060708;

        reader.Set(IntPtr.Add(reader.BaseAddress, Offsets.STATIC_CLIENT_CONNECTION), (uint)clientConnection.ToInt64());
        reader.Set(IntPtr.Add(clientConnection, Offsets.OBJECT_MANAGER_OFFSET), (uint)objectManager.ToInt64());
        reader.Set(IntPtr.Add(objectManager, Offsets.LOCAL_GUID_OFFSET), localGuid);
        reader.Set(IntPtr.Add(objectManager, Offsets.FIRST_OBJECT_OFFSET), (uint)objectOne.ToInt64());
        reader.Set(IntPtr.Add(reader.BaseAddress, Offsets.LOCAL_TARGET_GUID_STATIC), targetGuid);

        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_GUID), localGuid);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_TYPE), (int)WowObjectType.Player);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_POS_X), 100f);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_POS_Y), 200f);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_POS_Z), 300f);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_ROTATION), 1.25f);
        reader.Set(IntPtr.Add(objectOne, Offsets.NEXT_OBJECT_OFFSET), (uint)objectTwo.ToInt64());

        reader.Set(IntPtr.Add(objectTwo, Offsets.OBJECT_GUID), otherGuid);
        reader.Set(IntPtr.Add(objectTwo, Offsets.OBJECT_TYPE), (int)WowObjectType.Unit);
        reader.Set(IntPtr.Add(objectTwo, Offsets.OBJECT_POS_X), 101f);
        reader.Set(IntPtr.Add(objectTwo, Offsets.OBJECT_POS_Y), 201f);
        reader.Set(IntPtr.Add(objectTwo, Offsets.OBJECT_POS_Z), 301f);
        reader.Set(IntPtr.Add(objectTwo, Offsets.OBJECT_ROTATION), 2.25f);
        reader.Set(IntPtr.Add(objectTwo, Offsets.NEXT_OBJECT_OFFSET), 0u);

        var manager = new ObjectManagerService(reader, NullLogger<ObjectManagerService>.Instance);
        var snapshot = manager.GetSnapshot(1);

        Assert.True(snapshot.Success);
        Assert.Equal(2, snapshot.Objects.Count);
        Assert.NotNull(snapshot.Player);
        Assert.Equal(localGuid, snapshot.Player!.Guid);
        Assert.Equal(targetGuid, snapshot.Player.TargetGuid);
        Assert.Null(snapshot.Player.Health);
        Assert.Null(snapshot.Player.MaxHealth);
        Assert.False(snapshot.Player.InCombat);
        Assert.False(snapshot.Player.IsCasting);
    }

    [Fact]
    public void GetSnapshot_Returns_Empty_When_Chain_Is_Invalid()
    {
        var reader = new FakeMemoryReader { BaseAddress = IntPtr.Zero };
        var manager = new ObjectManagerService(reader, NullLogger<ObjectManagerService>.Instance);

        var snapshot = manager.GetSnapshot(2);

        Assert.False(snapshot.Success);
        Assert.Empty(snapshot.Objects);
        Assert.NotNull(snapshot.ErrorMessage);
    }

    [Fact]
    public void GetLocalPlayer_Uses_200ms_Cache_Window()
    {
        var reader = new FakeMemoryReader { BaseAddress = new IntPtr(0x400000) };
        var clientConnection = new IntPtr(0x500000);
        var objectManager = new IntPtr(0x600000);
        var objectOne = new IntPtr(0x700000);
        const ulong localGuid = 0x1111222233334444;

        reader.Set(IntPtr.Add(reader.BaseAddress, Offsets.STATIC_CLIENT_CONNECTION), (uint)clientConnection.ToInt64());
        reader.Set(IntPtr.Add(clientConnection, Offsets.OBJECT_MANAGER_OFFSET), (uint)objectManager.ToInt64());
        reader.Set(IntPtr.Add(objectManager, Offsets.LOCAL_GUID_OFFSET), localGuid);
        reader.Set(IntPtr.Add(objectManager, Offsets.FIRST_OBJECT_OFFSET), (uint)objectOne.ToInt64());

        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_GUID), localGuid);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_TYPE), (int)WowObjectType.Player);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_POS_X), 100f);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_POS_Y), 200f);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_POS_Z), 300f);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_ROTATION), 1.25f);
        reader.Set(IntPtr.Add(objectOne, Offsets.NEXT_OBJECT_OFFSET), 0u);

        var manager = new ObjectManagerService(reader, NullLogger<ObjectManagerService>.Instance);
        var firstSnapshot = manager.GetSnapshot(1);
        Assert.True(firstSnapshot.Success);
        Assert.NotNull(firstSnapshot.Player);

        // Break the chain; cached local player should still be returned immediately.
        reader.Set(IntPtr.Add(reader.BaseAddress, Offsets.STATIC_CLIENT_CONNECTION), 0u);

        var cachedPlayer = manager.GetLocalPlayer(2);
        Assert.NotNull(cachedPlayer);
        Assert.Equal(localGuid, cachedPlayer!.Guid);
    }

    [Fact]
    public void GetSnapshot_LocalPlayer_Populates_DescriptorBacked_Runtime_Data()
    {
        var reader = new FakeMemoryReader { BaseAddress = new IntPtr(0x400000) };

        var clientConnection = new IntPtr(0x500000);
        var objectManager = new IntPtr(0x600000);
        var objectOne = new IntPtr(0x700000);
        const ulong localGuid = 0x1111222233334444;

        reader.Set(IntPtr.Add(reader.BaseAddress, Offsets.STATIC_CLIENT_CONNECTION), (uint)clientConnection.ToInt64());
        reader.Set(IntPtr.Add(clientConnection, Offsets.OBJECT_MANAGER_OFFSET), (uint)objectManager.ToInt64());
        reader.Set(IntPtr.Add(objectManager, Offsets.LOCAL_GUID_OFFSET), localGuid);
        reader.Set(IntPtr.Add(objectManager, Offsets.FIRST_OBJECT_OFFSET), (uint)objectOne.ToInt64());

        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_GUID), localGuid);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_TYPE), (int)WowObjectType.Player);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_POS_X), 100f);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_POS_Y), 200f);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_POS_Z), 300f);
        reader.Set(IntPtr.Add(objectOne, Offsets.OBJECT_ROTATION), 1.25f);
        reader.Set(IntPtr.Add(objectOne, Offsets.PLAYER_DESCRIPTOR_HEALTH), 1234);
        reader.Set(IntPtr.Add(objectOne, Offsets.PLAYER_DESCRIPTOR_MAX_HEALTH), 2345);
        reader.Set(IntPtr.Add(objectOne, Offsets.UNIT_COMBAT_FLAG), 1);
        reader.Set(IntPtr.Add(objectOne, Offsets.UNIT_SPELL_CAST_START_MS), 100);
        reader.Set(IntPtr.Add(objectOne, Offsets.UNIT_SPELL_CAST_END_MS), 250);
        reader.Set(IntPtr.Add(objectOne, Offsets.NEXT_OBJECT_OFFSET), 0u);

        var manager = new ObjectManagerService(reader, NullLogger<ObjectManagerService>.Instance);
        var snapshot = manager.GetSnapshot(5);

        Assert.True(snapshot.Success);
        Assert.NotNull(snapshot.Player);
        Assert.Equal(1234, snapshot.Player!.Health);
        Assert.Equal(2345, snapshot.Player.MaxHealth);
        Assert.True(snapshot.Player.InCombat);
        Assert.True(snapshot.Player.IsCasting);
    }
}
