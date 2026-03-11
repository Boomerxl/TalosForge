using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TalosForge.UnlockerAgentHost.Runtime;

internal static class NativeAgentInjector
{
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

            var loadLibraryW = ResolveRemoteLoadLibraryW(processId);
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

    private static IntPtr ResolveRemoteLoadLibraryW(int processId)
    {
        var localKernel32 = GetModuleHandle("kernel32.dll");
        if (localKernel32 == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var localLoadLibraryW = GetProcAddress(localKernel32, "LoadLibraryW");
        if (localLoadLibraryW == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var remoteKernel32 = TryGetRemoteModuleBase(processId, "kernel32.dll");
        if (remoteKernel32 == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var offset = localLoadLibraryW.ToInt64() - localKernel32.ToInt64();
        if (offset < 0)
        {
            return IntPtr.Zero;
        }

        return new IntPtr(remoteKernel32.ToInt64() + offset);
    }

    private static IntPtr TryGetRemoteModuleBase(int processId, string moduleName)
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)processId);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return IntPtr.Zero;
        }

        try
        {
            var moduleEntry = new MODULEENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>()
            };

            if (!Module32First(snapshot, ref moduleEntry))
            {
                return IntPtr.Zero;
            }

            do
            {
                if (moduleEntry.szModule.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return moduleEntry.modBaseAddr;
                }
            }
            while (Module32Next(snapshot, ref moduleEntry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return IntPtr.Zero;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

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
