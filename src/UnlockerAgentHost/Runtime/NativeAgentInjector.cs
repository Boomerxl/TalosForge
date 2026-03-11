using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TalosForge.UnlockerAgentHost.Runtime;

internal static class NativeAgentInjector
{
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

            var kernel32 = GetModuleHandle("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
            {
                error = $"GetModuleHandle(kernel32) failed ({GetLastErrorMessage()}).";
                return false;
            }

            var loadLibraryW = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibraryW == IntPtr.Zero)
            {
                error = $"GetProcAddress(LoadLibraryW) failed ({GetLastErrorMessage()}).";
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

    private static string GetLastErrorMessage()
    {
        var code = Marshal.GetLastWin32Error();
        return $"{code} ({new Win32Exception(code).Message})";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

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
