using Microsoft.Extensions.Logging;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;

namespace TalosForge.Core.ObjectManager;

/// <summary>
/// Reads world objects from WoW's object manager chain.
/// </summary>
public sealed class ObjectManagerService : IObjectManager
{
    private const int MaxObjects = 16_384;

    private readonly IMemoryReader _memoryReader;
    private readonly ILogger<ObjectManagerService> _logger;

    public ObjectManagerService(IMemoryReader memoryReader, ILogger<ObjectManagerService> logger)
    {
        _memoryReader = memoryReader;
        _logger = logger;
    }

    public WorldSnapshot GetSnapshot(long tickId)
    {
        try
        {
            if (!_memoryReader.IsAttached && !_memoryReader.Attach())
            {
                return WorldSnapshot.Empty(tickId, "WoW process not found.");
            }

            var baseAddress = _memoryReader.BaseAddress;
            if (baseAddress == IntPtr.Zero)
            {
                return WorldSnapshot.Empty(tickId, "BaseAddress is zero.");
            }

            var clientConnection = _memoryReader.ReadPointer(IntPtr.Add(baseAddress, Offsets.STATIC_CLIENT_CONNECTION));
            if (clientConnection == IntPtr.Zero)
            {
                return WorldSnapshot.Empty(tickId, "Client connection pointer is zero.");
            }

            var objectManagerPointer = _memoryReader.ReadPointer(IntPtr.Add(clientConnection, Offsets.OBJECT_MANAGER_OFFSET));
            if (objectManagerPointer == IntPtr.Zero)
            {
                return WorldSnapshot.Empty(tickId, "Object manager pointer is zero.");
            }

            var localGuid = _memoryReader.Read<ulong>(IntPtr.Add(objectManagerPointer, Offsets.LOCAL_GUID_OFFSET));
            var firstObject = _memoryReader.ReadPointer(IntPtr.Add(objectManagerPointer, Offsets.FIRST_OBJECT_OFFSET));
            var targetGuid = ReadOptionalTargetGuid(baseAddress);

            var objects = new List<WowObjectSnapshot>(256);
            var visited = new HashSet<IntPtr>();
            var current = firstObject;

            while (current != IntPtr.Zero && objects.Count < MaxObjects && visited.Add(current))
            {
                if (TryReadObject(current, localGuid, targetGuid, out var objectSnapshot))
                {
                    objects.Add(objectSnapshot!);
                }

                current = _memoryReader.ReadPointer(IntPtr.Add(current, Offsets.NEXT_OBJECT_OFFSET));
            }

            var localObject = objects.FirstOrDefault(o => o.IsLocalPlayer);
            PlayerSnapshot? player = null;
            if (localObject != null)
            {
                player = new PlayerSnapshot(
                    localObject.Guid,
                    localObject.Position,
                    localObject.Facing,
                    targetGuid,
                    InCombat: false,
                    IsCasting: false,
                    LootReady: false,
                    IsMoving: false);
            }

            return new WorldSnapshot(
                tickId,
                DateTimeOffset.UtcNow,
                objects,
                player,
                Success: true,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ObjectManager scan failed at tick {TickId}", tickId);
            return WorldSnapshot.Empty(tickId, ex.Message);
        }
    }

    private ulong? ReadOptionalTargetGuid(IntPtr baseAddress)
    {
        try
        {
            var targetGuid = _memoryReader.Read<ulong>(IntPtr.Add(baseAddress, Offsets.LOCAL_TARGET_GUID_STATIC));
            return targetGuid == 0 ? null : targetGuid;
        }
        catch
        {
            return null;
        }
    }

    private bool TryReadObject(IntPtr objectPointer, ulong localGuid, ulong? targetGuid, out WowObjectSnapshot? snapshot)
    {
        snapshot = null;

        try
        {
            var guid = _memoryReader.Read<ulong>(IntPtr.Add(objectPointer, Offsets.OBJECT_GUID));
            var type = _memoryReader.Read<int>(IntPtr.Add(objectPointer, Offsets.OBJECT_TYPE));
            var x = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_POS_X));
            var y = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_POS_Y));
            var z = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_POS_Z));
            var facing = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_ROTATION));

            snapshot = new WowObjectSnapshot(
                objectPointer,
                guid,
                type,
                new Vector3(x, y, z),
                facing,
                guid == localGuid,
                targetGuid);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skipping unreadable object at {Pointer}", objectPointer);
            return false;
        }
    }
}
