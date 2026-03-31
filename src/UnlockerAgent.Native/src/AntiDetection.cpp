#include "AntiDetection.h"
#include "HWBPManager.h"
#include "Logger.h"
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <winternl.h>
#include <ntstatus.h>
#include <mutex>
#include <vector>

#pragma comment(lib, "ntdll.lib")

extern "C" {
    NTSTATUS NTAPI NtGetContextThread(HANDLE, PCONTEXT);
}

namespace TalosForge { namespace Native {

    // ---- Real API pointers (resolved once) ----
    using GetThreadContext_t = BOOL(WINAPI*)(HANDLE, LPCONTEXT);
    using NtGetContextThread_t = NTSTATUS(NTAPI*)(HANDLE, PCONTEXT);
    using NtQueryVirtualMemory_t = NTSTATUS(NTAPI*)(HANDLE, PVOID, int, PVOID, SIZE_T, PSIZE_T);

    static GetThreadContext_t   g_realGetThreadContext   = nullptr;
    static NtGetContextThread_t g_realNtGetContextThread = nullptr;
    static NtQueryVirtualMemory_t g_realNtQueryVirtualMemory = nullptr;

    static thread_local bool s_inGetThreadContext = false;

    static std::mutex           s_ignoreMutex;
    static std::vector<DWORD>   s_ignoredThreads;

    static HMODULE              g_ourModule = nullptr;
    static BYTE*                g_ourBase   = nullptr;
    static SIZE_T               g_ourSize   = 0;

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
    //  SetOurModuleInfo – called during init so memory hooks know our range
    // -----------------------------------------------------------------
    void SetOurModuleInfo(HMODULE hMod)
    {
        g_ourModule = hMod;
        g_ourBase = reinterpret_cast<BYTE*>(hMod);

        __try {
            PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)hMod;
            if (dos->e_magic == IMAGE_DOS_SIGNATURE) {
                PIMAGE_NT_HEADERS nt = (PIMAGE_NT_HEADERS)(g_ourBase + dos->e_lfanew);
                if (nt->Signature == IMAGE_NT_SIGNATURE)
                    g_ourSize = nt->OptionalHeader.SizeOfImage;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}

        if (g_ourSize == 0)
            g_ourSize = 0x100000;
    }

    // -----------------------------------------------------------------
    //  CleanOurDebugRegisters – zero DR slots that belong to our HWBP engine
    // -----------------------------------------------------------------
    static void CleanOurDebugRegisters(HANDLE hThread, LPCONTEXT lpContext)
    {
        if (!lpContext) return;
        if (!(lpContext->ContextFlags & CONTEXT_DEBUG_REGISTERS)) return;

        DWORD tid = hThread ? GetThreadId(hThread) : 0;
        if (!tid) return;

        DWORD_PTR* dr[] = { &lpContext->Dr0, &lpContext->Dr1, &lpContext->Dr2, &lpContext->Dr3 };
        for (int i = 0; i < 4; i++) {
            if (*dr[i] == 0) continue;
            int outIdx = -1;
            if (Core::HWBPManager::IsOurBreakpoint(tid, (void*)*dr[i], outIdx)) {
                *dr[i] = 0;
                lpContext->Dr7 &= ~(1UL << (2 * i));
                lpContext->Dr7 &= ~(0xFUL << (16 + 4 * i));
            }
        }

        lpContext->Dr6 = 0;
    }

    // -----------------------------------------------------------------
    //  Hooked GetThreadContext
    // -----------------------------------------------------------------
    static BOOL WINAPI Hooked_GetThreadContext(HANDLE hThread, LPCONTEXT lpContext)
    {
        if (!g_realGetThreadContext) {
            HMODULE hK = GetModuleHandleA("kernel32.dll");
            if (hK) g_realGetThreadContext = (GetThreadContext_t)GetProcAddress(hK, "GetThreadContext");
        }
        if (!g_realGetThreadContext) { SetLastError(ERROR_PROC_NOT_FOUND); return FALSE; }

        if (!hThread) return g_realGetThreadContext(hThread, lpContext);
        DWORD tid = GetThreadId(hThread);
        if (tid && IsIgnoredThread(tid)) return g_realGetThreadContext(hThread, lpContext);

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
    //  Hooked NtGetContextThread
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
    //  Hooked NtQueryVirtualMemory – hide our memory regions
    // -----------------------------------------------------------------
    static NTSTATUS NTAPI Hooked_NtQueryVirtualMemory(
        HANDLE ProcessHandle,
        PVOID BaseAddress,
        int MemoryInformationClass,
        PVOID MemoryInformation,
        SIZE_T MemoryInformationLength,
        PSIZE_T ReturnLength)
    {
        if (!g_realNtQueryVirtualMemory) {
            HMODULE hN = GetModuleHandleA("ntdll.dll");
            if (hN) g_realNtQueryVirtualMemory = (NtQueryVirtualMemory_t)GetProcAddress(hN, "NtQueryVirtualMemory");
        }
        if (!g_realNtQueryVirtualMemory) return STATUS_UNSUCCESSFUL;

        NTSTATUS st = g_realNtQueryVirtualMemory(ProcessHandle, BaseAddress, MemoryInformationClass, MemoryInformation, MemoryInformationLength, ReturnLength);

        if (NT_SUCCESS(st) && MemoryInformationClass == 0 && g_ourBase && g_ourSize > 0) {
            BYTE* queryAddr = reinterpret_cast<BYTE*>(BaseAddress);
            if (queryAddr >= g_ourBase && queryAddr < g_ourBase + g_ourSize) {
                MEMORY_BASIC_INFORMATION* mbi = reinterpret_cast<MEMORY_BASIC_INFORMATION*>(MemoryInformation);
                if (MemoryInformationLength >= sizeof(MEMORY_BASIC_INFORMATION)) {
                    mbi->Type = MEM_IMAGE;
                    mbi->Protect = PAGE_READONLY;
                    mbi->AllocationProtect = PAGE_READONLY;
                }
            }
        }

        return st;
    }

    // -----------------------------------------------------------------
    //  PatchContextIAT – walks the host EXE IAT and redirects
    // -----------------------------------------------------------------
    void PatchContextIAT()
    {
        HMODULE hMod = GetModuleHandleA(NULL);
        if (!hMod) return;

        PIMAGE_DOS_HEADER dos = (PIMAGE_DOS_HEADER)hMod;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
        PIMAGE_NT_HEADERS nt = (PIMAGE_NT_HEADERS)((BYTE*)hMod + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE) return;

        DWORD rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
        if (!rva) return;

        if (!g_realGetThreadContext) {
            HMODULE hK = GetModuleHandleA("kernel32.dll");
            if (hK) g_realGetThreadContext = (GetThreadContext_t)GetProcAddress(hK, "GetThreadContext");
        }
        if (!g_realNtGetContextThread) {
            HMODULE hN = GetModuleHandleA("ntdll.dll");
            if (hN) g_realNtGetContextThread = (NtGetContextThread_t)GetProcAddress(hN, "NtGetContextThread");
        }
        if (!g_realNtQueryVirtualMemory) {
            HMODULE hN = GetModuleHandleA("ntdll.dll");
            if (hN) g_realNtQueryVirtualMemory = (NtQueryVirtualMemory_t)GetProcAddress(hN, "NtQueryVirtualMemory");
        }

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

                void* hookTarget = nullptr;
                if (_stricmp((char*)ibn->Name, "GetThreadContext") == 0)
                    hookTarget = (void*)Hooked_GetThreadContext;
                else if (_stricmp((char*)ibn->Name, "NtGetContextThread") == 0)
                    hookTarget = (void*)Hooked_NtGetContextThread;
                else if (_stricmp((char*)ibn->Name, "NtQueryVirtualMemory") == 0)
                    hookTarget = (void*)Hooked_NtQueryVirtualMemory;

                if (hookTarget) {
                    DWORD old;
                    VirtualProtect(&thunk->u1.Function, sizeof(DWORD_PTR), PAGE_READWRITE, &old);
                    thunk->u1.Function = (DWORD_PTR)hookTarget;
                    VirtualProtect(&thunk->u1.Function, sizeof(DWORD_PTR), old, &old);
                    FlushInstructionCache(GetCurrentProcess(), &thunk->u1.Function, sizeof(DWORD_PTR));
                    Log("AntiDetection: IAT patched %s\n", (char*)ibn->Name);
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
        __asm {
            mov eax, fs:[0x30]
            mov pPEB, eax
        }
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
                    return false;
                }
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
        return false;
    }

    // -----------------------------------------------------------------
    //  CleanPebDebugFlags
    // -----------------------------------------------------------------
    void CleanPebDebugFlags()
    {
        PPEB pPEB = nullptr;
#ifdef _M_IX86
        __asm {
            mov eax, fs:[0x30]
            mov pPEB, eax
        }
#else
        pPEB = (PPEB)__readgsqword(0x60);
#endif
        if (!pPEB) return;

        pPEB->BeingDebugged = 0;
        *((DWORD*)((BYTE*)pPEB + 0x68)) &= ~0x70;

        __try {
#ifdef _M_IX86
            PVOID heap = *(PVOID*)((BYTE*)pPEB + 0x18);
#else
            PVOID heap = *(PVOID*)((BYTE*)pPEB + 0x30);
#endif
            if (heap) {
                DWORD* pFlags = (DWORD*)((BYTE*)heap + 0x0C);
                DWORD* pForceFlags = (DWORD*)((BYTE*)heap + 0x10);
                *pFlags &= ~0x50000062;
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
                    newProtect = PAGE_EXECUTE_READ;
                else if (chars & IMAGE_SCN_MEM_WRITE)
                    newProtect = PAGE_READWRITE;

                DWORD old;
                VirtualProtect(addr, size, newProtect, &old);
            }
            Log("AntiDetection: memory regions hardened (no RWX)\n");
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }

}} // namespace
