#pragma once
#include <windows.h>
#include <unordered_map>
#include <mutex>

namespace TalosForge { namespace Native { namespace Core {

    class HWBPManager {
    public:
        static bool Initialize();
        static void Shutdown();

        // Set a hardware breakpoint on a specific thread.
        // condition: 0 = execute, 1 = write, 2 = read/write, 3 = (depends)
        // Returns true if successfully set.
        static bool SetBreakpoint(DWORD tid, void* address, int condition, HANDLE hThread = nullptr);

        // Remove breakpoint at given index (0-3) for a thread.
        static bool RemoveBreakpoint(DWORD tid, int index, HANDLE hThread = nullptr);

        // Check if a given address is one of our breakpoints on a thread.
        static bool IsOurBreakpoint(DWORD tid, void* address, int& outIndex);

        // Get number of active breakpoints across all threads.
        static size_t GetActiveCount();

        // For internal use: get the thread context safely.
        static bool SafeGetThreadContext(HANDLE hThread, PCONTEXT ctx);
        static bool SafeSetThreadContext(HANDLE hThread, PCONTEXT ctx);

    private:
        struct Breakpoint {
            void* address;
            int condition;
            bool active;
        };

        struct ThreadBreakpoints {
            Breakpoint slots[4];
            DWORD tid;
        };

        static std::unordered_map<DWORD, ThreadBreakpoints> s_breakpoints;
        static std::mutex s_mutex;
        static bool s_initialized;
    };

}}} // namespace