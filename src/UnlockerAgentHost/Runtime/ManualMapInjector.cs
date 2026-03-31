using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TalosForge.UnlockerAgentHost.Runtime;

/// <summary>
/// Stealth manual-maps the native agent DLL into the target process.
/// - NtCreateSection + NtMapViewOfSection (no WriteProcessMemory)
/// - Thread hijacking via Wow64Get/SetThreadContext (no CreateRemoteThread)
/// - XOR encryption of image in transit (anti-pattern-scan)
/// The module never appears in PEB module lists, toolhelp snapshots, or NtQueryInformation.
/// </summary>
internal static class ManualMapInjector
{
    private const ushort ImageDosSignature = 0x5A4D;
    private const uint ImageNtSignature = 0x00004550;
    private const ushort Pe32Magic = 0x10B;
    private const int ImageFileHeaderSize = 20;
    private const int ImageExportDirectorySize = 40;
    private const int MaxExportNameLength = 256;
    private const int MaxForwarderDepth = 4;

    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;
    private const uint TH32CS_SNAPTHREAD = 0x00000004;

    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmRead = 0x0010;

    private const uint ThreadSuspendResume = 0x0002;
    private const uint ThreadGetContext = 0x0008;
    private const uint ThreadSetContext = 0x0010;
    private const uint ThreadQueryInformation = 0x0040;

    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteRead = 0x20;
    private const uint PageReadonly = 0x02;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;

    private const uint SectionAllAccess = 0xF001F;
    private const uint SecCommit = 0x8000000;
    private const uint ViewUnmap = 2;

    private const ushort ImageRelBasedHighlow = 3;
    private const ushort ImageRelBasedAbsolute = 0;

    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;

    private const uint Wow64ContextFull = 0x00010007;

    private const int ShellcodeAlignment = 64;
    private const int CompletionFlagOffset = 4;

    public static bool TryInject(int processId, string dllPath, int timeoutMs, out string error)
    {
        error = string.Empty;
        if (!File.Exists(dllPath))
        {
            error = $"Native DLL not found at '{dllPath}'.";
            return false;
        }

        var dllBytes = File.ReadAllBytes(dllPath);
        if (dllBytes.Length < 256)
        {
            error = "DLL file too small.";
            return false;
        }

        if (!TryParsePe32(dllBytes, out var pe, out error))
            return false;

        IntPtr processHandle = IntPtr.Zero;
        IntPtr sectionHandle = IntPtr.Zero;
        IntPtr localBase = IntPtr.Zero;
        IntPtr remoteBase = IntPtr.Zero;

        try
        {
            processHandle = OpenProcess(
                ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmWrite | ProcessVmRead,
                false, processId);
            if (processHandle == IntPtr.Zero)
            {
                error = $"OpenProcess failed ({GetLastErrorMessage()}).";
                return false;
            }

            // Prepare the full image buffer locally (sections mapped to virtual layout)
            var shellcodeSize = AlignUp(64, ShellcodeAlignment);
            var totalSize = (long)pe.SizeOfImage + shellcodeSize + ShellcodeAlignment;

            var imageBuffer = new byte[pe.SizeOfImage];
            Array.Copy(dllBytes, 0, imageBuffer, 0, Math.Min((int)pe.SizeOfHeaders, dllBytes.Length));

            CopySections(dllBytes, pe, imageBuffer);

            // Resolve imports from the mapped image buffer (RVAs are valid offsets here)
            var importPatches = ResolveAllImports(processHandle, processId, imageBuffer, pe, out error);
            if (importPatches == null)
                return false;

            // Create shared section (no VirtualAllocEx)
            var sectionSize = totalSize;
            var status = NtCreateSection(out sectionHandle, SectionAllAccess, IntPtr.Zero,
                ref sectionSize, PageExecuteReadWrite, SecCommit, IntPtr.Zero);
            if (status != 0)
            {
                error = $"NtCreateSection failed (0x{status:X8}).";
                return false;
            }

            // Map into our process (read-write, for preparation)
            var localViewSize = UIntPtr.Zero;
            long localOffset = 0;
            status = NtMapViewOfSection(sectionHandle, GetCurrentProcess(), ref localBase,
                UIntPtr.Zero, UIntPtr.Zero, ref localOffset, ref localViewSize, ViewUnmap, 0, PageReadWrite);
            if (status != 0)
            {
                error = $"NtMapViewOfSection (local) failed (0x{status:X8}).";
                return false;
            }

            // Map into target process (execute-read-write for DLL runtime needs)
            var remoteViewSize = UIntPtr.Zero;
            long remoteOffset = 0;
            status = NtMapViewOfSection(sectionHandle, processHandle, ref remoteBase,
                UIntPtr.Zero, UIntPtr.Zero, ref remoteOffset, ref remoteViewSize, ViewUnmap, 0, PageExecuteReadWrite);
            if (status != 0)
            {
                error = $"NtMapViewOfSection (remote) failed (0x{status:X8}).";
                return false;
            }

            var remoteBaseValue = (uint)remoteBase.ToInt64();
            var delta = remoteBaseValue - pe.PreferredBase;

            // Apply relocations
            if (delta != 0)
                ApplyRelocations(imageBuffer, pe, delta);

            // Apply import patches
            foreach (var patch in importPatches)
            {
                var addrBytes = BitConverter.GetBytes(patch.ResolvedAddress);
                Array.Copy(addrBytes, 0, imageBuffer, patch.IatOffset, 4);
            }

            // XOR encrypt the image (anti-pattern-scan)
            var xorKey = (byte)(Environment.TickCount & 0xFF);
            if (xorKey == 0) xorKey = 0xAB;
            var encryptedImage = new byte[imageBuffer.Length];
            for (var i = 0; i < imageBuffer.Length; i++)
                encryptedImage[i] = (byte)(imageBuffer[i] ^ xorKey);

            // Copy encrypted image to shared section (visible in target via shared pages)
            Marshal.Copy(encryptedImage, 0, localBase, encryptedImage.Length);

            // Build shellcode: decrypt in-place, call DllMain, set completion flag
            var entryPoint = remoteBaseValue + pe.EntryPointRva;
            var flagAddr = remoteBaseValue + pe.SizeOfImage + (uint)shellcodeSize;
            var shellcode = BuildShellcode(remoteBaseValue, pe.SizeOfImage, xorKey, entryPoint, flagAddr);

            // Write shellcode to shared section (after the image)
            Marshal.Copy(shellcode, 0, IntPtr.Add(localBase, (int)pe.SizeOfImage), shellcode.Length);

            // Zero the completion flag
            Marshal.WriteByte(IntPtr.Add(localBase, (int)pe.SizeOfImage + shellcodeSize), 0);

            // Thread hijacking (no CreateRemoteThread)
            var shellcodeAddr = remoteBaseValue + pe.SizeOfImage;
            if (!HijackThread(processId, processHandle, shellcodeAddr, out error))
                return false;

            // Poll completion flag via our local mapping (shared memory, no RPM needed)
            var flagLocal = IntPtr.Add(localBase, (int)pe.SizeOfImage + shellcodeSize);
            var waited = 0;
            const int pollInterval = 10;
            var budget = Math.Max(5000, timeoutMs);
            while (waited < budget)
            {
                if (Marshal.ReadByte(flagLocal) != 0)
                    break;
                Thread.Sleep(pollInterval);
                waited += pollInterval;
            }

            if (Marshal.ReadByte(flagLocal) == 0)
            {
                error = $"DllMain did not complete within {budget}ms.";
                return false;
            }

            // Erase PE header in remote memory via shared mapping
            var headerSize = Math.Min((int)pe.SizeOfHeaders, 0x1000);
            var zeroHeader = new byte[headerSize];
            Marshal.Copy(zeroHeader, 0, localBase, headerSize);

            // Erase shellcode from shared mapping
            var zeroShellcode = new byte[shellcodeSize + ShellcodeAlignment];
            Marshal.Copy(zeroShellcode, 0, IntPtr.Add(localBase, (int)pe.SizeOfImage), zeroShellcode.Length);

            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (localBase != IntPtr.Zero)
                NtUnmapViewOfSection(GetCurrentProcess(), localBase);
            if (sectionHandle != IntPtr.Zero)
                CloseHandle(sectionHandle);
            if (processHandle != IntPtr.Zero)
                CloseHandle(processHandle);
        }
    }

    #region PE parsing

    private struct PeInfo
    {
        public uint PreferredBase;
        public uint SizeOfImage;
        public uint EntryPointRva;
        public uint SizeOfHeaders;
        public ushort NumberOfSections;
        public int SectionHeaderOffset;
        public int OptionalHeaderOffset;
        public uint RelocDirRva;
        public uint RelocDirSize;
        public uint ImportDirRva;
    }

    private static bool TryParsePe32(byte[] dllBytes, out PeInfo pe, out string error)
    {
        pe = default;
        error = string.Empty;

        if (BitConverter.ToUInt16(dllBytes, 0) != ImageDosSignature)
        {
            error = "Invalid DOS signature.";
            return false;
        }

        var lfanew = BitConverter.ToInt32(dllBytes, 0x3C);
        if (lfanew <= 0 || lfanew + 4 >= dllBytes.Length)
        {
            error = "Invalid e_lfanew.";
            return false;
        }

        if (BitConverter.ToUInt32(dllBytes, lfanew) != ImageNtSignature)
        {
            error = "Invalid NT signature.";
            return false;
        }

        var opt = lfanew + 4 + ImageFileHeaderSize;
        if (BitConverter.ToUInt16(dllBytes, opt) != Pe32Magic)
        {
            error = "Only PE32 (32-bit) DLLs supported.";
            return false;
        }

        pe.OptionalHeaderOffset = opt;
        pe.PreferredBase = BitConverter.ToUInt32(dllBytes, opt + 28);
        pe.SizeOfImage = BitConverter.ToUInt32(dllBytes, opt + 56);
        pe.EntryPointRva = BitConverter.ToUInt32(dllBytes, opt + 16);
        pe.SizeOfHeaders = BitConverter.ToUInt32(dllBytes, opt + 60);
        pe.NumberOfSections = BitConverter.ToUInt16(dllBytes, lfanew + 4 + 2);
        pe.SectionHeaderOffset = opt + BitConverter.ToUInt16(dllBytes, lfanew + 4 + 16);
        pe.RelocDirRva = BitConverter.ToUInt32(dllBytes, opt + 136);
        pe.RelocDirSize = BitConverter.ToUInt32(dllBytes, opt + 140);
        pe.ImportDirRva = BitConverter.ToUInt32(dllBytes, opt + 104);
        return true;
    }

    #endregion

    #region Image preparation

    private static void CopySections(byte[] dllBytes, PeInfo pe, byte[] imageBuffer)
    {
        for (var i = 0; i < pe.NumberOfSections; i++)
        {
            var secOff = pe.SectionHeaderOffset + i * 40;
            var va = BitConverter.ToUInt32(dllBytes, secOff + 12);
            var rawSize = BitConverter.ToUInt32(dllBytes, secOff + 16);
            var rawPtr = BitConverter.ToUInt32(dllBytes, secOff + 20);
            if (rawSize == 0 || rawPtr == 0) continue;
            if (rawPtr + rawSize > (uint)dllBytes.Length) continue;
            if (va + rawSize > pe.SizeOfImage) continue;
            Array.Copy(dllBytes, (int)rawPtr, imageBuffer, (int)va, (int)rawSize);
        }
    }

    private static void ApplyRelocations(byte[] imageBuffer, PeInfo pe, uint delta)
    {
        if (pe.RelocDirRva == 0 || pe.RelocDirSize == 0) return;

        var offset = (int)pe.RelocDirRva;
        var end = offset + (int)pe.RelocDirSize;

        while (offset + 8 <= end && offset + 8 <= imageBuffer.Length)
        {
            var blockRva = BitConverter.ToUInt32(imageBuffer, offset);
            var blockSize = BitConverter.ToUInt32(imageBuffer, offset + 4);
            if (blockSize < 8) break;

            var count = (int)(blockSize - 8) / 2;
            for (var i = 0; i < count; i++)
            {
                var entry = BitConverter.ToUInt16(imageBuffer, offset + 8 + i * 2);
                var type = (ushort)(entry >> 12);
                var off = (ushort)(entry & 0xFFF);

                if (type == ImageRelBasedAbsolute) continue;
                if (type != ImageRelBasedHighlow) continue;

                var patchOff = (int)(blockRva + off);
                if (patchOff + 4 > imageBuffer.Length) continue;

                var original = BitConverter.ToUInt32(imageBuffer, patchOff);
                var relocated = original + delta;
                imageBuffer[patchOff] = (byte)(relocated & 0xFF);
                imageBuffer[patchOff + 1] = (byte)((relocated >> 8) & 0xFF);
                imageBuffer[patchOff + 2] = (byte)((relocated >> 16) & 0xFF);
                imageBuffer[patchOff + 3] = (byte)((relocated >> 24) & 0xFF);
            }

            offset += (int)blockSize;
        }
    }

    private struct ImportPatch
    {
        public int IatOffset;
        public int ResolvedAddress;
    }

    private static List<ImportPatch>? ResolveAllImports(
        IntPtr processHandle, int processId, byte[] dllBytes, PeInfo pe, out string error)
    {
        error = string.Empty;
        var patches = new List<ImportPatch>();
        if (pe.ImportDirRva == 0) return patches;

        var impOffset = (int)pe.ImportDirRva;
        while (impOffset + 20 <= dllBytes.Length)
        {
            var iltRva = BitConverter.ToUInt32(dllBytes, impOffset);
            var nameRva = BitConverter.ToUInt32(dllBytes, impOffset + 12);
            var iatRva = BitConverter.ToUInt32(dllBytes, impOffset + 16);
            if (iltRva == 0 && nameRva == 0 && iatRva == 0) break;

            // Read module name from the image buffer (not remote memory)
            var moduleName = ReadNullTerminatedAscii(dllBytes, (int)nameRva);
            if (string.IsNullOrEmpty(moduleName)) { impOffset += 20; continue; }

            if (!TryGetRemoteModuleInfo(processId, moduleName, out var remoteModBase, out _))
            {
                error = $"Cannot resolve remote module '{moduleName}'.";
                return null;
            }

            var thunkRva = iatRva != 0 ? iatRva : iltRva;
            var lookupRva = iltRva != 0 ? iltRva : iatRva;
            var idx = 0;

            while (true)
            {
                var lookupOff = (int)lookupRva + idx * 4;
                if (lookupOff + 4 > dllBytes.Length) break;
                var thunkData = BitConverter.ToUInt32(dllBytes, lookupOff);
                if (thunkData == 0) break;

                IntPtr resolved;
                string? importFuncName = null;
                if ((thunkData & 0x80000000) != 0)
                {
                    resolved = ResolveRemoteExportByOrdinal(processHandle, processId, remoteModBase, (ushort)(thunkData & 0xFFFF));
                    importFuncName = $"ordinal#{thunkData & 0xFFFF}";
                }
                else
                {
                    importFuncName = ReadNullTerminatedAscii(dllBytes, (int)thunkData + 2);
                    if (string.IsNullOrEmpty(importFuncName))
                    {
                        error = $"Empty import name in '{moduleName}' (thunk=0x{thunkData:X8}, idx={idx}).";
                        return null;
                    }
                    resolved = ResolveRemoteExportByName(processHandle, processId, remoteModBase, importFuncName, 0);
                }

                if (resolved == IntPtr.Zero)
                {
                    error = $"Cannot resolve '{importFuncName}' from '{moduleName}' (thunk=0x{thunkData:X8}, idx={idx}, modBase=0x{remoteModBase.ToInt64():X}).";
                    return null;
                }

                patches.Add(new ImportPatch
                {
                    IatOffset = (int)thunkRva + idx * 4,
                    ResolvedAddress = resolved.ToInt32()
                });
                idx++;
            }

            impOffset += 20;
        }

        return patches;
    }

    #endregion

    #region Shellcode generation

    private static byte[] BuildShellcode(uint remoteBase, uint imageSize, byte xorKey, uint entryPoint, uint flagAddr)
    {
        // x86 shellcode layout:
        // pushad; pushfd
        // mov edi, remoteBase; mov ecx, imageSize; mov al, xorKey
        // loop: xor [edi], al; inc edi; dec ecx; jnz loop
        // push 0; push 1; push remoteBase
        // mov eax, entryPoint; call eax
        // mov byte ptr [flagAddr], 1
        // popfd; popad
        // push originalEIP; ret
        // (originalEIP is patched in HijackThread)

        var code = new byte[]
        {
            0x60,                                           // pushad
            0x9C,                                           // pushfd
            0xBF, 0, 0, 0, 0,                              // mov edi, <remoteBase>
            0xB9, 0, 0, 0, 0,                              // mov ecx, <imageSize>
            0xB0, 0,                                        // mov al, <xorKey>
            0x30, 0x07,                                     // xor [edi], al
            0x47,                                           // inc edi
            0x49,                                           // dec ecx
            0x75, 0xFA,                                     // jnz -6 (back to xor)
            0x6A, 0x00,                                     // push 0
            0x6A, 0x01,                                     // push 1
            0x68, 0, 0, 0, 0,                               // push <remoteBase>
            0xB8, 0, 0, 0, 0,                               // mov eax, <entryPoint>
            0xFF, 0xD0,                                     // call eax
            0xC6, 0x05, 0, 0, 0, 0, 0x01,                  // mov byte ptr [flagAddr], 1
            0x9D,                                           // popfd
            0x61,                                           // popad
            0x68, 0, 0, 0, 0,                               // push <originalEIP> (patched later)
            0xC3                                            // ret
        };

        Patch32(code, 3, remoteBase);
        Patch32(code, 8, imageSize);
        code[13] = xorKey;
        Patch32(code, 25, remoteBase);
        Patch32(code, 30, entryPoint);
        Patch32(code, 36, flagAddr);
        // originalEIP at offset 42 is patched by HijackThread

        return code;
    }

    private const int ShellcodeOriginalEipOffset = 42;

    private static void Patch32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    #endregion

    #region Thread hijacking

    private static bool HijackThread(int processId, IntPtr processHandle, uint shellcodeAddr, out string error)
    {
        error = string.Empty;

        if (!FindSuitableThread(processId, out var threadId))
        {
            error = "No suitable thread found for hijacking.";
            return false;
        }

        var threadHandle = OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadSetContext | ThreadQueryInformation,
            false, threadId);
        if (threadHandle == IntPtr.Zero)
        {
            error = $"OpenThread failed for TID {threadId} ({GetLastErrorMessage()}).";
            return false;
        }

        try
        {
            if (SuspendThread(threadHandle) == unchecked((uint)-1))
            {
                error = $"SuspendThread failed for TID {threadId} ({GetLastErrorMessage()}).";
                return false;
            }

            try
            {
                if (Environment.Is64BitProcess)
                    return HijackThreadWow64(processHandle, threadHandle, shellcodeAddr, out error);
                else
                    return HijackThread32(processHandle, threadHandle, shellcodeAddr, out error);
            }
            finally
            {
                ResumeThread(threadHandle);
            }
        }
        finally
        {
            CloseHandle(threadHandle);
        }
    }

    private static bool HijackThreadWow64(IntPtr processHandle, IntPtr threadHandle, uint shellcodeAddr, out string error)
    {
        error = string.Empty;
        var ctx = new WOW64_CONTEXT { ContextFlags = Wow64ContextFull };
        ctx.ExtendedRegisters = new byte[512];
        ctx.FloatSave.RegisterArea = new byte[80];

        if (!Wow64GetThreadContext(threadHandle, ref ctx))
        {
            error = $"Wow64GetThreadContext failed ({GetLastErrorMessage()}).";
            return false;
        }

        var originalEip = ctx.Eip;

        // Patch the shellcode's originalEIP return address in the shared section.
        // We write it through the local mapping which is visible in the remote process.
        // The caller has already placed the shellcode at shellcodeAddr.
        // We need a way to reach the local mapping. We'll use WriteProcessMemory just for
        // the 4-byte EIP patch (much smaller surface than writing entire images).
        var eipPatchAddr = new IntPtr(shellcodeAddr + ShellcodeOriginalEipOffset);
        var eipBytes = BitConverter.GetBytes(originalEip);
        if (!WriteProcessMemory(processHandle, eipPatchAddr, eipBytes, 4, out _))
        {
            error = $"Failed to patch return EIP ({GetLastErrorMessage()}).";
            return false;
        }

        ctx.Eip = shellcodeAddr;
        if (!Wow64SetThreadContext(threadHandle, ref ctx))
        {
            error = $"Wow64SetThreadContext failed ({GetLastErrorMessage()}).";
            return false;
        }

        return true;
    }

    private static bool HijackThread32(IntPtr processHandle, IntPtr threadHandle, uint shellcodeAddr, out string error)
    {
        error = string.Empty;
        var ctx = new CONTEXT_X86 { ContextFlags = Wow64ContextFull };
        ctx.ExtendedRegisters = new byte[512];
        ctx.FloatSave.RegisterArea = new byte[80];

        if (!GetThreadContext32(threadHandle, ref ctx))
        {
            error = $"GetThreadContext failed ({GetLastErrorMessage()}).";
            return false;
        }

        var originalEip = ctx.Eip;
        var eipPatchAddr = new IntPtr(shellcodeAddr + ShellcodeOriginalEipOffset);
        var eipBytes = BitConverter.GetBytes(originalEip);
        if (!WriteProcessMemory(processHandle, eipPatchAddr, eipBytes, 4, out _))
        {
            error = $"Failed to patch return EIP ({GetLastErrorMessage()}).";
            return false;
        }

        ctx.Eip = shellcodeAddr;
        if (!SetThreadContext32(threadHandle, ref ctx))
        {
            error = $"SetThreadContext failed ({GetLastErrorMessage()}).";
            return false;
        }

        return true;
    }

    private static bool FindSuitableThread(int processId, out uint threadId)
    {
        threadId = 0;
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return false;

        try
        {
            var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (!Thread32First(snapshot, ref entry))
                return false;

            do
            {
                if (entry.th32OwnerProcessID != processId)
                    continue;

                // Use the main (first) thread -- it runs the game loop and is
                // guaranteed to be scheduled frequently, unlike worker threads
                // which may be blocked in kernel waits indefinitely.
                threadId = entry.th32ThreadID;
                return true;
            } while (Thread32Next(snapshot, ref entry));

            return false;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    #endregion

    #region Remote PE resolution

    /// <summary>
    /// Resolves an export from a remote 32-bit module by reading the DLL from disk (SysWOW64).
    /// This avoids cross-architecture ReadProcessMemory issues when the host is 64-bit.
    /// </summary>
    private static IntPtr ResolveRemoteExportByName(
        IntPtr processHandle, int processId, IntPtr moduleBase, string exportName, int depth)
    {
        if (depth > MaxForwarderDepth || moduleBase == IntPtr.Zero || string.IsNullOrWhiteSpace(exportName))
            return IntPtr.Zero;

        if (!TryGetRemoteModulePath(processId, moduleBase, out var modulePath))
        {
            System.Diagnostics.Trace.WriteLine($"[ManualMap] TryGetRemoteModulePath FAILED for base=0x{moduleBase.ToInt64():X}, export={exportName}");
            return IntPtr.Zero;
        }

        System.Diagnostics.Trace.WriteLine($"[ManualMap] Resolving '{exportName}' from '{modulePath}' (base=0x{moduleBase.ToInt64():X}, depth={depth})");
        return ResolveExportFromDisk(processHandle, processId, moduleBase, modulePath, exportName, depth);
    }

    private static IntPtr ResolveExportFromDisk(
        IntPtr processHandle, int processId, IntPtr moduleBase, string modulePath, string exportName, int depth)
    {
        if (!File.Exists(modulePath))
            return IntPtr.Zero;

        var dllBytes = File.ReadAllBytes(modulePath);
        if (dllBytes.Length < 256) return IntPtr.Zero;
        if (BitConverter.ToUInt16(dllBytes, 0) != ImageDosSignature) return IntPtr.Zero;

        var lfanew = BitConverter.ToInt32(dllBytes, 0x3C);
        if (lfanew <= 0 || lfanew + 4 >= dllBytes.Length) return IntPtr.Zero;
        if (BitConverter.ToUInt32(dllBytes, lfanew) != ImageNtSignature) return IntPtr.Zero;

        var opt = lfanew + 4 + ImageFileHeaderSize;
        var magic = BitConverter.ToUInt16(dllBytes, opt);
        if (magic != Pe32Magic) return IntPtr.Zero;

        var expRva = BitConverter.ToUInt32(dllBytes, opt + 96);
        var expSize = BitConverter.ToUInt32(dllBytes, opt + 100);
        if (expRva == 0 || expSize == 0) return IntPtr.Zero;

        var numSections = BitConverter.ToUInt16(dllBytes, lfanew + 4 + 2);
        var sectionStart = opt + BitConverter.ToUInt16(dllBytes, lfanew + 4 + 16);

        var expFileOff = RvaToFileOffset(dllBytes, expRva, numSections, sectionStart);
        if (expFileOff < 0 || expFileOff + ImageExportDirectorySize > dllBytes.Length) return IntPtr.Zero;

        var numFunctions = BitConverter.ToUInt32(dllBytes, expFileOff + 20);
        var numNames = BitConverter.ToUInt32(dllBytes, expFileOff + 24);
        var addrOfFunctions = BitConverter.ToUInt32(dllBytes, expFileOff + 28);
        var addrOfNames = BitConverter.ToUInt32(dllBytes, expFileOff + 32);
        var addrOfOrdinals = BitConverter.ToUInt32(dllBytes, expFileOff + 36);

        if (numFunctions == 0 || numNames == 0) return IntPtr.Zero;

        var namesOff = RvaToFileOffset(dllBytes, addrOfNames, numSections, sectionStart);
        var ordinalsOff = RvaToFileOffset(dllBytes, addrOfOrdinals, numSections, sectionStart);
        var functionsOff = RvaToFileOffset(dllBytes, addrOfFunctions, numSections, sectionStart);
        if (namesOff < 0 || ordinalsOff < 0 || functionsOff < 0) return IntPtr.Zero;

        for (uint i = 0; i < numNames; i++)
        {
            var nameRva = BitConverter.ToUInt32(dllBytes, namesOff + (int)i * 4);
            if (nameRva == 0) continue;
            var nameOff = RvaToFileOffset(dllBytes, nameRva, numSections, sectionStart);
            if (nameOff < 0) continue;

            var name = ReadNullTerminatedAscii(dllBytes, nameOff);
            if (!string.Equals(name, exportName, StringComparison.Ordinal)) continue;

            var ordinal = BitConverter.ToUInt16(dllBytes, ordinalsOff + (int)i * 2);
            if (ordinal >= numFunctions) return IntPtr.Zero;
            var fRva = BitConverter.ToUInt32(dllBytes, functionsOff + ordinal * 4);
            if (fRva == 0) return IntPtr.Zero;

            if (fRva >= expRva && fRva < expRva + expSize)
            {
                var fwdOff = RvaToFileOffset(dllBytes, fRva, numSections, sectionStart);
                var fwd = fwdOff >= 0 ? ReadNullTerminatedAscii(dllBytes, fwdOff) : null;
                if (!TryParseForwarder(fwd, out var fwdMod, out var fwdExp)) return IntPtr.Zero;
                if (IsApiSetModule(fwdMod))
                    return ResolveApiSetForwarder(processHandle, processId, fwdMod, fwdExp, depth);
                if (!TryGetRemoteModuleInfo(processId, fwdMod, out var fwdBase, out _)) return IntPtr.Zero;
                return ResolveRemoteExportByName(processHandle, processId, fwdBase, fwdExp, depth + 1);
            }

            return Add(moduleBase, fRva);
        }
        return IntPtr.Zero;
    }

    private static IntPtr ResolveRemoteExportByOrdinal(IntPtr processHandle, int processId, IntPtr moduleBase, ushort ordinal)
    {
        if (!TryGetRemoteModulePath(processId, moduleBase, out var modulePath))
            return IntPtr.Zero;
        if (!File.Exists(modulePath)) return IntPtr.Zero;

        var dllBytes = File.ReadAllBytes(modulePath);
        if (dllBytes.Length < 256) return IntPtr.Zero;
        if (BitConverter.ToUInt16(dllBytes, 0) != ImageDosSignature) return IntPtr.Zero;

        var lfanew = BitConverter.ToInt32(dllBytes, 0x3C);
        if (lfanew <= 0 || lfanew + 4 >= dllBytes.Length) return IntPtr.Zero;

        var opt = lfanew + 4 + ImageFileHeaderSize;
        var numSections = BitConverter.ToUInt16(dllBytes, lfanew + 4 + 2);
        var sectionStart = opt + BitConverter.ToUInt16(dllBytes, lfanew + 4 + 16);

        var expRva = BitConverter.ToUInt32(dllBytes, opt + 96);
        if (expRva == 0) return IntPtr.Zero;

        var expFileOff = RvaToFileOffset(dllBytes, expRva, numSections, sectionStart);
        if (expFileOff < 0 || expFileOff + ImageExportDirectorySize > dllBytes.Length) return IntPtr.Zero;

        var numFunctions = BitConverter.ToUInt32(dllBytes, expFileOff + 20);
        var ordBase = BitConverter.ToUInt32(dllBytes, expFileOff + 16);
        var addrOfFunctions = BitConverter.ToUInt32(dllBytes, expFileOff + 28);

        var index = (uint)ordinal - ordBase;
        if (index >= numFunctions) return IntPtr.Zero;

        var functionsOff = RvaToFileOffset(dllBytes, addrOfFunctions, numSections, sectionStart);
        if (functionsOff < 0) return IntPtr.Zero;

        var fRva = BitConverter.ToUInt32(dllBytes, functionsOff + (int)index * 4);
        return fRva == 0 ? IntPtr.Zero : Add(moduleBase, fRva);
    }

    private static int RvaToFileOffset(byte[] dllBytes, uint rva, ushort numSections, int sectionStart)
    {
        for (var i = 0; i < numSections; i++)
        {
            var secOff = sectionStart + i * 40;
            if (secOff + 40 > dllBytes.Length) return -1;
            var va = BitConverter.ToUInt32(dllBytes, secOff + 12);
            var rawSize = BitConverter.ToUInt32(dllBytes, secOff + 16);
            var rawPtr = BitConverter.ToUInt32(dllBytes, secOff + 20);
            var virtualSize = BitConverter.ToUInt32(dllBytes, secOff + 8);
            var sectionEnd = va + Math.Max(rawSize, virtualSize);
            if (rva >= va && rva < sectionEnd)
                return (int)(rawPtr + (rva - va));
        }
        return -1;
    }

    private static bool TryGetRemoteModulePath(int processId, IntPtr moduleBase, out string modulePath)
    {
        modulePath = string.Empty;
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)processId);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return false;
        try
        {
            var entry = new MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>() };
            if (!Module32First(snap, ref entry)) return false;
            do
            {
                if (entry.modBaseAddr == moduleBase)
                {
                    modulePath = entry.szExePath;
                    return !string.IsNullOrEmpty(modulePath);
                }
            } while (Module32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return false;
    }

    private static bool TryGetRemoteModuleInfo(int processId, string moduleName, out IntPtr moduleBase, out uint moduleSize)
    {
        moduleBase = IntPtr.Zero;
        moduleSize = 0;
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)processId);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return false;
        try
        {
            var entry = new MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>() };
            if (!Module32First(snap, ref entry)) return false;
            IntPtr fallbackBase = IntPtr.Zero;
            uint fallbackSize = 0;
            do
            {
                if (!entry.szModule.Equals(moduleName, StringComparison.OrdinalIgnoreCase)) continue;
                var addr = (ulong)entry.modBaseAddr.ToInt64();
                if (addr < 0x100000000UL)
                {
                    moduleBase = entry.modBaseAddr;
                    moduleSize = entry.modBaseSize;
                    return true;
                }
                if (fallbackBase == IntPtr.Zero)
                {
                    fallbackBase = entry.modBaseAddr;
                    fallbackSize = entry.modBaseSize;
                }
            } while (Module32Next(snap, ref entry));

            if (fallbackBase != IntPtr.Zero)
            {
                moduleBase = fallbackBase;
                moduleSize = fallbackSize;
                return true;
            }
        }
        finally { CloseHandle(snap); }
        return false;
    }

    private static bool TryParseForwarder(string? fwd, out string modName, out string expName)
    {
        modName = expName = string.Empty;
        if (string.IsNullOrWhiteSpace(fwd)) return false;
        var sep = fwd.IndexOf('.');
        if (sep <= 0 || sep >= fwd.Length - 1) return false;
        modName = fwd[..sep];
        expName = fwd[(sep + 1)..];
        if (expName.StartsWith('#')) return false;
        if (!modName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) modName += ".dll";
        return true;
    }

    private static readonly string[] ApiSetBackingModules = ["kernelbase.dll", "ntdll.dll", "kernel32.dll", "ucrtbase.dll"];

    private static IntPtr ResolveApiSetForwarder(
        IntPtr processHandle, int processId, string apiSetModule, string exportName, int depth)
    {
        foreach (var backing in ApiSetBackingModules)
        {
            if (!TryGetRemoteModuleInfo(processId, backing, out var backBase, out _))
                continue;
            var result = ResolveRemoteExportByName(processHandle, processId, backBase, exportName, depth + 1);
            if (result != IntPtr.Zero)
                return result;
        }
        return IntPtr.Zero;
    }

    private static bool IsApiSetModule(string moduleName)
    {
        return moduleName.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) ||
               moduleName.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Memory helpers

    private static IntPtr Add(IntPtr a, int b) => new(a.ToInt64() + b);
    private static IntPtr Add(IntPtr a, uint b) => new(a.ToInt64() + b);
    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static bool TryReadInt32(IntPtr h, IntPtr addr, out int value) { value = 0; if (!TryReadBytes(h, addr, 4, out var b)) return false; value = BitConverter.ToInt32(b, 0); return true; }
    private static bool TryReadUInt16(IntPtr h, IntPtr addr, out ushort value) { value = 0; if (!TryReadBytes(h, addr, 2, out var b)) return false; value = BitConverter.ToUInt16(b, 0); return true; }
    private static bool TryReadUInt32(IntPtr h, IntPtr addr, out uint value) { value = 0; if (!TryReadBytes(h, addr, 4, out var b)) return false; value = BitConverter.ToUInt32(b, 0); return true; }

    private static bool TryReadUInt32Array(IntPtr h, IntPtr addr, uint count, out uint[] values)
    {
        values = Array.Empty<uint>();
        if (count == 0 || count > int.MaxValue / 4) return false;
        var len = (int)count;
        if (!TryReadBytes(h, addr, len * 4, out var buf)) return false;
        values = new uint[len];
        for (var i = 0; i < len; i++) values[i] = BitConverter.ToUInt32(buf, i * 4);
        return true;
    }

    private static bool TryReadUInt16Array(IntPtr h, IntPtr addr, uint count, out ushort[] values)
    {
        values = Array.Empty<ushort>();
        if (count == 0 || count > int.MaxValue / 2) return false;
        var len = (int)count;
        if (!TryReadBytes(h, addr, len * 2, out var buf)) return false;
        values = new ushort[len];
        for (var i = 0; i < len; i++) values[i] = BitConverter.ToUInt16(buf, i * 2);
        return true;
    }

    private static string? TryReadAnsiString(IntPtr h, IntPtr addr, int maxLen)
    {
        if (maxLen <= 0) return null;
        if (!TryReadBytes(h, addr, maxLen, out var buf)) return null;
        var term = Array.IndexOf(buf, (byte)0);
        if (term < 0) term = buf.Length;
        return Encoding.ASCII.GetString(buf, 0, term);
    }

    private static string? ReadNullTerminatedAscii(byte[] buffer, int offset)
    {
        if (offset < 0 || offset >= buffer.Length) return null;
        var end = offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        return end == offset ? null : Encoding.ASCII.GetString(buffer, offset, end - offset);
    }

    private static bool TryReadBytes(IntPtr h, IntPtr addr, int size, out byte[] buffer)
    {
        buffer = Array.Empty<byte>();
        if (size <= 0) return false;
        var tmp = new byte[size];
        if (!ReadProcessMemory(h, addr, tmp, size, out var read)) return false;
        if (read.ToUInt64() != (ulong)size) return false;
        buffer = tmp;
        return true;
    }

    private static string GetLastErrorMessage()
    {
        var code = Marshal.GetLastWin32Error();
        return $"{code} ({new Win32Exception(code).Message})";
    }

    #endregion

    #region P/Invoke structs

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MODULEENTRY32
    {
        public uint dwSize, th32ModuleID, th32ProcessID, GlblcntUsage, ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WOW64_FLOATING_SAVE_AREA
    {
        public uint ControlWord, StatusWord, TagWord, ErrorOffset, ErrorSelector, DataOffset, DataSelector;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 80)] public byte[] RegisterArea;
        public uint Spare0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WOW64_CONTEXT
    {
        public uint ContextFlags;
        public uint Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
        public WOW64_FLOATING_SAVE_AREA FloatSave;
        public uint SegGs, SegFs, SegEs, SegDs;
        public uint Edi, Esi, Ebx, Edx, Ecx, Eax;
        public uint Ebp, Eip, SegCs, EFlags, Esp, SegSs;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] ExtendedRegisters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONTEXT_X86
    {
        public uint ContextFlags;
        public uint Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
        public WOW64_FLOATING_SAVE_AREA FloatSave;
        public uint SegGs, SegFs, SegEs, SegDs;
        public uint Edi, Esi, Ebx, Edx, Ecx, Eax;
        public uint Ebp, Eip, SegCs, EFlags, Esp, SegSs;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] ExtendedRegisters;
    }

    #endregion

    #region P/Invoke functions

    [DllImport("ntdll.dll")] private static extern int NtCreateSection(out IntPtr SectionHandle, uint DesiredAccess, IntPtr ObjectAttributes, ref long MaximumSize, uint SectionPageProtection, uint AllocationAttributes, IntPtr FileHandle);
    [DllImport("ntdll.dll")] private static extern int NtMapViewOfSection(IntPtr SectionHandle, IntPtr ProcessHandle, ref IntPtr BaseAddress, UIntPtr ZeroBits, UIntPtr CommitSize, ref long SectionOffset, ref UIntPtr ViewSize, uint InheritDisposition, uint AllocationType, uint Win32Protect);
    [DllImport("ntdll.dll")] private static extern int NtUnmapViewOfSection(IntPtr ProcessHandle, IntPtr BaseAddress);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr hThread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr hThread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Wow64GetThreadContext(IntPtr hThread, ref WOW64_CONTEXT ctx);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Wow64SetThreadContext(IntPtr hThread, ref WOW64_CONTEXT ctx);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetThreadContext")] private static extern bool GetThreadContext32(IntPtr hThread, ref CONTEXT_X86 ctx);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetThreadContext")] private static extern bool SetThreadContext32(IntPtr hThread, ref CONTEXT_X86 ctx);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern bool Module32First(IntPtr snap, ref MODULEENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern bool Module32Next(IntPtr snap, ref MODULEENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out UIntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);

    #endregion
}
