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
    private static readonly TimeSpan LocalPlayerCacheTtl = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PointerCacheTtl = TimeSpan.FromSeconds(2);

    private readonly IMemoryReader _memoryReader;
    private readonly ILogger<ObjectManagerService> _logger;
    private readonly object _localPlayerLock = new();
    private readonly object _pointerCacheLock = new();
    private PlayerSnapshot? _cachedLocalPlayer;
    private DateTimeOffset _cachedLocalPlayerExpiresUtc = DateTimeOffset.MinValue;
    private IntPtr _cachedClientConnection;
    private IntPtr _cachedObjectManager;
    private DateTimeOffset _cachedPointerChainExpiresUtc = DateTimeOffset.MinValue;

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
            var hasValidClientConnection = clientConnection != IntPtr.Zero && IsLikelyPointer(clientConnection);
            if (!hasValidClientConnection && TryGetCachedPointerChain(out var cachedClientConnection, out _))
            {
                clientConnection = cachedClientConnection;
                hasValidClientConnection = clientConnection != IntPtr.Zero && IsLikelyPointer(clientConnection);
            }

            if (!hasValidClientConnection)
            {
                return WorldSnapshot.Empty(
                    tickId,
                    $"Client connection pointer is invalid (0x{clientConnection.ToInt64():X}).");
            }

            var objectManagerAddress = IntPtr.Add(clientConnection, Offsets.OBJECT_MANAGER_OFFSET);
            var hasValidObjectManager = TryReadPointer(objectManagerAddress, out var objectManagerPointer) &&
                                        IsLikelyPointer(objectManagerPointer);
            if (!hasValidObjectManager && TryGetCachedPointerChain(out _, out var cachedObjectManager))
            {
                objectManagerPointer = cachedObjectManager;
                hasValidObjectManager = objectManagerPointer != IntPtr.Zero && IsLikelyPointer(objectManagerPointer);
            }

            if (!hasValidObjectManager)
            {
                return WorldSnapshot.Empty(
                    tickId,
                    $"Object manager pointer is invalid at 0x{objectManagerAddress.ToInt64():X}.");
            }

            CachePointerChain(clientConnection, objectManagerPointer);

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

                if (!TryReadNextObjectPointer(current, out current))
                {
                    break;
                }
            }

            var localObject = objects.FirstOrDefault(o => o.IsLocalPlayer);
            PlayerSnapshot? player = null;
            if (localObject != null)
            {
                var runtimeData = ReadLocalPlayerRuntimeData(localObject.Pointer);
                player = new PlayerSnapshot(
                    localObject.Guid,
                    localObject.Position,
                    localObject.Facing,
                    localObject.TargetGuid ?? targetGuid,
                    runtimeData.InCombat,
                    runtimeData.IsCasting,
                    LootReady: false,
                    IsMoving: false,
                    runtimeData.Health,
                    runtimeData.MaxHealth,
                    Mana: runtimeData.Mana,
                    MaxMana: runtimeData.MaxMana,
                    Level: runtimeData.Level,
                    Name: localObject.Name,
                    Auras: localObject.Auras);

                CacheLocalPlayer(player);
            }
            else
            {
                InvalidateLocalPlayerCache();
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

    /// <summary>
    /// Returns local player from a 200ms cache window, refreshing via a snapshot when stale.
    /// </summary>
    public PlayerSnapshot? GetLocalPlayer(long tickId)
    {
        if (TryGetCachedLocalPlayer(out var player))
        {
            return player;
        }

        var snapshot = GetSnapshot(tickId);
        if (!snapshot.Success || snapshot.Player == null)
        {
            return null;
        }

        return snapshot.Player;
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
            ulong guid;
            int type;
            float x;
            float y;
            float z;
            float facing;
            ulong? objectTargetGuid = targetGuid;

            if (TryRead(objectPointer, out CGObject cgObject))
            {
                guid = cgObject.Guid;
                type = cgObject.TypeId;
            }
            else
            {
                guid = _memoryReader.Read<ulong>(IntPtr.Add(objectPointer, Offsets.OBJECT_GUID));
                type = _memoryReader.Read<int>(IntPtr.Add(objectPointer, Offsets.OBJECT_TYPE));
            }

            uint? health = null, maxHealth = null, mana = null, maxMana = null;
            int? level = null;
            uint? entryId = null, unitFlags = null, dynamicFlags = null;
            int? factionTemplate = null;
            string? name = null;
            IReadOnlyList<AuraInfo>? auras = null;

            if (type == (int)WowObjectType.Unit || type == (int)WowObjectType.Player)
            {
                if (TryRead(objectPointer, out CGUnit cgUnit))
                {
                    x = cgUnit.PositionX;
                    y = cgUnit.PositionY;
                    z = cgUnit.PositionZ;
                    facing = cgUnit.Facing;
                    health = cgUnit.Health;
                    maxHealth = cgUnit.MaxHealth;
                    mana = cgUnit.Mana;
                    maxMana = cgUnit.MaxMana;
                }
                else
                {
                    x = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_POS_X));
                    y = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_POS_Y));
                    z = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_POS_Z));
                    facing = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_ROTATION));
                }

                TryReadDescriptor(objectPointer, Offsets.DESC_OBJECT_ENTRY, out uint rawEntry);
                if (rawEntry != 0) entryId = rawEntry;

                TryReadDescriptor(objectPointer, Offsets.DESC_UNIT_LEVEL, out int rawLevel);
                if (rawLevel > 0 && rawLevel <= 255) level = rawLevel;

                TryReadDescriptor(objectPointer, Offsets.DESC_UNIT_FLAGS, out uint rawFlags);
                unitFlags = rawFlags;

                TryReadDescriptor(objectPointer, Offsets.DESC_UNIT_DYNAMIC_FLAGS, out uint rawDynFlags);
                dynamicFlags = rawDynFlags;

                TryReadDescriptor(objectPointer, Offsets.DESC_UNIT_FACTION_TEMPLATE, out int rawFaction);
                if (rawFaction != 0) factionTemplate = rawFaction;

                name = TryReadUnitName(objectPointer, type);
                auras = AuraReader.ReadAuras(_memoryReader, objectPointer);

                if (type == (int)WowObjectType.Player && TryRead(objectPointer, out CGPlayer cgPlayer) && cgPlayer.TargetGuid != 0)
                {
                    objectTargetGuid = cgPlayer.TargetGuid;
                }
            }
            else
            {
                x = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_POS_X));
                y = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_POS_Y));
                z = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_POS_Z));
                facing = _memoryReader.Read<float>(IntPtr.Add(objectPointer, Offsets.OBJECT_ROTATION));
            }

            bool isDead = health.HasValue && health.Value == 0;
            if (!isDead && dynamicFlags.HasValue)
            {
                isDead = (dynamicFlags.Value & Offsets.UNIT_DYNAMIC_FLAG_DEAD) != 0;
            }

            snapshot = new WowObjectSnapshot(
                objectPointer,
                guid,
                type,
                new Vector3(x, y, z),
                facing,
                guid == localGuid,
                objectTargetGuid,
                Health: health,
                MaxHealth: maxHealth,
                Mana: mana,
                MaxMana: maxMana,
                Level: level,
                EntryId: entryId,
                IsDead: isDead,
                UnitFlags: unitFlags,
                DynamicFlags: dynamicFlags,
                Name: name,
                FactionTemplate: factionTemplate,
                Auras: auras);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skipping unreadable object at {Pointer}", objectPointer);
            return false;
        }
    }

    private LocalPlayerRuntimeData ReadLocalPlayerRuntimeData(IntPtr objectPointer)
    {
        int? health = null;
        int? maxHealth = null;
        int? mana = null;
        int? maxMana = null;
        int? level = null;
        var inCombat = false;
        var isCasting = false;

        if (TryRead(objectPointer, out CGPlayer cgPlayer))
        {
            health = NormalizeOptionalValue(cgPlayer.DescriptorHealth);
            maxHealth = NormalizeOptionalValue(cgPlayer.DescriptorMaxHealth);
            mana = NormalizeOptionalValue(cgPlayer.DescriptorMana);
            maxMana = NormalizeOptionalValue(cgPlayer.DescriptorMaxMana);
            level = cgPlayer.Level > 0 ? cgPlayer.Level : null;
            inCombat = cgPlayer.Base.CombatFlag != 0;
            isCasting = IsCasting(cgPlayer.Base.SpellCastStartMs, cgPlayer.Base.SpellCastEndMs);
        }
        else if (TryRead(objectPointer, out CGUnit cgUnit))
        {
            health = (int?)NormalizeOptionalValue((int)cgUnit.Health);
            maxHealth = (int?)NormalizeOptionalValue((int)cgUnit.MaxHealth);
            mana = (int?)NormalizeOptionalValue((int)cgUnit.Mana);
            maxMana = (int?)NormalizeOptionalValue((int)cgUnit.MaxMana);
            inCombat = cgUnit.CombatFlag != 0;
            isCasting = IsCasting(cgUnit.SpellCastStartMs, cgUnit.SpellCastEndMs);
        }

        if (health == null)
        {
            health = TryReadOptionalInt(IntPtr.Add(objectPointer, Offsets.PLAYER_DESCRIPTOR_HEALTH));
        }

        if (maxHealth == null)
        {
            maxHealth = TryReadOptionalInt(IntPtr.Add(objectPointer, Offsets.PLAYER_DESCRIPTOR_MAX_HEALTH));
        }

        if (TryRead(IntPtr.Add(objectPointer, Offsets.UNIT_COMBAT_FLAG), out int combatFlag))
        {
            inCombat = combatFlag != 0;
        }

        if (TryRead(IntPtr.Add(objectPointer, Offsets.UNIT_SPELL_CAST_START_MS), out int castStartMs) &&
            TryRead(IntPtr.Add(objectPointer, Offsets.UNIT_SPELL_CAST_END_MS), out int castEndMs))
        {
            isCasting = IsCasting(castStartMs, castEndMs);
        }

        return new LocalPlayerRuntimeData(health, maxHealth, mana, maxMana, level, inCombat, isCasting);
    }

    private bool TryReadDescriptor<T>(IntPtr objectPointer, int descriptorOffset, out T value) where T : struct
    {
        value = default;
        try
        {
            var descriptorBase = _memoryReader.ReadPointer(IntPtr.Add(objectPointer, Offsets.OBJECT_DESCRIPTOR_PTR));
            if (!IsLikelyPointer(descriptorBase))
                return false;
            value = _memoryReader.Read<T>(IntPtr.Add(descriptorBase, descriptorOffset));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string? TryReadUnitName(IntPtr objectPointer, int objectType)
    {
        try
        {
            if (objectType == (int)WowObjectType.Unit)
            {
                var nameInfoPtr = _memoryReader.ReadPointer(IntPtr.Add(objectPointer, Offsets.UNIT_NAME_INFO_PTR));
                if (IsLikelyPointer(nameInfoPtr))
                {
                    var namePtr = _memoryReader.ReadPointer(IntPtr.Add(nameInfoPtr, Offsets.UNIT_NAME_STRING_OFFSET));
                    if (IsLikelyPointer(namePtr))
                    {
                        var name = _memoryReader.ReadString(namePtr, 64);
                        if (!string.IsNullOrWhiteSpace(name))
                            return name;
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private bool TryReadNextObjectPointer(IntPtr objectPointer, out IntPtr nextObjectPointer)
    {
        // Prefer the raw link field over full-struct reads. In live WoW sessions the
        // larger CGObject overlay can intermittently fail across page boundaries even
        // when the next-object link at +0x3C is still readable.
        return TryReadPointer(IntPtr.Add(objectPointer, Offsets.NEXT_OBJECT_OFFSET), out nextObjectPointer);
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

    private static bool IsCasting(int castStartMs, int castEndMs)
    {
        return castEndMs > 0 && castEndMs >= castStartMs;
    }

    private int? TryReadOptionalInt(IntPtr address)
    {
        if (TryRead(address, out int value))
        {
            return NormalizeOptionalValue(value);
        }

        return null;
    }

    private static int? NormalizeOptionalValue(int value)
    {
        return value >= 0 ? value : null;
    }

    private void CacheLocalPlayer(PlayerSnapshot player)
    {
        lock (_localPlayerLock)
        {
            _cachedLocalPlayer = player;
            _cachedLocalPlayerExpiresUtc = DateTimeOffset.UtcNow.Add(LocalPlayerCacheTtl);
        }
    }

    private void InvalidateLocalPlayerCache()
    {
        lock (_localPlayerLock)
        {
            _cachedLocalPlayer = null;
            _cachedLocalPlayerExpiresUtc = DateTimeOffset.MinValue;
        }
    }

    private bool TryGetCachedLocalPlayer(out PlayerSnapshot? player)
    {
        lock (_localPlayerLock)
        {
            if (_cachedLocalPlayer != null && DateTimeOffset.UtcNow <= _cachedLocalPlayerExpiresUtc)
            {
                player = _cachedLocalPlayer;
                return true;
            }

            player = null;
            return false;
        }
    }

    private void CachePointerChain(IntPtr clientConnection, IntPtr objectManagerPointer)
    {
        lock (_pointerCacheLock)
        {
            _cachedClientConnection = clientConnection;
            _cachedObjectManager = objectManagerPointer;
            _cachedPointerChainExpiresUtc = DateTimeOffset.UtcNow.Add(PointerCacheTtl);
        }
    }

    private bool TryGetCachedPointerChain(out IntPtr clientConnection, out IntPtr objectManagerPointer)
    {
        lock (_pointerCacheLock)
        {
            if (DateTimeOffset.UtcNow <= _cachedPointerChainExpiresUtc &&
                _cachedClientConnection != IntPtr.Zero &&
                _cachedObjectManager != IntPtr.Zero)
            {
                clientConnection = _cachedClientConnection;
                objectManagerPointer = _cachedObjectManager;
                return true;
            }

            clientConnection = IntPtr.Zero;
            objectManagerPointer = IntPtr.Zero;
            return false;
        }
    }

    private static IntPtr ToAbsoluteAddress(int offset)
    {
        return new IntPtr(unchecked((int)(uint)offset));
    }

    private sealed record LocalPlayerRuntimeData(
        int? Health,
        int? MaxHealth,
        int? Mana,
        int? MaxMana,
        int? Level,
        bool InCombat,
        bool IsCasting);
}
