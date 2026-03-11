#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef _WIN32
#define AGENT_API extern "C" __declspec(dllexport)
#define AGENT_CALL __stdcall
#else
#define AGENT_API extern "C"
#define AGENT_CALL
#endif

namespace TalosForge {
namespace NativeAgent {

enum class AgentState : uint32_t {
    Booting = 0,
    Ready = 1,
    Faulted = 2
};

struct AgentInitConfig {
    uint32_t version;
    const char* wowProcessName;
    const char* evasionProfile;
    uint32_t reservedFlags;
};

struct AgentStatus {
    uint32_t state;
    uint64_t heartbeatUnixMs;
    uint32_t queueDepth;
    char lastError[256];
};

} // namespace NativeAgent
} // namespace TalosForge

AGENT_API bool AGENT_CALL AgentInitialize(const TalosForge::NativeAgent::AgentInitConfig* config);
AGENT_API bool AGENT_CALL AgentShutdown();
AGENT_API bool AGENT_CALL AgentEnqueueCommand(const char* opcode, const char* payloadJson, uint32_t timeoutMs);
AGENT_API bool AGENT_CALL AgentTryGetStatus(TalosForge::NativeAgent::AgentStatus* status);
