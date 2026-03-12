#include "ThreadEnumerator.h"
#include <tlhelp32.h>

namespace TalosForge { namespace Native {

    bool EnumerateProcessThreads(DWORD pid, std::vector<DWORD>& outTids)
    {
        outTids.clear();
        HANDLE hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, pid);
        if (hSnap == INVALID_HANDLE_VALUE)
            return false;

        THREADENTRY32 te = { sizeof(THREADENTRY32) };
        if (Thread32First(hSnap, &te)) {
            do {
                if (te.th32OwnerProcessID == pid) {
                    outTids.push_back(te.th32ThreadID);
                }
            } while (Thread32Next(hSnap, &te));
        }
        CloseHandle(hSnap);
        return true;
    }

}} // namespace