using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TalosForge.UnlockerAgentHost.Runtime;

internal static class NativeAgentInjector
{
    private const ushort ImageDosSignature = 0x5A4D;
    private const uint ImageNtSignature = 0x00004550;
    private const ushort ImageNtOptionalHdr32Magic = 0x10B;
    private const ushort ImageNtOptionalHdr64Magic = 0x20B;
    private const int ImageFileHeaderSize = 20;
    private const int ImageExportDirectorySize = 40;
    private const int MaxExportNameLength = 256;
    private const int MaxForwarderDepth = 4;

    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;

    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmRead = 0x0010;

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;

    public static bool TryInject(int processId, string dllPath, int timeoutMs, out string error)
    {
        error = string.Empty;
        if (!File.Exists(dllPath))
        {
            error = $"Native DLL not found at '{dllPath}'.";
            return false;
        }

        IntPtr processHandle = IntPtr.Zero;
        IntPtr remoteBuffer = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            processHandle = OpenProcess(
                ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmWrite | ProcessVmRead,
                false,
                processId);
            if (processHandle == IntPtr.Zero)
            {
                error = $"OpenProcess failed ({GetLastErrorMessage()}).";
                return false;
            }

            var unicodeBytes = Encoding.Unicode.GetBytes(dllPath + '\0');
            remoteBuffer = VirtualAllocEx(
                processHandle,
                IntPtr.Zero,
                (nuint)unicodeBytes.Length,
                MemCommit | MemReserve,
                PageReadWrite);
            if (remoteBuffer == IntPtr.Zero)
            {
                error = $"VirtualAllocEx failed ({GetLastErrorMessage()}).";
                return false;
            }

            if (!WriteProcessMemory(processHandle, remoteBuffer, unicodeBytes, unicodeBytes.Length, out var written) ||
                written.ToUInt64() != (ulong)unicodeBytes.Length)
            {
                error = $"WriteProcessMemory failed ({GetLastErrorMessage()}).";
                return false;
            }

            var loadLibraryW = ResolveRemoteLoadLibraryW(processHandle, processId);
            if (loadLibraryW == IntPtr.Zero)
            {
                error = "Unable to resolve remote LoadLibraryW address.";
                return false;
            }

            threadHandle = CreateRemoteThread(
                processHandle,
                IntPtr.Zero,
                0,
                loadLibraryW,
                remoteBuffer,
                0,
                out _);
            if (threadHandle == IntPtr.Zero)
            {
                error = $"CreateRemoteThread failed ({GetLastErrorMessage()}).";
                return false;
            }

            var waitResult = WaitForSingleObject(threadHandle, (uint)Math.Max(1, timeoutMs));
            if (waitResult == WaitTimeout)
            {
                error = $"Injection thread timed out after {timeoutMs}ms.";
                return false;
            }

            if (waitResult != WaitObject0)
            {
                error = $"WaitForSingleObject failed (result={waitResult}).";
                return false;
            }

            if (!GetExitCodeThread(threadHandle, out var exitCode))
            {
                error = $"GetExitCodeThread failed ({GetLastErrorMessage()}).";
                return false;
            }

            if (exitCode == 0)
            {
                error = "LoadLibraryW returned null module handle.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (threadHandle != IntPtr.Zero)
            {
                CloseHandle(threadHandle);
            }

            if (remoteBuffer != IntPtr.Zero && processHandle != IntPtr.Zero)
            {
                VirtualFreeEx(processHandle, remoteBuffer, 0, MemRelease);
            }

            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }
        }
    }

    private static IntPtr ResolveRemoteLoadLibraryW(IntPtr processHandle, int processId)
    {
        if (!TryGetRemoteModuleInfo(processId, "kernel32.dll", out var remoteKernel32, out _))
        {
            return IntPtr.Zero;
        }

        return ResolveRemoteExportByName(processHandle, processId, remoteKernel32, "LoadLibraryW", depth: 0);
    }

    private static IntPtr ResolveRemoteExportByName(
        IntPtr processHandle,
        int processId,
        IntPtr moduleBase,
        string exportName,
        int depth)
    {
        if (depth > MaxForwarderDepth || moduleBase == IntPtr.Zero || string.IsNullOrWhiteSpace(exportName))
        {
            return IntPtr.Zero;
        }

        if (!TryReadUInt16(processHandle, moduleBase, out var mz) || mz != ImageDosSignature)
        {
            return IntPtr.Zero;
        }

        if (!TryReadInt32(processHandle, Add(moduleBase, 0x3C), out var lfanew) || lfanew <= 0)
        {
            return IntPtr.Zero;
        }

        var ntHeaders = Add(moduleBase, lfanew);
        if (!TryReadUInt32(processHandle, ntHeaders, out var signature) || signature != ImageNtSignature)
        {
            return IntPtr.Zero;
        }

        var optionalHeader = Add(ntHeaders, 4 + ImageFileHeaderSize);
        if (!TryReadUInt16(processHandle, optionalHeader, out var optionalMagic))
        {
            return IntPtr.Zero;
        }

        var dataDirectoryOffset = optionalMagic switch
        {
            ImageNtOptionalHdr32Magic => 96,
            ImageNtOptionalHdr64Magic => 112,
            _ => -1
        };
        if (dataDirectoryOffset < 0)
        {
            return IntPtr.Zero;
        }

        var exportDirectoryEntry = Add(optionalHeader, dataDirectoryOffset);
        if (!TryReadUInt32(processHandle, exportDirectoryEntry, out var exportRva) || exportRva == 0 ||
            !TryReadUInt32(processHandle, Add(exportDirectoryEntry, 4), out var exportSize) || exportSize == 0)
        {
            return IntPtr.Zero;
        }

        if (!TryReadBytes(processHandle, Add(moduleBase, exportRva), ImageExportDirectorySize, out var exportDirectory))
        {
            return IntPtr.Zero;
        }

        var numberOfFunctions = BitConverter.ToUInt32(exportDirectory, 20);
        var numberOfNames = BitConverter.ToUInt32(exportDirectory, 24);
        var addressOfFunctions = BitConverter.ToUInt32(exportDirectory, 28);
        var addressOfNames = BitConverter.ToUInt32(exportDirectory, 32);
        var addressOfNameOrdinals = BitConverter.ToUInt32(exportDirectory, 36);

        if (numberOfFunctions == 0 || numberOfNames == 0 || addressOfFunctions == 0 || addressOfNames == 0 || addressOfNameOrdinals == 0)
        {
            return IntPtr.Zero;
        }

        if (!TryReadUInt32Array(processHandle, Add(moduleBase, addressOfNames), numberOfNames, out var nameRvas) ||
            !TryReadUInt16Array(processHandle, Add(moduleBase, addressOfNameOrdinals), numberOfNames, out var nameOrdinals) ||
            !TryReadUInt32Array(processHandle, Add(moduleBase, addressOfFunctions), numberOfFunctions, out var functionRvas))
        {
            return IntPtr.Zero;
        }

        for (var i = 0; i < nameRvas.Length; i++)
        {
            if (nameRvas[i] == 0)
            {
                continue;
            }

            var currentName = TryReadAnsiString(processHandle, Add(moduleBase, nameRvas[i]), MaxExportNameLength);
            if (!string.Equals(currentName, exportName, StringComparison.Ordinal))
            {
                continue;
            }

            var ordinal = nameOrdinals[i];
            if (ordinal >= functionRvas.Length)
            {
                return IntPtr.Zero;
            }

            var functionRva = functionRvas[ordinal];
            if (functionRva == 0)
            {
                return IntPtr.Zero;
            }

            var exportEnd = exportRva + exportSize;
            if (functionRva >= exportRva && functionRva < exportEnd)
            {
                var forwarder = TryReadAnsiString(processHandle, Add(moduleBase, functionRva), MaxExportNameLength);
                if (!TryParseForwarder(forwarder, out var forwarderModule, out var forwarderExport))
                {
                    return IntPtr.Zero;
                }

                if (!TryGetRemoteModuleInfo(processId, forwarderModule, out var forwarderBase, out _))
                {
                    return IntPtr.Zero;
                }

                return ResolveRemoteExportByName(processHandle, processId, forwarderBase, forwarderExport, depth + 1);
            }

            return Add(moduleBase, functionRva);
        }

        return IntPtr.Zero;
    }

    private static bool TryGetRemoteModuleInfo(int processId, string moduleName, out IntPtr moduleBase, out uint moduleSize)
    {
        moduleBase = IntPtr.Zero;
        moduleSize = 0;

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)processId);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            var moduleEntry = new MODULEENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>()
            };

            if (!Module32First(snapshot, ref moduleEntry))
            {
                return false;
            }

            do
            {
                if (moduleEntry.szModule.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    moduleBase = moduleEntry.modBaseAddr;
                    moduleSize = moduleEntry.modBaseSize;
                    return true;
                }
            }
            while (Module32Next(snapshot, ref moduleEntry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return false;
    }

    private static bool TryParseForwarder(string? forwarder, out string moduleName, out string exportName)
    {
        moduleName = string.Empty;
        exportName = string.Empty;
        if (string.IsNullOrWhiteSpace(forwarder))
        {
            return false;
        }

        var separator = forwarder.IndexOf('.');
        if (separator <= 0 || separator >= forwarder.Length - 1)
        {
            return false;
        }

        moduleName = forwarder.Substring(0, separator);
        exportName = forwarder.Substring(separator + 1);
        if (exportName.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        if (!moduleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            moduleName += ".dll";
        }

        return true;
    }

    private static IntPtr Add(IntPtr address, int offset)
    {
        return new IntPtr(address.ToInt64() + offset);
    }

    private static IntPtr Add(IntPtr address, uint offset)
    {
        return new IntPtr(address.ToInt64() + (long)offset);
    }

    private static bool TryReadInt32(IntPtr processHandle, IntPtr address, out int value)
    {
        value = 0;
        if (!TryReadBytes(processHandle, address, sizeof(int), out var buffer))
        {
            return false;
        }

        value = BitConverter.ToInt32(buffer, 0);
        return true;
    }

    private static bool TryReadUInt16(IntPtr processHandle, IntPtr address, out ushort value)
    {
        value = 0;
        if (!TryReadBytes(processHandle, address, sizeof(ushort), out var buffer))
        {
            return false;
        }

        value = BitConverter.ToUInt16(buffer, 0);
        return true;
    }

    private static bool TryReadUInt32(IntPtr processHandle, IntPtr address, out uint value)
    {
        value = 0;
        if (!TryReadBytes(processHandle, address, sizeof(uint), out var buffer))
        {
            return false;
        }

        value = BitConverter.ToUInt32(buffer, 0);
        return true;
    }

    private static bool TryReadUInt32Array(IntPtr processHandle, IntPtr address, uint count, out uint[] values)
    {
        values = Array.Empty<uint>();
        if (count == 0 || count > int.MaxValue / sizeof(uint))
        {
            return false;
        }

        var length = checked((int)count);
        if (!TryReadBytes(processHandle, address, checked(length * sizeof(uint)), out var buffer))
        {
            return false;
        }

        values = new uint[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = BitConverter.ToUInt32(buffer, i * sizeof(uint));
        }

        return true;
    }

    private static bool TryReadUInt16Array(IntPtr processHandle, IntPtr address, uint count, out ushort[] values)
    {
        values = Array.Empty<ushort>();
        if (count == 0 || count > int.MaxValue / sizeof(ushort))
        {
            return false;
        }

        var length = checked((int)count);
        if (!TryReadBytes(processHandle, address, checked(length * sizeof(ushort)), out var buffer))
        {
            return false;
        }

        values = new ushort[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = BitConverter.ToUInt16(buffer, i * sizeof(ushort));
        }

        return true;
    }

    private static string? TryReadAnsiString(IntPtr processHandle, IntPtr address, int maxLength)
    {
        if (maxLength <= 0)
        {
            return null;
        }

        if (!TryReadBytes(processHandle, address, maxLength, out var buffer))
        {
            return null;
        }

        var terminatorIndex = Array.IndexOf(buffer, (byte)0);
        if (terminatorIndex < 0)
        {
            terminatorIndex = buffer.Length;
        }

        return Encoding.ASCII.GetString(buffer, 0, terminatorIndex);
    }

    private static bool TryReadBytes(IntPtr processHandle, IntPtr address, int size, out byte[] buffer)
    {
        buffer = Array.Empty<byte>();
        if (size <= 0)
        {
            return false;
        }

        var temp = new byte[size];
        if (!ReadProcessMemory(processHandle, address, temp, size, out var bytesRead))
        {
            return false;
        }

        if (bytesRead.ToUInt64() != (ulong)size)
        {
            return false;
        }

        buffer = temp;
        return true;
    }

    private static string GetLastErrorMessage()
    {
        var code = Marshal.GetLastWin32Error();
        return $"{code} ({new Win32Exception(code).Message})";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MODULEENTRY32
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
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
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Module32First(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Module32Next(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr processHandle,
        IntPtr address,
        nuint size,
        uint allocationType,
        uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(
        IntPtr processHandle,
        IntPtr address,
        nuint size,
        uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out UIntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out UIntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr processHandle,
        IntPtr threadAttributes,
        uint stackSize,
        IntPtr startAddress,
        IntPtr parameter,
        uint creationFlags,
        out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
