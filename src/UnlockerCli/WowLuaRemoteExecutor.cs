using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TalosForge.UnlockerCli;

public static class WowLuaRemoteExecutor
{
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;

    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeoutMs = 5000;

    // x86 remote thread body:
    // - optional hardware event flag set/reset
    // - call FrameScript_Execute(code, source, 0)
    // - restore stack regardless of calling convention behavior
    private static readonly byte[] LuaExecShellcode =
    {
        0x55,                         // push ebp
        0x8B, 0xEC,                   // mov ebp, esp
        0x60,                         // pushad
        0x8B, 0x45, 0x08,             // mov eax, [ebp+8]      ; ctx
        0x8B, 0x08,                   // mov ecx, [eax]        ; flag addr
        0x85, 0xC9,                   // test ecx, ecx
        0x74, 0x06,                   // je +6
        0xC7, 0x01, 0x01, 0x00, 0x00, 0x00, // mov dword ptr [ecx],1
        0x8B, 0xFC,                   // mov edi, esp
        0x6A, 0x00,                   // push 0
        0xFF, 0x70, 0x04,             // push dword ptr [eax+4] ; source
        0xFF, 0x70, 0x08,             // push dword ptr [eax+8] ; code
        0x8B, 0x50, 0x0C,             // mov edx, [eax+0xC]    ; exec fn
        0xFF, 0xD2,                   // call edx
        0x8B, 0xE7,                   // mov esp, edi
        0x85, 0xC9,                   // test ecx, ecx
        0x74, 0x06,                   // je +6
        0xC7, 0x01, 0x00, 0x00, 0x00, 0x00, // mov dword ptr [ecx],0
        0x61,                         // popad
        0x33, 0xC0,                   // xor eax, eax
        0x5D,                         // pop ebp
        0xC2, 0x04, 0x00              // ret 4
    };

    public static bool TryExecute(
        string processName,
        string luaCode,
        uint luaExecuteAddress,
        uint? hardwareEventFlagAddress,
        string sourceLabel,
        out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(luaCode))
        {
            error = "Lua code is empty.";
            return false;
        }

        var process = Process.GetProcessesByName(processName).FirstOrDefault();
        if (process is null)
        {
            error = $"WoW process '{processName}' not found.";
            return false;
        }

        IntPtr processHandle = IntPtr.Zero;
        IntPtr codePtr = IntPtr.Zero;
        IntPtr sourcePtr = IntPtr.Zero;
        IntPtr contextPtr = IntPtr.Zero;
        IntPtr shellPtr = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            processHandle = OpenProcess(
                ProcessCreateThread | ProcessVmOperation | ProcessVmWrite | ProcessVmRead | ProcessQueryInformation,
                false,
                process.Id);
            if (processHandle == IntPtr.Zero)
            {
                error = $"OpenProcess failed: {GetLastWin32ErrorMessage()}";
                return false;
            }

            if (!TryWriteRemoteUtf8(processHandle, luaCode, out codePtr, out error) ||
                !TryWriteRemoteUtf8(processHandle, sourceLabel, out sourcePtr, out error))
            {
                return false;
            }

            var context = BuildContext(
                hardwareEventFlagAddress ?? 0,
                ToAddress32(sourcePtr),
                ToAddress32(codePtr),
                luaExecuteAddress);

            contextPtr = VirtualAllocEx(
                processHandle,
                IntPtr.Zero,
                (nuint)context.Length,
                MemCommit | MemReserve,
                PageReadWrite);
            if (contextPtr == IntPtr.Zero)
            {
                error = $"VirtualAllocEx(context) failed: {GetLastWin32ErrorMessage()}";
                return false;
            }

            if (!WriteProcessMemory(processHandle, contextPtr, context, context.Length, out var written) ||
                written.ToUInt64() != (ulong)context.Length)
            {
                error = $"WriteProcessMemory(context) failed: {GetLastWin32ErrorMessage()}";
                return false;
            }

            shellPtr = VirtualAllocEx(
                processHandle,
                IntPtr.Zero,
                (nuint)LuaExecShellcode.Length,
                MemCommit | MemReserve,
                PageExecuteReadWrite);
            if (shellPtr == IntPtr.Zero)
            {
                error = $"VirtualAllocEx(shellcode) failed: {GetLastWin32ErrorMessage()}";
                return false;
            }

            if (!WriteProcessMemory(processHandle, shellPtr, LuaExecShellcode, LuaExecShellcode.Length, out written) ||
                written.ToUInt64() != (ulong)LuaExecShellcode.Length)
            {
                error = $"WriteProcessMemory(shellcode) failed: {GetLastWin32ErrorMessage()}";
                return false;
            }

            threadHandle = CreateRemoteThread(
                processHandle,
                IntPtr.Zero,
                0,
                shellPtr,
                contextPtr,
                0,
                out _);
            if (threadHandle == IntPtr.Zero)
            {
                error = $"CreateRemoteThread failed: {GetLastWin32ErrorMessage()}";
                return false;
            }

            var wait = WaitForSingleObject(threadHandle, WaitTimeoutMs);
            if (wait != WaitObject0)
            {
                error = wait == 0x00000102
                    ? "Remote Lua thread timed out."
                    : $"WaitForSingleObject failed (code={wait}).";
                return false;
            }

            if (!GetExitCodeThread(threadHandle, out var exitCode))
            {
                error = $"GetExitCodeThread failed: {GetLastWin32ErrorMessage()}";
                return false;
            }

            if (exitCode != 0)
            {
                error = $"Remote Lua thread returned non-zero exit code {exitCode}.";
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

            if (shellPtr != IntPtr.Zero && processHandle != IntPtr.Zero)
            {
                VirtualFreeEx(processHandle, shellPtr, 0, MemRelease);
            }

            if (contextPtr != IntPtr.Zero && processHandle != IntPtr.Zero)
            {
                VirtualFreeEx(processHandle, contextPtr, 0, MemRelease);
            }

            if (sourcePtr != IntPtr.Zero && processHandle != IntPtr.Zero)
            {
                VirtualFreeEx(processHandle, sourcePtr, 0, MemRelease);
            }

            if (codePtr != IntPtr.Zero && processHandle != IntPtr.Zero)
            {
                VirtualFreeEx(processHandle, codePtr, 0, MemRelease);
            }

            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }
        }
    }

    private static bool TryWriteRemoteUtf8(
        IntPtr processHandle,
        string text,
        out IntPtr remotePtr,
        out string error)
    {
        error = string.Empty;
        var bytes = Encoding.UTF8.GetBytes(text + '\0');
        remotePtr = VirtualAllocEx(
            processHandle,
            IntPtr.Zero,
            (nuint)bytes.Length,
            MemCommit | MemReserve,
            PageReadWrite);
        if (remotePtr == IntPtr.Zero)
        {
            error = $"VirtualAllocEx(string) failed: {GetLastWin32ErrorMessage()}";
            return false;
        }

        if (!WriteProcessMemory(processHandle, remotePtr, bytes, bytes.Length, out var written) ||
            written.ToUInt64() != (ulong)bytes.Length)
        {
            error = $"WriteProcessMemory(string) failed: {GetLastWin32ErrorMessage()}";
            return false;
        }

        return true;
    }

    private static byte[] BuildContext(uint hardwareFlagAddress, uint sourcePtr, uint codePtr, uint luaExecuteAddress)
    {
        var context = new byte[16];
        WriteUInt32(context, 0, hardwareFlagAddress);
        WriteUInt32(context, 4, sourcePtr);
        WriteUInt32(context, 8, codePtr);
        WriteUInt32(context, 12, luaExecuteAddress);
        return context;
    }

    private static void WriteUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset + 0] = (byte)(value & 0xFF);
        destination[offset + 1] = (byte)((value >> 8) & 0xFF);
        destination[offset + 2] = (byte)((value >> 16) & 0xFF);
        destination[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static uint ToAddress32(IntPtr ptr)
    {
        var raw = ptr.ToInt64();
        if (raw < 0 || raw > uint.MaxValue)
        {
            throw new InvalidOperationException($"Remote pointer out of x86 range: 0x{raw:X}");
        }

        return (uint)raw;
    }

    private static string GetLastWin32ErrorMessage()
    {
        var errorCode = Marshal.GetLastWin32Error();
        return $"{errorCode} ({new Win32Exception(errorCode).Message})";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint processAccess,
        bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

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
    private static extern uint WaitForSingleObject(
        IntPtr handle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(
        IntPtr thread,
        out uint exitCode);
}
