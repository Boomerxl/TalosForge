using System.Runtime.InteropServices;
using TalosForge.Core.Abstractions;

namespace TalosForge.Core.Warden;

public enum WardenState { Unknown, NotLoaded, Loaded, Active }

public enum MemoryReaderMode { External, Internal }

public sealed record WardenSnapshot(
    WardenState State,
    string StateDetail,
    int RwxPageCount,
    int HiddenModuleCount,
    MemoryReaderMode ReaderMode,
    bool AgentPipeAlive,
    string AgentPipeName,
    List<CanaryAlert> CanaryAlerts,
    DateTimeOffset Timestamp);

public enum CanaryAlertType { FunctionHooked, PrologueChanged }

public sealed record CanaryAlert(
    CanaryAlertType Type,
    string Target,
    string Description);

/// <summary>
/// Passive Warden monitor inspired by WardenScanner.
/// Checks Warden state, RWX pages, hidden PE modules, and system function prologues.
/// All operations are read-only — zero writes to WoW memory.
/// </summary>
public sealed class WardenMonitor
{
    private const uint WardenStructurePTR = 0x00D31A4C;
    private const uint WardenVTableOffset = 0x228;
    private const uint ClientConnectionPtr = 0x00C79CE0;
    private const uint LoadWardenModuleAddr = 0x00872350;

    private const int PrologueSize = 8;

    private static readonly (string Module, string Function, uint KnownAddr)[] WatchList =
    [
        ("ntdll.dll", "NtQuerySystemInformation", 0),
        ("ntdll.dll", "NtQueryInformationProcess", 0),
        ("ntdll.dll", "NtGetContextThread", 0),
        ("kernel32.dll", "CreateToolhelp32Snapshot", 0),
        ("kernel32.dll", "IsDebuggerPresent", 0),
    ];

    private readonly IMemoryReader _reader;
    private readonly MemoryReaderMode _mode;
    private readonly Dictionary<string, byte[]> _baselinePrologues = new();
    private readonly Dictionary<string, uint> _resolvedAddresses = new();
    private bool _canaryInitialized;

    public WardenMonitor(IMemoryReader reader, MemoryReaderMode mode)
    {
        _reader = reader;
        _mode = mode;
    }

    public WardenSnapshot TakeSnapshot()
    {
        var (state, detail) = CheckWardenState();
        var rwxCount = 0;
        var hiddenModules = 0;

        if (_mode == MemoryReaderMode.External)
        {
            try
            {
                var handle = GetProcessHandle();
                if (handle != IntPtr.Zero)
                {
                    rwxCount = CountRwxPages(handle);
                    hiddenModules = CountHiddenModules(handle);
                }
            }
            catch { }
        }

        var canaryAlerts = CheckCanary();

        var pipeAlive = false;
        var pipeName = "N/A";
        try
        {
            var pid = _reader.WowProcess.Id;
            var discoveryPath = Path.Combine(Path.GetTempPath(), $"TalosForge.pipe.{pid}");
            if (File.Exists(discoveryPath))
            {
                pipeName = File.ReadAllText(discoveryPath).Trim();
                pipeAlive = !string.IsNullOrEmpty(pipeName);
                if (pipeAlive && pipeName.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
                    pipeName = pipeName[@"\\.\pipe\".Length..];
            }
        }
        catch { }

        return new WardenSnapshot(
            state, detail, rwxCount, hiddenModules,
            _mode, pipeAlive, pipeName,
            canaryAlerts, DateTimeOffset.UtcNow);
    }

    private (WardenState, string) CheckWardenState()
    {
        try
        {
            var testBytes = ReadUInt32(LoadWardenModuleAddr);
            if (testBytes == 0)
                return (WardenState.Unknown, "Cannot read WoW memory");

            var connPtr = ReadUInt32(ClientConnectionPtr);
            if (connPtr == 0)
                return (WardenState.NotLoaded, "Client not connected");

            var wardenPtr = ReadUInt32(WardenStructurePTR);
            if (wardenPtr == 0)
                return (WardenState.NotLoaded, "WardenPtr=0x00000000");

            var vtable = ReadUInt32(wardenPtr + WardenVTableOffset);
            if (vtable != 0)
                return (WardenState.Active,
                    $"WardenPtr=0x{wardenPtr:X8} VTable=0x{vtable:X8}");

            return (WardenState.Loaded,
                $"WardenPtr=0x{wardenPtr:X8} VTable=NULL (loaded, not yet active)");
        }
        catch (Exception ex)
        {
            return (WardenState.Unknown, $"Read error: {ex.Message}");
        }
    }

    public void InitializeCanary()
    {
        if (_mode != MemoryReaderMode.External)
            return;

        try
        {
            var handle = GetProcessHandle();
            if (handle == IntPtr.Zero) return;

            foreach (var (module, function, _) in WatchList)
            {
                var addr = ResolveWow64Export(handle, module, function);
                if (addr == 0) continue;

                var key = $"{module}!{function}";
                _resolvedAddresses[key] = addr;

                try
                {
                    var prologue = ReadBytes(addr, PrologueSize);
                    if (prologue != null)
                        _baselinePrologues[key] = prologue;
                }
                catch { }
            }

            _canaryInitialized = true;
        }
        catch { }
    }

    private List<CanaryAlert> CheckCanary()
    {
        var alerts = new List<CanaryAlert>();
        if (!_canaryInitialized) return alerts;

        foreach (var (name, addr) in _resolvedAddresses)
        {
            try
            {
                var current = ReadBytes(addr, PrologueSize);
                if (current == null) continue;

                if (_baselinePrologues.TryGetValue(name, out var baseline))
                {
                    if (current.AsSpan().SequenceEqual(baseline))
                        continue;

                    var hookType = DetectHook(current);
                    if (hookType != null)
                    {
                        alerts.Add(new CanaryAlert(
                            CanaryAlertType.FunctionHooked,
                            name,
                            $"{name} @ 0x{addr:X8} HOOKED ({hookType})"));
                    }
                    else
                    {
                        alerts.Add(new CanaryAlert(
                            CanaryAlertType.PrologueChanged,
                            name,
                            $"{name} prologue changed since startup"));
                    }

                    _baselinePrologues[name] = current;
                }
            }
            catch { }
        }

        return alerts;
    }

    private uint ReadUInt32(uint address)
    {
        return _reader.Read<uint>(new IntPtr(unchecked((int)address)));
    }

    private byte[]? ReadBytes(uint address, int size)
    {
        try
        {
            var result = new byte[size];
            for (var i = 0; i < size; i += 4)
            {
                var chunkSize = Math.Min(4, size - i);
                if (chunkSize == 4)
                {
                    var val = _reader.Read<uint>(new IntPtr(unchecked((int)(address + (uint)i))));
                    result[i] = (byte)(val & 0xFF);
                    result[i + 1] = (byte)((val >> 8) & 0xFF);
                    result[i + 2] = (byte)((val >> 16) & 0xFF);
                    result[i + 3] = (byte)((val >> 24) & 0xFF);
                }
                else
                {
                    for (var j = 0; j < chunkSize; j++)
                    {
                        result[i + j] = _reader.Read<byte>(
                            new IntPtr(unchecked((int)(address + (uint)i + (uint)j))));
                    }
                }
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private IntPtr GetProcessHandle()
    {
        try
        {
            var process = _reader.WowProcess;
            return process.Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static int CountRwxPages(IntPtr hProcess)
    {
        var count = 0;
        uint addr = 0x10000;

        while (addr < 0x7FFF0000)
        {
            var result = VirtualQueryEx(hProcess, new IntPtr(addr),
                out MEMORY_BASIC_INFORMATION mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());

            if (result == 0 || mbi.RegionSize == 0) break;

            if (mbi.State == 0x1000 && (mbi.Protect & 0x40) != 0)
                count++;

            if (mbi.RegionSize > 0x7FFF0000 - addr) break;
            var next = addr + mbi.RegionSize;
            if (next <= addr) break;
            addr = next;
        }

        return count;
    }

    private static int CountHiddenModules(IntPtr hProcess)
    {
        var count = 0;
        uint addr = 0x10000;

        while (addr < 0x7FFF0000)
        {
            var result = VirtualQueryEx(hProcess, new IntPtr(addr),
                out MEMORY_BASIC_INFORMATION mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());

            if (result == 0 || mbi.RegionSize == 0) break;

            if (mbi.State == 0x1000 && mbi.Type == 0x20000)
            {
                var header = new byte[2];
                if (ReadProcessMemory(hProcess, new IntPtr(addr), header, 2, out _) &&
                    header[0] == 0x4D && header[1] == 0x5A)
                {
                    count++;
                }
            }

            if (mbi.RegionSize > 0x7FFF0000 - addr) break;
            var next = addr + mbi.RegionSize;
            if (next <= addr) break;
            addr = next;
        }

        return count;
    }

    /// <summary>
    /// Resolves an export address within a 32-bit system DLL loaded in the WOW64 target.
    /// Enumerates the target's modules to find the correct 32-bit base address.
    /// </summary>
    private static uint ResolveWow64Export(IntPtr hProcess, string moduleName, string functionName)
    {
        const uint TH32CS_SNAPMODULE = 0x00000008;
        const uint TH32CS_SNAPMODULE32 = 0x00000010;

        int pid;
        try { pid = GetProcessId(hProcess); }
        catch { return 0; }

        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
            return 0;

        try
        {
            var entry = new MODULEENTRY32W();
            entry.dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>();

            if (!Module32FirstW(snap, ref entry))
                return 0;

            do
            {
                if (entry.szModule.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return FindExportInRemoteModule(hProcess, (uint)entry.modBaseAddr.ToInt32(),
                        entry.modBaseSize, functionName);
                }
            } while (Module32NextW(snap, ref entry));

            return 0;
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    private static uint FindExportInRemoteModule(IntPtr hProcess, uint moduleBase, uint moduleSize,
        string functionName)
    {
        var headerBuf = new byte[0x400];
        if (!ReadProcessMemory(hProcess, new IntPtr(moduleBase), headerBuf, headerBuf.Length, out _))
            return 0;

        var peOffset = BitConverter.ToUInt32(headerBuf, 0x3C);
        if (peOffset + 0x78 + 8 > headerBuf.Length)
            return 0;

        var exportRva = BitConverter.ToUInt32(headerBuf, (int)(peOffset + 0x78));
        var exportSize = BitConverter.ToUInt32(headerBuf, (int)(peOffset + 0x7C));
        if (exportRva == 0 || exportSize == 0)
            return 0;

        var exportBuf = new byte[exportSize];
        if (!ReadProcessMemory(hProcess, new IntPtr(moduleBase + exportRva), exportBuf, exportBuf.Length, out _))
            return 0;

        var numberOfNames = BitConverter.ToUInt32(exportBuf, 0x18);
        var namesRva = BitConverter.ToUInt32(exportBuf, 0x20);
        var ordinalsRva = BitConverter.ToUInt32(exportBuf, 0x24);
        var functionsRva = BitConverter.ToUInt32(exportBuf, 0x1C);

        for (uint i = 0; i < numberOfNames; i++)
        {
            var nameRvaOffset = namesRva + i * 4 - exportRva;
            if (nameRvaOffset + 4 > exportBuf.Length) continue;

            var nameRva = BitConverter.ToUInt32(exportBuf, (int)nameRvaOffset);
            var nameAddr = moduleBase + nameRva;

            var nameBuf = new byte[128];
            if (!ReadProcessMemory(hProcess, new IntPtr(nameAddr), nameBuf, nameBuf.Length, out _))
                continue;

            var nameStr = System.Text.Encoding.ASCII.GetString(nameBuf);
            var nullIdx = nameStr.IndexOf('\0');
            if (nullIdx >= 0) nameStr = nameStr[..nullIdx];

            if (!nameStr.Equals(functionName, StringComparison.Ordinal))
                continue;

            var ordOffset = ordinalsRva + i * 2 - exportRva;
            if (ordOffset + 2 > exportBuf.Length) continue;
            var ordinal = BitConverter.ToUInt16(exportBuf, (int)ordOffset);

            var funcOffset = functionsRva + ordinal * 4u - exportRva;
            if (funcOffset + 4 > exportBuf.Length) continue;
            var funcRva = BitConverter.ToUInt32(exportBuf, (int)funcOffset);

            return moduleBase + funcRva;
        }

        return 0;
    }

    private static string? DetectHook(byte[] bytes)
    {
        if (bytes.Length < 2) return null;
        if (bytes[0] == 0xE9) return "JMP rel32";
        if (bytes[0] == 0xE8) return "CALL rel32";
        if (bytes[0] == 0xFF && bytes[1] == 0x25) return "JMP [addr]";
        if (bytes.Length >= 6 && bytes[0] == 0x68 && bytes[5] == 0xC3) return "PUSH+RET";
        if (bytes[0] == 0xEB) return "JMP rel8";
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public uint BaseAddress;
        public uint AllocationBase;
        public uint AllocationProtect;
        public uint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlssCntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetProcessId(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);
}
