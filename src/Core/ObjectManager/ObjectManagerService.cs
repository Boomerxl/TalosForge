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
    private const long MinValidPointer = 0x10000;
    private const long MaxValidPointer = 0x7FFFFFFF;

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

            var clientConnection = ResolveStaticPointer(
                baseAddress,
                Offsets.STATIC_CLIENT_CONNECTION,
                "client connection");
            if (clientConnection == IntPtr.Zero || !IsLikelyPointer(clientConnection))
            {
                return WorldSnapshot.Empty(
                    tickId,
                    $"Client connection pointer is invalid (0x{clientConnection.ToInt64():X}).");
            }

            var objectManagerAddress = IntPtr.Add(clientConnection, Offsets.OBJECT_MANAGER_OFFSET);
            if (!TryReadPointer(objectManagerAddress, out var objectManagerPointer) || !IsLikelyPointer(objectManagerPointer))
            {
                return WorldSnapshot.Empty(
                    tickId,
                    $"Object manager pointer is invalid at 0x{objectManagerAddress.ToInt64():X}.");
            }

            if (!TryRead(IntPtr.Add(objectManagerPointer, Offsets.LOCAL_GUID_OFFSET), out ulong localGuid))
            {
                return WorldSnapshot.Empty(tickId, "Unable to read local GUID.");
            }

            if (!TryReadPointer(IntPtr.Add(objectManagerPointer, Offsets.FIRST_OBJECT_OFFSET), out var firstObject))
            {
                return WorldSnapshot.Empty(tickId, "Unable to read first object pointer.");
            }

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

                if (!TryReadPointer(IntPtr.Add(current, Offsets.NEXT_OBJECT_OFFSET), out current))
                {
                    break;
                }
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
        var absoluteAddress = ToAbsoluteAddress(Offsets.LOCAL_TARGET_GUID_STATIC);
        if (TryRead(absoluteAddress, out ulong absoluteTarget) && absoluteTarget != 0)
        {
            return absoluteTarget;
        }

        var baseRelativeAddress = IntPtr.Add(baseAddress, Offsets.LOCAL_TARGET_GUID_STATIC);
        if (TryRead(baseRelativeAddress, out ulong baseRelativeTarget) && baseRelativeTarget != 0)
        {
            return baseRelativeTarget;
        }

        return null;
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

    private IntPtr ResolveStaticPointer(IntPtr baseAddress, int staticOffset, string label)
    {
        // Most 3.3.5a offsets are absolute virtual addresses; keep base-relative fallback.
        var absoluteAddress = ToAbsoluteAddress(staticOffset);
        if (TryReadPointer(absoluteAddress, out var absolutePointer))
        {
            _logger.LogDebug(
                "Resolved {Label} from absolute address 0x{Address:X} => 0x{Pointer:X}",
                label,
                absoluteAddress.ToInt64(),
                absolutePointer.ToInt64());
            return absolutePointer;
        }

        var baseRelativeAddress = IntPtr.Add(baseAddress, staticOffset);
        if (TryReadPointer(baseRelativeAddress, out var baseRelativePointer))
        {
            _logger.LogDebug(
                "Resolved {Label} from base-relative address 0x{Address:X} => 0x{Pointer:X}",
                label,
                baseRelativeAddress.ToInt64(),
                baseRelativePointer.ToInt64());
            return baseRelativePointer;
        }

        return IntPtr.Zero;
    }

    private bool TryReadPointer(IntPtr address, out IntPtr pointer)
    {
        pointer = IntPtr.Zero;
        try
        {
            pointer = _memoryReader.ReadPointer(address);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryRead<T>(IntPtr address, out T value) where T : struct
    {
        value = default;
        try
        {
            value = _memoryReader.Read<T>(address);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyPointer(IntPtr pointer)
    {
        var value = pointer.ToInt64();
        return value >= MinValidPointer && value <= MaxValidPointer;
    }

    private static IntPtr ToAbsoluteAddress(int offset)
    {
        return new IntPtr(unchecked((int)(uint)offset));
    }
}
