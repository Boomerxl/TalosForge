#include "WoWHooks.h"
#include "Logger.h"
#include "HWBPManager.h"
#include "VEHHandler.h"
#include "HookRegistry.h"
#include "ThreadEnumerator.h"
#include "AntiDetection.h"   // for IgnoreContextThread
#include <Windows.h>
#include <vector>
#include <unordered_set>
#include <mutex>

namespace TalosForge { namespace Native {

    // Feature toggles (can be made configurable)
    static size_t g_maxWoWHooksHWBPThreads = 0;       // 0 = disabled (use dynamic)
    static bool   g_enableWoWHooksSafeMode   = false; // only current+worker
    static bool   g_enableWoWHooksSelfTest   = true;  // run self-test

    // -----------------------------------------------------------------
    //  Local test function
    // -----------------------------------------------------------------
    static void WoWLocalHWBPTest()
    {
        volatile int marker = 0;
        marker++;
        Sleep(200);
    }

    // -----------------------------------------------------------------
    //  InstallWoWHooks
    // -----------------------------------------------------------------
    void InstallWoWHooks()
    {
        Log("WoWHooks: InstallWoWHooks begin\n");

        // Get ws2_32!recv address
        HMODULE hWs2 = GetModuleHandleA("ws2_32.dll");
        if (!hWs2) {
            Log("WoWHooks: ws2_32.dll not loaded\n");
            return;
        }
        FARPROC recvAddr = GetProcAddress(hWs2, "recv");
        if (!recvAddr) {
            Log("WoWHooks: GetProcAddress(recv) failed\n");
            return;
        }
        Log("WoWHooks: recv = %p\n", recvAddr);

        // Register a hook for recv that dynamically adds breakpoints to new threads
        static std::unordered_set<DWORD> s_hwbpThreads;
        static std::mutex s_hwbpLock;
        static size_t s_hwbpCount = 0;

        Core::HookRegistry::RegisterHook((void*)recvAddr, [recvAddr](DWORD tid, void* addr, PCONTEXT ctx) {
            Log("WoWHooks: recv hit on thread %u at %p\n", tid, addr);

            bool needSet = false;
            {
                std::lock_guard<std::mutex> lock(s_hwbpLock);
                if (s_hwbpThreads.find(tid) == s_hwbpThreads.end()) {
                    if (g_enableWoWHooksSafeMode) {
                        needSet = true;
                    } else if (g_maxWoWHooksHWBPThreads > 0 && s_hwbpCount < g_maxWoWHooksHWBPThreads) {
                        needSet = true;
                    }
                    if (needSet) {
                        s_hwbpThreads.insert(tid);
                        ++s_hwbpCount;
                    }
                }
            }

            if (needSet) {
                if (Core::HWBPManager::SetBreakpoint(tid, (void*)recvAddr, 0)) {
                    Log("WoWHooks: dynamic SetBreakpoint succeeded on thread %u\n", tid);
                } else {
                    Log("WoWHooks: dynamic SetBreakpoint FAILED on thread %u\n", tid);
                }
            }
        });

        // HWBP on NtGetContextThread prologue (if you have SyscallStub defense, add later)
        // For now skip.

        // --- HWBP on NetClient::ProcessMessage (DR2) for Warden passive monitoring ---
        // Hardcoded address for 3.3.5a – replace with pattern scan if needed.
        const uintptr_t NET_CLIENT_PROCESS_MESSAGE = 0x00C631E0; // example, adjust!

        struct ProcessMessageSEH {
            static bool ReadPacketFields(PCONTEXT ctx,
                                         uint32_t& outOpcode,
                                         uint8_t*& outBuffer,
                                         uint32_t& outRemaining)
            {
                __try {
                    // Assuming thiscall with this in ecx, first arg on stack? Actually cdecl? Need to check.
                    // In original code they used ctx->Esp + 4 etc. We'll keep as is.
                    outOpcode = *(uint32_t*)(ctx->Esp + 4);
                    if (outOpcode != 0x02E6)
                        return false;

                    uint32_t pDataStore = *(uint32_t*)(ctx->Esp + 0xC);
                    if (!pDataStore)
                        return false;

                    uint8_t* buffer  = *(uint8_t**)(pDataStore + 0x04);
                    uint32_t size    = *(uint32_t*)(pDataStore + 0x10);
                    uint32_t readPos = *(uint32_t*)(pDataStore + 0x14);
                    if (!buffer || size <= readPos)
                        return false;

                    outBuffer    = buffer + readPos;
                    outRemaining = size - readPos;
                    return true;
                } __except (EXCEPTION_EXECUTE_HANDLER) {
                    return false;
                }
            }
        };

        void* pmAddr = (void*)NET_CLIENT_PROCESS_MESSAGE;
        Core::HookRegistry::RegisterHook(pmAddr, [](DWORD tid, void* addr, PCONTEXT ctx) {
            uint32_t opcode = 0;
            uint8_t* payloadPtr = nullptr;
            uint32_t remaining = 0;
            if (!ProcessMessageSEH::ReadPacketFields(ctx, opcode, payloadPtr, remaining))
                return;

            constexpr uint32_t MAX_SAFE_WARDEN_PAYLOAD = 0x4000;
            if (remaining == 0 || remaining > MAX_SAFE_WARDEN_PAYLOAD)
                return;

            // Here you can forward the packet to your Warden monitor
            Log("WoWHooks: Warden packet received, size=%u\n", remaining);
            // TODO: call WardenMonitor::OnRecvPacket(payloadPtr, remaining);
        });

        if (Core::HWBPManager::SetBreakpoint(GetCurrentThreadId(), pmAddr, 2)) {
            Log("WoWHooks: ProcessMessage HWBP installed (DR2)\n");
        } else {
            Log("WoWHooks: ProcessMessage HWBP FAILED\n");
        }

        // Self-test
        if (g_enableWoWHooksSelfTest) {
            Log("WoWHooks: Running local HWBP self-test\n");
            HANDLE hTest = CreateThread(nullptr, 0, (LPTHREAD_START_ROUTINE)WoWLocalHWBPTest, nullptr, CREATE_SUSPENDED, nullptr);
            if (hTest) {
                DWORD tid = GetThreadId(hTest);
                if (Core::HWBPManager::SetBreakpoint(tid, (void*)WoWLocalHWBPTest, 0, hTest)) {
                    ResumeThread(hTest);
                    Sleep(50);
                    Core::HWBPManager::RemoveBreakpoint(tid, 0, hTest);
                    Log("WoWHooks: self-test completed\n");
                } else {
                    Log("WoWHooks: self-test failed to set breakpoint\n");
                    ResumeThread(hTest);
                }
                WaitForSingleObject(hTest, 100);
                CloseHandle(hTest);
            } else {
                Log("WoWHooks: failed to create test thread\n");
            }
        }
    }

}} // namespace