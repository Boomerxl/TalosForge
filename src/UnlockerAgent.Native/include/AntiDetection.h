#pragma once
#include <windows.h>

namespace TalosForge { namespace Native {

    // IAT redirection for GetThreadContext / NtGetContextThread (safe in DllMain)
    void PatchContextIAT();

    // PEB cleaning: BeingDebugged = 0, NtGlobalFlag, heap flags
    void CleanPebDebugFlags();

    // Hide a module from the three PEB loader lists
    bool HideModuleFromPeb(HMODULE hMod);

    // Erase DOS/PE headers from memory (call after all imports resolved)
    void ErasePeHeader(HMODULE hMod);

    // Harden module memory: remove RWX, set RX for code, RW for data
    void HardenModuleMemory(HMODULE hMod);

    // Mark a thread as "ignored" – our GetThreadContext hook will not filter its debug registers
    void IgnoreContextThread(DWORD tid);

}} // namespace