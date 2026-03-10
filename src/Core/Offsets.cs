namespace TalosForge.Core
{
    /// <summary>
    /// Offsets for WoW 3.3.5a (build 12340), confirmed from verified open-source references.
    /// </summary>
    public static class Offsets
    {
        // STATIC_CLIENT_CONNECTION = 0x00C79CE0
        public const int STATIC_CLIENT_CONNECTION = 0x00C79CE0;

        // OBJECT_MANAGER_OFFSET = 0x2ED0 (relative)
        public const int OBJECT_MANAGER_OFFSET = 0x2ED0;

        // FIRST_OBJECT_OFFSET = 0xAC
        public const int FIRST_OBJECT_OFFSET = 0x00AC;

        // NEXT_OBJECT_OFFSET = 0x3C
        public const int NEXT_OBJECT_OFFSET = 0x003C;

        // OBJECT_GUID = 0x30
        public const int OBJECT_GUID = 0x0030;

        // OBJECT_TYPE = 0x14
        public const int OBJECT_TYPE = 0x0014;

        // OBJECT_POS_X = 0x79C, Y = 0x798, Z = 0x7A0
        public const int OBJECT_POS_X = 0x079C;
        public const int OBJECT_POS_Y = 0x0798;
        public const int OBJECT_POS_Z = 0x07A0;

        // OBJECT_ROTATION (Facing) = 0x7A8
        public const int OBJECT_ROTATION = 0x07A8;

        // LOCAL_GUID_OFFSET = 0xC0 (from ObjectManager)
        public const int LOCAL_GUID_OFFSET = 0x00C0;

        // LOCAL_TARGET_GUID_STATIC = 0x00BD07B0
        public const int LOCAL_TARGET_GUID_STATIC = 0x00BD07B0;

        // Camera chain: 0x00C7B5A8 + 0x6B04 + 0xE8 -> Yaw +0x30, Pitch +0x34
        public const int CAMERA_CHAIN_BASE = 0x00C7B5A8;
        public const int CAMERA_CHAIN_OFFSET_1 = 0x6B04;
        public const int CAMERA_CHAIN_OFFSET_2 = 0x00E8;
        public const int CAMERA_YAW_OFFSET = 0x0030;
        public const int CAMERA_PITCH_OFFSET = 0x0034;

        // Full unit/descriptor fields (health, powers, flags, auras, casting) available in reference.
    }
}
