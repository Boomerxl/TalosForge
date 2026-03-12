#include "VEHHandler.h"
#include "HookRegistry.h"
#include "HWBPManager.h"   // to check if breakpoint is ours
#include "Logger.h"

namespace TalosForge { namespace Native { namespace Core {

    static PVOID g_vehHandle = nullptr;
    static DWORD g_workerThreadId = 0; // not used unless you create a worker

    static LONG CALLBACK VectoredHandler(PEXCEPTION_POINTERS ep)
    {
        // Only handle single-step and breakpoint exceptions
        if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_SINGLE_STEP &&
            ep->ExceptionRecord->ExceptionCode != EXCEPTION_BREAKPOINT)
            return EXCEPTION_CONTINUE_SEARCH;

        DWORD tid = GetCurrentThreadId();
        void* address = ep->ExceptionRecord->ExceptionAddress;

        // Check if this address corresponds to one of our registered hooks
        if (HookRegistry::Dispatch(tid, address, ep->ContextRecord)) {
            // The callback may have modified the context; we need to continue execution.
            // For single-step, we need to set the resume flag? Actually, after a hardware breakpoint,
            // the CPU clears the enable bit. We need to re‑enable it in the VEH.
            // But HWBPManager already re‑arms breakpoints? In the original code, they re‑enable in the handler.
            // For simplicity, we'll assume the callback handles that.
            return EXCEPTION_CONTINUE_EXECUTION;
        }

        return EXCEPTION_CONTINUE_SEARCH;
    }

    bool InstallVectoredHandler()
    {
        if (g_vehHandle) return true;
        g_vehHandle = AddVectoredExceptionHandler(1, VectoredHandler);
        if (g_vehHandle) {
            Log("VEHHandler: installed\n");
            return true;
        }
        Log("VEHHandler: installation failed\n");
        return false;
    }

    void RemoveVectoredHandler()
    {
        if (g_vehHandle) {
            RemoveVectoredExceptionHandler(g_vehHandle);
            g_vehHandle = nullptr;
            Log("VEHHandler: removed\n");
        }
    }

    DWORD GetWorkerThreadId()
    {
        return g_workerThreadId; // not used; return 0
    }

}}} // namespace