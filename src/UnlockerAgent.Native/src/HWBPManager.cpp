#include "HWBPManager.h"
#include "Logger.h"
#include <vector>

namespace TalosForge { namespace Native { namespace Core {

    std::unordered_map<DWORD, HWBPManager::ThreadBreakpoints> HWBPManager::s_breakpoints;
    std::mutex HWBPManager::s_mutex;
    bool HWBPManager::s_initialized = false;

    bool HWBPManager::Initialize()
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        if (s_initialized) return true;
        s_breakpoints.clear();
        s_initialized = true;
        Log("HWBPManager: initialized\n");
        return true;
    }

    void HWBPManager::Shutdown()
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        // TODO: Clear all breakpoints (requires thread handles)
        s_breakpoints.clear();
        s_initialized = false;
        Log("HWBPManager: shutdown\n");
    }

    bool HWBPManager::SafeGetThreadContext(HANDLE hThread, PCONTEXT ctx)
    {
        if (!hThread || !ctx) return false;
        ctx->ContextFlags = CONTEXT_DEBUG_REGISTERS | CONTEXT_INTEGER | CONTEXT_CONTROL;
        return GetThreadContext(hThread, ctx) != FALSE;
    }

    bool HWBPManager::SafeSetThreadContext(HANDLE hThread, PCONTEXT ctx)
    {
        if (!hThread || !ctx) return false;
        return SetThreadContext(hThread, ctx) != FALSE;
    }

    bool HWBPManager::SetBreakpoint(DWORD tid, void* address, int condition, HANDLE hThread)
    {
        if (!s_initialized) {
            Log("HWBPManager: not initialized\n");
            return false;
        }
        if (!address) return false;

        HANDLE hOwnHandle = nullptr;
        bool closeHandle = false;
        if (!hThread) {
            hOwnHandle = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT, FALSE, tid);
            if (!hOwnHandle) {
                Log("HWBPManager: OpenThread failed for %u (err %u)\n", tid, GetLastError());
                return false;
            }
            hThread = hOwnHandle;
            closeHandle = true;
        }

        bool success = false;
        // Suspend the thread to modify context safely
        if (SuspendThread(hThread) == (DWORD)-1) {
            Log("HWBPManager: SuspendThread failed for %u\n", tid);
            if (closeHandle) CloseHandle(hThread);
            return false;
        }

        CONTEXT ctx = {0};
        ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(hThread, &ctx)) {
            // Find a free debug register (DR0-DR3)
            int freeIndex = -1;
            DWORD_PTR* dr[] = { &ctx.Dr0, &ctx.Dr1, &ctx.Dr2, &ctx.Dr3 };
            for (int i = 0; i < 4; i++) {
                if (*dr[i] == 0) {
                    freeIndex = i;
                    break;
                }
            }

            if (freeIndex != -1) {
                *dr[freeIndex] = (DWORD_PTR)address;
                // Set Dr7 enable bits: Lx = 1 (local), Gx = 0 (global) – we use local.
                // Each register has two bits: Lx and Gx at positions (2*i) and (2*i+1).
                ctx.Dr7 |= (1 << (2 * freeIndex)); // set local enable
                // Condition is stored in the general-purpose bits of Dr7 (4 bits per register).
                // We'll ignore condition for now (set to 0 = execute break).
                // If you need condition, you need to set the corresponding bits in Dr7.
                // We'll assume execute break (condition 0) for simplicity.
                // For condition, bits: for DR0: bits 16-17, DR1: 20-21, etc.
                // condition 0 = 00b (execute), 1 = 01b (write), 3 = 11b (read/write)
                // We'll not implement condition now to keep simple.
                if (SetThreadContext(hThread, &ctx)) {
                    success = true;
                    // Store in our map
                    {
                        std::lock_guard<std::mutex> lock(s_mutex);
                        auto& tb = s_breakpoints[tid];
                        tb.tid = tid;
                        tb.slots[freeIndex] = { address, condition, true };
                    }
                    Log("HWBPManager: breakpoint set on thread %u at %p (DR%d)\n", tid, address, freeIndex);
                } else {
                    Log("HWBPManager: SetThreadContext failed for %u\n", tid);
                }
            } else {
                Log("HWBPManager: no free debug register for thread %u\n", tid);
            }
        } else {
            Log("HWBPManager: GetThreadContext failed for %u\n", tid);
        }

        ResumeThread(hThread);
        if (closeHandle) CloseHandle(hThread);
        return success;
    }

    bool HWBPManager::RemoveBreakpoint(DWORD tid, int index, HANDLE hThread)
    {
        if (index < 0 || index > 3) return false;

        HANDLE hOwnHandle = nullptr;
        bool closeHandle = false;
        if (!hThread) {
            hOwnHandle = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT, FALSE, tid);
            if (!hOwnHandle) return false;
            hThread = hOwnHandle;
            closeHandle = true;
        }

        bool success = false;
        if (SuspendThread(hThread) == (DWORD)-1) {
            if (closeHandle) CloseHandle(hThread);
            return false;
        }

        CONTEXT ctx = {0};
        ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(hThread, &ctx)) {
            DWORD_PTR* dr[] = { &ctx.Dr0, &ctx.Dr1, &ctx.Dr2, &ctx.Dr3 };
            *dr[index] = 0;
            // Clear enable bits in Dr7
            ctx.Dr7 &= ~(1 << (2 * index)); // clear local enable
            // Optionally clear condition bits? Not needed.
            if (SetThreadContext(hThread, &ctx)) {
                success = true;
                {
                    std::lock_guard<std::mutex> lock(s_mutex);
                    auto it = s_breakpoints.find(tid);
                    if (it != s_breakpoints.end()) {
                        it->second.slots[index].active = false;
                        it->second.slots[index].address = nullptr;
                    }
                }
                Log("HWBPManager: breakpoint removed from thread %u DR%d\n", tid, index);
            }
        }

        ResumeThread(hThread);
        if (closeHandle) CloseHandle(hThread);
        return success;
    }

    bool HWBPManager::IsOurBreakpoint(DWORD tid, void* address, int& outIndex)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        auto it = s_breakpoints.find(tid);
        if (it == s_breakpoints.end()) return false;
        for (int i = 0; i < 4; i++) {
            if (it->second.slots[i].active && it->second.slots[i].address == address) {
                outIndex = i;
                return true;
            }
        }
        return false;
    }

    size_t HWBPManager::GetActiveCount()
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        size_t count = 0;
        for (const auto& pair : s_breakpoints) {
            for (int i = 0; i < 4; i++) {
                if (pair.second.slots[i].active) count++;
            }
        }
        return count;
    }

}}} // namespace