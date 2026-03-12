#pragma once
#include <windows.h>

namespace TalosForge { namespace Native { namespace Core {

    bool InstallVectoredHandler();
    void RemoveVectoredHandler();
    DWORD GetWorkerThreadId(); // if you have a dedicated worker thread for VEH (optional)

}}}