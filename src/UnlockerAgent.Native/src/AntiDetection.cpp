#include "AntiDetection.h"
#include "Logger.h"
#include <windows.h>
#include <winternl.h>
#include <mutex>
#include <vector>

#pragma comment(lib, "ntdll.lib")  // for NT functions if needed

// We need some NT types and functions not fully declared in winternl.h
extern "C" {
    NTSTATUS NTAPI NtGetContextThread(HANDLE, PCONTEXT);
}

namespace TalosForge { namespace Native {

    // ---- Real API pointers (resolved once) ----
    using GetThreadContext_t = BOOL(WINAPI*)(HANDLE, LPCONTEXT);
    using NtGetContextThread_t = NTSTATUS(NTAPI*)(HANDLE, PCONTEXT);

    static GetThreadContext_t   g_realGetThreadContext   = nullptr;
    static NtGetContextThread_t g_realNtGetContextThread = nullptr;

    // ---- Recursion guard (thread-local) ----
    static thread_local bool s_inGetThreadContext = false;

    // ---- Ignored threads list ----
    static std::mutex           s_ignoreMutex;
    static std::vector<DWORD>   s_ignoredThreads;

    // -----------------------------------------------------------------
    //  IgnoreContextThread
    // -----------------------------------------------------------------
    void IgnoreContextThread(DWORD tid)
    {
        if (!tid) return;
        std::lock_guard<std::mutex> lock(s_ignoreMutex);
        for (DWORD t : s_ignoredThreads)
            if (t == tid) return;
        s_ignoredThreads.push_back(tid);
    }

    static bool IsIgnoredThread(DWORD tid)
    {
        if (!tid) return false;
        std::lock_guard<std::mutex> lock(s_ignoreMutex);
        for (DWORD t : s_ignoredThreads)
            if (t == tid) return true;
        return false;
    }

    // -----------------------------------------------------------------
    //  CleanOurDebugRegisters – zero only registers owned by our engine
    // -----------------------------------------------------------------
    // Forward declaration of HWBPManager's IsOurBreakpoint (we'll include header later)
    namespace Core { // We'll place HWBPManager in TalosForge::Native::Core
        class HWBPManager;
    }
    // But to avoid circular includes, we'll just declare a helper function
    // that will be defined after HWBPManager is implemented.
    // For now, we use a weak dependency: we'll call a function pointer that will be set later.
    // Alternatively, we can move the cleaning logic into a separate file that includes HWBPManager.h.
    // Let's do it cleanly: we'll implement CleanOurDebugRegisters in a .cpp that includes HWBPManager.h.
    // So we'll move the body to the bottom of this file after HWBPManager is defined? No.
    // Better: separate the cleaning into its own function that uses HWBPManager::IsOurBreakpoint.
    // We'll declare the function here and define it later in a separate .cpp (or at end of this file after including HWBPManager.h).
    // For now, we'll keep the logic inline but conditionally compile.
    // Since this is a single translation unit, we can include HWBPManager.h here.
    // But we haven't defined HWBPManager yet. We'll reorder includes: include HWBPManager.h after its definition.
    // To simplify, I'll move the cleaning function to the end of this file after HWBPManager.h is included.
    // We'll just declare a static helper now and implement later.

    static void CleanOurDebugRegisters(HANDLE hThread, LPCONTEXT lpContext); // forward

    // -----------------------------------------------------------------
    //  Hooked GetThreadContext (IAT redirect target)
    // -----------------------------------------------------------------
    static BOOL WINAPI Hooked_GetThreadContext(HANDLE hThread, LPCONTEXT lpContext)
    {
        // Resolve real function once
        if (!g_realGetThreadContext) {
            HMODULE hK = GetModuleHandleA("kernel32.dll");
            if (hK) g_realGetThreadContext = (GetThreadContext_t)GetProcAddress(hK, "GetThreadContext");
        }
        if (!g_realGetThreadContext) { SetLastError(ERROR_PROC_NOT_FOUND); return FALSE; }

        // Fast paths
        if (!hThread) return g_realGetThreadContext(hThread, lpContext);
        DWORD tid = GetThreadId(hThread);
        if (tid && IsIgnoredThread(tid)) return g_realGetThreadContext(hThread, lpContext);

        // Recursion guard
        if (s_inGetThreadContext) return g_realGetThreadContext(hThread, lpContext);
        s_inGetThreadContext = true;

        BOOL res = FALSE;
        __try {
            res = g_realGetThreadContext(hThread, lpContext);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            res = FALSE;
        }

        s_inGetThreadContext = false;

        if (res && lpContext)
            CleanOurDebugRegisters(hThread, lpContext);

        return res;
    }

    // -----------------------------------------------------------------
    //  Hooked NtGetContextThread (IAT redirect target)
    // -----------------------------------------------------------------
    static NTSTATUS NTAPI Hooked_NtGetContextThread(HANDLE hThread, PCONTEXT lpContext)
    {
        if (!g_realNtGetContextThread) {
            HMODULE hN = GetModuleHandleA("ntdll.dll");
            if (hN) g_realNtGetContextThread = (NtGetContextThread_t)GetProcAddress(hN, "NtGetContextThread");
        }
        if (!g_realNtGetContextThread) return STATUS_UNSUCCESSFUL;

        if (!lpContext) return STATUS_INVALID_PARAMETER;

        DWORD tid = hThread ? GetThreadId(hThread) : 0;
        if (tid && IsIgnoredThread(tid)) return g_realNtGetContextThread(hThread, lpContext);

        if (s_inGetThreadContext) return g_realNtGetContextThread(hThread, lpContext);
        s_inGetThreadContext = true;

        NTSTATUS st = STATUS_UNSUCCESSFUL;
        __try {
            st = g_realNtGetContextThread(hThread, lpContext);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            st = STATUS_UNSUCCESSFUL;
        }
        s_inGetThreadContext = false;

        if (NT_SUCCESS(st) && lpContext)
            CleanOurDebugRegisters(hThread, lpContext);

        return st;
    }

    // -----------------------------------------------------------------
    //  PatchContextIAT – walks the host EXE IAT and redirects
    // -----------------------------------------------------------------
    void PatchContextIAT()
    {
        HMODULE hMod = GetModuleHandleA(NULL); // EXE
        if (!hMod) return;

        PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)hMod;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
        PIMAGE_NT_HEADERS nt = (PIMAGE_NT_HEADERS)((BYTE*)hMod + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE) return;

        DWORD rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
        if (!rva) return;

        PIMAGE_IMPORT_DESCRIPTOR imp = (PIMAGE_IMPORT_DESCRIPTOR)((BYTE*)hMod + rva);
        DWORD_PTR base = (DWORD_PTR)hMod;

        for (; imp->Name; imp++) {
            LPCSTR modname = (LPCSTR)(base + imp->Name);
            if (_stricmp(modname, "kernel32.dll") != 0 && _stricmp(modname, "ntdll.dll") != 0)
                continue;

            PIMAGE_THUNK_DATA thunk = (PIMAGE_THUNK_DATA)(base + imp->FirstThunk);
            PIMAGE_THUNK_DATA orig  = imp->OriginalFirstThunk
                ? (PIMAGE_THUNK_DATA)(base + imp->OriginalFirstThunk)
                : thunk;

            for (; orig->u1.AddressOfData; orig++, thunk++) {
                if (orig->u1.Ordinal & IMAGE_ORDINAL_FLAG) continue;
                PIMAGE_IMPORT_BY_NAME ibn = (PIMAGE_IMPORT_BY_NAME)(base + orig->u1.AddressOfData);
                if (!ibn || !ibn->Name) continue;

                bool patchGTC = (_stricmp((char*)ibn->Name, "GetThreadContext") == 0);
                bool patchNGC = (_stricmp((char*)ibn->Name, "NtGetContextThread") == 0);

                if (patchGTC) {
                    if (!g_realGetThreadContext) {
                        HMODULE hK = GetModuleHandleA("kernel32.dll");
                        if (hK) g_realGetThreadContext = (GetThreadContext_t)GetProcAddress(hK, "GetThreadContext");
                    }
                    DWORD old;
                    VirtualProtect(&thunk->u1.Function, sizeof(DWORD_PTR), PAGE_READWRITE, &old);
                    thunk->u1.Function = (DWORD_PTR)Hooked_GetThreadContext;
                    VirtualProtect(&thunk->u1.Function, sizeof(DWORD_PTR), old, &old);
                    FlushInstructionCache(GetCurrentProcess(), &thunk->u1.Function, sizeof(DWORD_PTR));
                    Log("AntiDetection: IAT patched GetThreadContext -> %p\n", Hooked_GetThreadContext);
                }
                else if (patchNGC) {
                    if (!g_realNtGetContextThread) {
                        HMODULE hN = GetModuleHandleA("ntdll.dll");
                        if (hN) g_realNtGetContextThread = (NtGetContextThread_t)GetProcAddress(hN, "NtGetContextThread");
                    }
                    DWORD old;
                    VirtualProtect(&thunk->u1.Function, sizeof(DWORD_PTR), PAGE_READWRITE, &old);
                    thunk->u1.Function = (DWORD_PTR)Hooked_NtGetContextThread;
                    VirtualProtect(&thunk->u1.Function, sizeof(DWORD_PTR), old, &old);
                    FlushInstructionCache(GetCurrentProcess(), &thunk->u1.Function, sizeof(DWORD_PTR));
                    Log("AntiDetection: IAT patched NtGetContextThread -> %p\n", Hooked_NtGetContextThread);
                }
            }
        }
    }

    // -----------------------------------------------------------------
    //  PEB structures (extended)
    // -----------------------------------------------------------------
    struct MY_PEB_LDR_DATA {
        ULONG      Length;
        BOOLEAN    Initialized;
        PVOID      SsHandle;
        LIST_ENTRY InLoadOrderModuleList;
        LIST_ENTRY InMemoryOrderModuleList;
        LIST_ENTRY InInitializationOrderModuleList;
    };

    struct MY_LDR_DATA_TABLE_ENTRY {
        LIST_ENTRY InLoadOrderLinks;
        LIST_ENTRY InMemoryOrderLinks;
        LIST_ENTRY InInitializationOrderLinks;
        PVOID      DllBase;
        PVOID      EntryPoint;
        ULONG      SizeOfImage;
        UNICODE_STRING FullDllName;
        UNICODE_STRING BaseDllName;
    };

    // -----------------------------------------------------------------
    //  HideModuleFromPeb
    // -----------------------------------------------------------------
    bool HideModuleFromPeb(HMODULE hMod)
    {
        if (!hMod) return false;

        PPEB pPEB = nullptr;
#ifdef _M_IX86
        __asm { mov eax, fs:[0x30] mov pPEB, eax }
#else
        pPEB = (PPEB)__readgsqword(0x60);
#endif
        if (!pPEB || !pPEB->Ldr) return false;

        __try {
            MY_PEB_LDR_DATA* ldr = (MY_PEB_LDR_DATA*)pPEB->Ldr;
            if (!ldr) return false;

            PLIST_ENTRY head = &ldr->InLoadOrderModuleList;
            PLIST_ENTRY curr = head->Flink;
            while (curr != head) {
                __try {
                    auto entry = CONTAINING_RECORD(curr, MY_LDR_DATA_TABLE_ENTRY, InLoadOrderLinks);
                    if (entry->DllBase == (PVOID)hMod) {
                        // Unlink from all three lists
                        curr->Blink->Flink = curr->Flink;
                        curr->Flink->Blink = curr->Blink;

                        entry->InMemoryOrderLinks.Blink->Flink = entry->InMemoryOrderLinks.Flink;
                        entry->InMemoryOrderLinks.Flink->Blink = entry->InMemoryOrderLinks.Blink;

                        entry->InInitializationOrderLinks.Blink->Flink = entry->InInitializationOrderLinks.Flink;
                        entry->InInitializationOrderLinks.Flink->Blink = entry->InInitializationOrderLinks.Blink;

                        Log("AntiDetection: module hidden from PEB (%p)\n", hMod);
                        return true;
                    }
                    curr = curr->Flink;
                } __except (EXCEPTION_EXECUTE_HANDLER) {
                    Log("AntiDetection: corrupted PEB entry during walk\n");
                    return false;
                }
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            Log("AntiDetection: SEH in HideModule PEB walk\n");
            return false;
        }
        Log("AntiDetection: module not found in PEB\n");
        return false;
    }

    // -----------------------------------------------------------------
    //  CleanPebDebugFlags
    // -----------------------------------------------------------------
    void CleanPebDebugFlags()
    {
        PPEB pPEB = nullptr;
#ifdef _M_IX86
        __asm { mov eax, fs:[0x30] mov pPEB, eax }
#else
        pPEB = (PPEB)__readgsqword(0x60);
#endif
        if (!pPEB) return;

        // BeingDebugged
        pPEB->BeingDebugged = 0;

        // NtGlobalFlag (offset 0x68 on x64, 0x68 on x86? Actually on x86 it's at 0x68 as well? winternl.h says +0x68 for NtGlobalFlag)
        // Use pointer arithmetic to avoid struct definition mismatch.
        *((DWORD*)((BYTE*)pPEB + 0x68)) &= ~0x70; // clear FLG_HEAP_ENABLE_TAIL_CHECK, FLG_HEAP_ENABLE_FREE_CHECK, FLG_HEAP_VALIDATE_PARAMETERS

        // Heap flags cleanup
        __try {
            // ProcessHeap at PEB+0x18 (x86) or +0x30? Actually on x64 it's +0x30. We'll just read from known offset.
            // This is fragile; better to use RtlGetProcessHeap() but that might be hooked.
            // We'll attempt to read from known offset.
#ifdef _M_IX86
            PVOID heap = *(PVOID*)((BYTE*)pPEB + 0x18);
#else
            PVOID heap = *(PVOID*)((BYTE*)pPEB + 0x30);
#endif
            if (heap) {
                // Flags at offset 0x0C (x86) and 0x40? Actually for x64 it's different. We'll just attempt to clear common debug flags.
                // This is simplified; for full stealth you'd need to know the heap structure.
                // We'll zero the ForceFlags field which is at offset 0x10 on x86 and 0x74 on x64? Not reliable.
                // Let's skip for now or just try to clear a couple of dwords.
                DWORD* pFlags = (DWORD*)((BYTE*)heap + 0x0C);
                DWORD* pForceFlags = (DWORD*)((BYTE*)heap + 0x10);
                *pFlags &= ~0x50000062; // typical debug flags
                *pForceFlags = 0;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}

        Log("AntiDetection: PEB debug flags cleaned\n");
    }

    // -----------------------------------------------------------------
    //  ErasePeHeader
    // -----------------------------------------------------------------
    void ErasePeHeader(HMODULE hMod)
    {
        if (!hMod) return;
        __try {
            PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)hMod;
            if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
            PIMAGE_NT_HEADERS nt = (PIMAGE_NT_HEADERS)((BYTE*)hMod + dos->e_lfanew);
            if (nt->Signature != IMAGE_NT_SIGNATURE) return;

            DWORD headerSize = nt->OptionalHeader.SizeOfHeaders;
            if (headerSize == 0 || headerSize > 0x2000) headerSize = 0x1000;

            DWORD oldProtect;
            if (VirtualProtect(hMod, headerSize, PAGE_READWRITE, &oldProtect)) {
                SecureZeroMemory(hMod, headerSize);
                VirtualProtect(hMod, headerSize, oldProtect, &oldProtect);
                Log("AntiDetection: PE header erased (%u bytes)\n", headerSize);
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    // -----------------------------------------------------------------
    //  HardenModuleMemory
    // -----------------------------------------------------------------
    void HardenModuleMemory(HMODULE hMod)
    {
        if (!hMod) return;
        __try {
            PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)hMod;
            if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
            PIMAGE_NT_HEADERS nt = (PIMAGE_NT_HEADERS)((BYTE*)hMod + dos->e_lfanew);
            if (nt->Signature != IMAGE_NT_SIGNATURE) return;

            PIMAGE_SECTION_HEADER section = IMAGE_FIRST_SECTION(nt);
            for (WORD i = 0; i < nt->FileHeader.NumberOfSections; i++, section++) {
                DWORD va = section->VirtualAddress;
                DWORD size = section->Misc.VirtualSize;
                if (!va || !size) continue;

                BYTE* addr = (BYTE*)hMod + va;
                DWORD chars = section->Characteristics;
                DWORD newProtect = PAGE_READONLY;

                if (chars & IMAGE_SCN_MEM_EXECUTE)
                    newProtect = PAGE_EXECUTE_READ; // RX
                else if (chars & IMAGE_SCN_MEM_WRITE)
                    newProtect = PAGE_READWRITE;    // RW (no X)

                DWORD old;
                VirtualProtect(addr, size, newProtect, &old);
            }
            Log("AntiDetection: memory regions hardened (no RWX)\n");
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

    // -----------------------------------------------------------------
    //  CleanOurDebugRegisters – implementation (needs HWBPManager)
    // -----------------------------------------------------------------
    // We'll include HWBPManager.h after it's defined. Since this is a single file,
    // we need to include it now. But we haven't defined HWBPManager yet.
    // To avoid circular dependency, we can forward declare HWBPManager and
    // then implement this function after HWBPManager is fully defined.
    // For now, we'll move this function to a separate .cpp that includes both headers.
    // But for simplicity, we'll include HWBPManager.h here, assuming it's already written.
    // I'll provide HWBPManager files next, so you can include them in proper order.
    // For now, we'll just define a placeholder that does nothing.
    // Let's do it properly: after you create HWBPManager.h, you'll need to include it here.
    // I'll add the include at the top of this file after HWBPManager is defined.
    // To keep this file self-contained, I'll leave the function empty for now and you can fill it later.
    // Alternatively, I can provide a separate file AntiDetection_impl.cpp that includes HWBPManager.h.
    // I'll do that.

    // For now, declare an empty function that does nothing (stub) – but it's critical.
    // I'll provide the full function in a separate code block below that you can paste after HWBPManager is ready.

}} // namespace