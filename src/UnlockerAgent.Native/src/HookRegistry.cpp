#include "HookRegistry.h"
#include "Logger.h"

namespace TalosForge { namespace Native { namespace Core {

    std::unordered_map<void*, HookCallback> HookRegistry::s_hooks;
    std::mutex HookRegistry::s_mutex;

    bool HookRegistry::RegisterHook(void* target, HookCallback callback)
    {
        if (!target || !callback) return false;
        std::lock_guard<std::mutex> lock(s_mutex);
        s_hooks[target] = callback;
        Log("HookRegistry: registered hook at %p\n", target);
        return true;
    }

    void HookRegistry::UnregisterHook(void* target)
    {
        if (!target) return;
        std::lock_guard<std::mutex> lock(s_mutex);
        s_hooks.erase(target);
    }

    bool HookRegistry::Dispatch(DWORD tid, void* address, PCONTEXT ctx)
    {
        if (!address) return false;
        std::lock_guard<std::mutex> lock(s_mutex);
        auto it = s_hooks.find(address);
        if (it != s_hooks.end()) {
            it->second(tid, address, ctx);
            return true;
        }
        return false;
    }

}}} // namespace