namespace TalosForge.Core
{
    /// <summary>
    /// Offsets for WoW 3.3.5a (build 12340), confirmed from verified open-source references.
    /// </summary>
    public static class Offsets
    {
        public const int STATIC_CLIENT_CONNECTION = 0x00C79CE0;
        public const int OBJECT_MANAGER_OFFSET = 0x2ED0;
        public const int FIRST_OBJECT_OFFSET = 0x00AC;
        public const int NEXT_OBJECT_OFFSET = 0x003C;

        public const int OBJECT_GUID = 0x0030;
        public const int OBJECT_TYPE = 0x0014;
        public const int OBJECT_DESCRIPTOR_PTR = 0x0008;

        public const int OBJECT_POS_X = 0x079C;
        public const int OBJECT_POS_Y = 0x0798;
        public const int OBJECT_POS_Z = 0x07A0;
        public const int OBJECT_ROTATION = 0x07A8;

        public const int LOCAL_GUID_OFFSET = 0x00C0;
        public const int LOCAL_TARGET_GUID_STATIC = 0x00BD07B0;

        // Camera chain: 0x00C7B5A8 + 0x6B04 + 0xE8 -> Yaw +0x30, Pitch +0x34
        public const int CAMERA_CHAIN_BASE = 0x00C7B5A8;
        public const int CAMERA_CHAIN_OFFSET_1 = 0x6B04;
        public const int CAMERA_CHAIN_OFFSET_2 = 0x00E8;
        public const int CAMERA_YAW_OFFSET = 0x0030;
        public const int CAMERA_PITCH_OFFSET = 0x0034;

        // Unit runtime fields (absolute offsets within object struct, Binana layout)
        public const int UNIT_SPELL_CAST_START_MS = 0x0A78;
        public const int UNIT_SPELL_CAST_END_MS = 0x0A7C;
        public const int UNIT_COMBAT_FLAG = 0x0BEC;
        public const int PLAYER_DESCRIPTOR_HEALTH = 0x19B8;
        public const int PLAYER_DESCRIPTOR_MAX_HEALTH = 0x19D8;

        // Descriptor field offsets (byte offsets from the descriptor base at object+0x08)
        // Object fields
        public const int DESC_OBJECT_ENTRY = 0x000C;

        // Unit fields (from descriptor base)
        public const int DESC_UNIT_TARGET = 0x0048;
        public const int DESC_UNIT_BYTES_0 = 0x005C;
        public const int DESC_UNIT_HEALTH = 0x0060;
        public const int DESC_UNIT_POWER1 = 0x0064;
        public const int DESC_UNIT_MAXHEALTH = 0x0080;
        public const int DESC_UNIT_MAXPOWER1 = 0x0084;
        public const int DESC_UNIT_LEVEL = 0x00D8;
        public const int DESC_UNIT_FACTION_TEMPLATE = 0x00DC;
        public const int DESC_UNIT_FLAGS = 0x00EC;
        public const int DESC_UNIT_FLAGS_2 = 0x00F0;
        public const int DESC_UNIT_DYNAMIC_FLAGS = 0x013C;
        public const int DESC_UNIT_NPC_FLAGS = 0x0144;

        // Creature/NPC name reading: object+0x964 -> name info ptr, then +0x05C -> name string
        public const int UNIT_NAME_INFO_PTR = 0x0964;
        public const int UNIT_NAME_STRING_OFFSET = 0x005C;

        // Player name cache
        public const int PLAYER_NAME_CACHE_BASE = 0x00C0D788;
        public const int PLAYER_NAME_CACHE_NEXT = 0x000C;
        public const int PLAYER_NAME_CACHE_NAME = 0x0024;

        // Aura fields (within CGUnit struct)
        public const int UNIT_AURA_COUNT = 0x0DD0;
        public const int UNIT_AURA_TABLE_BASE = 0x0DD4;
        public const int UNIT_AURA_SORTED_COUNT = 0x0E54;
        public const int AURA_ENTRY_SIZE = 0x28;
        public const int AURA_SPELL_ID = 0x08;
        public const int AURA_CASTER_GUID = 0x18;
        public const int AURA_FLAGS = 0x10;
        public const int AURA_STACKS = 0x11;
        public const int AURA_LEVEL = 0x12;
        public const int AURA_DURATION = 0x24;
        public const int AURA_END_TIME = 0x20;
        public const int MAX_AURAS = 80;

        // Common unit flag constants
        public const uint UNIT_FLAG_NON_ATTACKABLE = 0x00000002;
        public const uint UNIT_FLAG_NOT_ATTACKABLE_1 = 0x00000100;
        public const uint UNIT_FLAG_IMMUNE_TO_PC = 0x00000200;
        public const uint UNIT_FLAG_IMMUNE_TO_NPC = 0x00000400;
        public const uint UNIT_FLAG_LOOTING = 0x00000800;
        public const uint UNIT_FLAG_IN_COMBAT = 0x00002000;
        public const uint UNIT_FLAG_SKINNABLE = 0x04000000;
        public const uint UNIT_FLAG_NOT_SELECTABLE = 0x02000000;

        public const uint UNIT_DYNAMIC_FLAG_LOOTABLE = 0x00000001;
        public const uint UNIT_DYNAMIC_FLAG_TAPPED = 0x00000004;
        public const uint UNIT_DYNAMIC_FLAG_TAPPED_BY_ME = 0x00000008;
        public const uint UNIT_DYNAMIC_FLAG_DEAD = 0x00000020;
    }
}
