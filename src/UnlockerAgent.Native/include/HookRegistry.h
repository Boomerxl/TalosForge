#pragma once
#include <windows.h>
#include <functional>
#include <unordered_map>
#include <mutex>

namespace TalosForge { namespace Native { namespace Core {

    using HookCallback = std::function<void(DWORD tid, void* address, PCONTEXT ctx)>;

    class HookRegistry {
    public:
        static bool RegisterHook(void* target, HookCallback callback);
        static void UnregisterHook(void* target);
        static bool Dispatch(DWORD tid, void* address, PCONTEXT ctx); // returns true if handled

    private:
        struct HookEntry {
            void* target;
            HookCallback callback;
        };
        static std::unordered_map<void*, HookCallback> s_hooks;
        static std::mutex s_mutex;
    };

}}} // namespace