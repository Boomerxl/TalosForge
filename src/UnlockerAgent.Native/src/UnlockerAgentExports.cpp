#include "UnlockerAgentExports.h"
#include "AntiDetection.h"
#include "HWBPManager.h"
#include "VEHHandler.h"
#include "HookRegistry.h"
#include "WoWHooks.h"
#include "Logger.h"
#include "LuaFrameOverlay.h"

#include <Windows.h>

#include <atomic>
#include <chrono>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <unordered_set>
#include <vector>

namespace {

using TalosForge::NativeAgent::AgentState;
using TalosForge::NativeAgent::AgentStatus;

constexpr uint32_t kLuaStatePtrAddr    = 0x00D3F78C;
constexpr uint32_t kLuaLoadBufferAddr  = 0x0084F860;
constexpr uint32_t kLuaPCallAddr       = 0x0084EC50;
constexpr uint32_t kLuaGetTopAddr      = 0x0084DBD0;
constexpr uint32_t kLuaToLStringAddr   = 0x0084E0E0;
constexpr uint32_t kLuaSetTopAddr      = 0x0084DBF0;
constexpr int kDefaultCommandTimeoutMs = 2500;
constexpr int kStartupWaitTimeoutMs = 2000;
constexpr int kStartupPollIntervalMs = 20;
// Optional delay before installing timer/pipe (was used for Warden; 0 = activate immediately).
constexpr int kDeferredActivationMs = 0;

constexpr UINT_PTR kDispatchTimerId = 0x7F01;
constexpr UINT kDispatchTimerIntervalMs = 16;
std::mutex g_sync;
std::atomic<uint64_t> g_heartbeatUnixMs{0};
std::atomic<AgentState> g_state{AgentState::Booting};
std::atomic<bool> g_stop{false};
std::atomic<bool> g_startupInProgress{false};
std::atomic<bool> g_processDetaching{false};
std::string g_lastError;
bool g_initialized = false;
HANDLE g_serverThread = nullptr;
HMODULE g_module = nullptr;
std::string g_pipeName;
uint32_t g_queueDepth = 0;
std::mutex g_dispatchSync;

HWND g_gameWindow = nullptr;
bool g_timerInstalled = false;

struct PendingLuaDispatch {
    std::string lua;
    std::string error;
    std::string result;
    bool success = false;
    bool wantsResult = false;
    HANDLE doneEvent = nullptr;

    ~PendingLuaDispatch() {
        if (doneEvent != nullptr) {
            CloseHandle(doneEvent);
            doneEvent = nullptr;
        }
    }
};

std::deque<std::shared_ptr<PendingLuaDispatch>> g_dispatchQueue;

using LuaLoadBufferFn  = int(__cdecl*)(void* L, const char* buff, int size, const char* name);
using LuaPCallFn       = int(__cdecl*)(void* L, int nargs, int nresults, int errfunc);
using LuaGetTopFn      = int(__cdecl*)(void* L);
using LuaSetTopFn      = void(__cdecl*)(void* L, int index);
using LuaToLStringFn   = const char*(__cdecl*)(void* L, int index, size_t* len);

DWORD WINAPI StartupThread(LPVOID lpParam);
void FailAndDrainDispatchQueue(const char* message);
std::string EscapeLua(const std::string& text);

uint64_t NowUnixMs() {
    const auto now = std::chrono::time_point_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now());
    return static_cast<uint64_t>(now.time_since_epoch().count());
}

void SetErrorLocked(const char* message) {
    g_lastError = message ? message : "";
    g_state.store(AgentState::Faulted);
}

void SetFaultState(const char* message) {
    std::lock_guard<std::mutex> lock(g_sync);
    g_initialized = false;
    SetErrorLocked(message);
    g_heartbeatUnixMs.store(NowUnixMs());
}

// -----------------------------------------------------------------
//  Generate an opaque pipe name derived from process identity
// -----------------------------------------------------------------
std::string GenerateObfuscatedPipeName() {
    DWORD pid = GetCurrentProcessId();
    FILETIME ct, et, kt, ut;
    GetProcessTimes(GetCurrentProcess(), &ct, &et, &kt, &ut);

    uint32_t seed = pid ^ ct.dwLowDateTime ^ 0xA3B7C9D1;
    seed = ((seed >> 16) ^ seed) * 0x45d9f3b;
    seed = ((seed >> 16) ^ seed) * 0x45d9f3b;
    seed = (seed >> 16) ^ seed;

    char buf[64];
    snprintf(buf, sizeof(buf), "\\\\.\\pipe\\WinSvc_%08X", seed);
    return buf;
}

bool ReadLine(HANDLE pipe, std::string& line) {
    line.clear();
    char ch = 0;
    DWORD read = 0;
    while (true) {
        const BOOL ok = ReadFile(pipe, &ch, 1, &read, nullptr);
        if (!ok || read == 0) {
            return false;
        }

        if (ch == '\n') {
            break;
        }

        if (ch != '\r') {
            line.push_back(ch);
        }
    }

    return true;
}

bool WriteLine(HANDLE pipe, const std::string& line) {
    std::string payload = line;
    payload.push_back('\n');
    DWORD written = 0;
    return WriteFile(pipe, payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr) == TRUE;
}

bool TryExtractJsonString(const std::string& json, const std::string& key, std::string& value) {
    value.clear();
    const std::string pattern = "\"" + key + "\"";
    size_t pos = json.find(pattern);
    if (pos == std::string::npos) {
        return false;
    }

    pos = json.find(':', pos + pattern.size());
    if (pos == std::string::npos) {
        return false;
    }

    pos = json.find('"', pos + 1);
    if (pos == std::string::npos) {
        return false;
    }

    ++pos;
    bool escaped = false;
    auto hexValue = [](char c) -> int {
        if (c >= '0' && c <= '9') {
            return c - '0';
        }

        c = static_cast<char>(tolower(static_cast<unsigned char>(c)));
        if (c >= 'a' && c <= 'f') {
            return 10 + (c - 'a');
        }

        return -1;
    };

    auto appendCodePointUtf8 = [&value](uint32_t codePoint) {
        if (codePoint <= 0x7F) {
            value.push_back(static_cast<char>(codePoint));
            return;
        }

        if (codePoint <= 0x7FF) {
            value.push_back(static_cast<char>(0xC0 | ((codePoint >> 6) & 0x1F)));
            value.push_back(static_cast<char>(0x80 | (codePoint & 0x3F)));
            return;
        }

        value.push_back(static_cast<char>(0xE0 | ((codePoint >> 12) & 0x0F)));
        value.push_back(static_cast<char>(0x80 | ((codePoint >> 6) & 0x3F)));
        value.push_back(static_cast<char>(0x80 | (codePoint & 0x3F)));
    };

    while (pos < json.size()) {
        const char ch = json[pos++];
        if (escaped) {
            switch (ch) {
            case 'b': value.push_back('\b'); break;
            case 'f': value.push_back('\f'); break;
            case 'n': value.push_back('\n'); break;
            case 'r': value.push_back('\r'); break;
            case 't': value.push_back('\t'); break;
            case '/': value.push_back('/'); break;
            case '\\': value.push_back('\\'); break;
            case '"': value.push_back('"'); break;
            case 'u':
            {
                if (pos + 4 > json.size()) {
                    return false;
                }

                uint32_t codePoint = 0;
                for (size_t i = 0; i < 4; i++) {
                    const int hv = hexValue(json[pos + i]);
                    if (hv < 0) {
                        return false;
                    }

                    codePoint = (codePoint << 4) | static_cast<uint32_t>(hv);
                }

                pos += 4;
                appendCodePointUtf8(codePoint);
                break;
            }
            default: value.push_back(ch); break;
            }
            escaped = false;
            continue;
        }

        if (ch == '\\') {
            escaped = true;
            continue;
        }

        if (ch == '"') {
            return true;
        }

        value.push_back(ch);
    }

    return false;
}

bool TryExtractJsonNumber(const std::string& json, const std::string& key, double& value) {
    const std::string pattern = "\"" + key + "\"";
    size_t pos = json.find(pattern);
    if (pos == std::string::npos) {
        return false;
    }

    pos = json.find(':', pos + pattern.size());
    if (pos == std::string::npos) {
        return false;
    }

    ++pos;
    while (pos < json.size() && isspace(static_cast<unsigned char>(json[pos])) != 0) {
        ++pos;
    }

    size_t end = pos;
    while (end < json.size()) {
        const char ch = json[end];
        if ((ch >= '0' && ch <= '9') || ch == '.' || ch == '-' || ch == '+') {
            ++end;
        }
        else {
            break;
        }
    }

    if (end <= pos) {
        return false;
    }

    std::istringstream iss(json.substr(pos, end - pos));
    iss >> value;
    return !iss.fail();
}

bool TryExtractJsonUInt64(const std::string& json, const std::string& key, uint64_t& value) {
    std::string raw;
    if (TryExtractJsonString(json, key, raw)) {
        if (raw.rfind("0x", 0) == 0 || raw.rfind("0X", 0) == 0) {
            std::istringstream iss(raw.substr(2));
            iss >> std::hex >> value;
            return !iss.fail();
        }

        std::istringstream iss(raw);
        iss >> value;
        return !iss.fail();
    }

    double numeric = 0;
    if (!TryExtractJsonNumber(json, key, numeric)) {
        return false;
    }

    if (numeric < 0) {
        return false;
    }

    value = static_cast<uint64_t>(numeric);
    return true;
}

static int SehRunLua(const char* code, int codeLen, int* outLoadResult, int* outCallResult) {
    __try {
        void* L = *reinterpret_cast<void**>(kLuaStatePtrAddr);
        if (!L) return -1;

        auto loadbuf = reinterpret_cast<LuaLoadBufferFn>(kLuaLoadBufferAddr);
        auto pcall   = reinterpret_cast<LuaPCallFn>(kLuaPCallAddr);
        auto gettop  = reinterpret_cast<LuaGetTopFn>(kLuaGetTopAddr);
        auto settop  = reinterpret_cast<LuaSetTopFn>(kLuaSetTopAddr);

        *outLoadResult = loadbuf(L, code, codeLen, "");
        if (*outLoadResult != 0) {
            int top = gettop(L);
            if (top > 0) settop(L, top - 1);
            return -2;
        }

        *outCallResult = pcall(L, 0, 0, 0);
        if (*outCallResult != 0) {
            int top = gettop(L);
            if (top > 0) settop(L, top - 1);
            return -3;
        }

        return 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -99;
    }
}

static int SehRunLuaQuery(const char* code, int codeLen, char* outBuf, int outBufSize, int* outLen) {
    __try {
        void* L = *reinterpret_cast<void**>(kLuaStatePtrAddr);
        if (!L) return -1;

        auto loadbuf  = reinterpret_cast<LuaLoadBufferFn>(kLuaLoadBufferAddr);
        auto pcall    = reinterpret_cast<LuaPCallFn>(kLuaPCallAddr);
        auto gettop   = reinterpret_cast<LuaGetTopFn>(kLuaGetTopAddr);
        auto settop   = reinterpret_cast<LuaSetTopFn>(kLuaSetTopAddr);
        auto tolstr   = reinterpret_cast<LuaToLStringFn>(kLuaToLStringAddr);

        int topBefore = gettop(L);

        int lr = loadbuf(L, code, codeLen, "");
        if (lr != 0) {
            int top = gettop(L);
            if (top > topBefore) settop(L, topBefore);
            return -2;
        }

        int cr = pcall(L, 0, 1, 0);
        if (cr != 0) {
            int top = gettop(L);
            if (top > topBefore) settop(L, topBefore);
            return -3;
        }

        int top = gettop(L);
        if (top > topBefore) {
            size_t slen = 0;
            const char* s = tolstr(L, -1, &slen);
            if (s && slen > 0 && outBuf && outBufSize > 0) {
                int copyLen = (int)slen < outBufSize - 1 ? (int)slen : outBufSize - 1;
                memcpy(outBuf, s, copyLen);
                outBuf[copyLen] = '\0';
                *outLen = copyLen;
            } else {
                *outLen = 0;
            }
            settop(L, topBefore);
        } else {
            *outLen = 0;
        }

        return 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -99;
    }
}

bool ExecuteLuaQuery(const std::string& code, std::string& result, std::string& error) {
    if (code.empty()) {
        error = "Lua code is empty.";
        return false;
    }

    char buf[4096];
    int len = 0;
    int rc = SehRunLuaQuery(code.c_str(), static_cast<int>(code.size()), buf, sizeof(buf), &len);

    switch (rc) {
    case 0:
        result.assign(buf, len);
        return true;
    case -1: error = "lua_State is null."; return false;
    case -2: error = "luaL_loadbuffer failed."; return false;
    case -3: error = "lua_pcall failed."; return false;
    default: error = "Lua query raised an SEH exception."; return false;
    }
}

bool ExecuteLua(const std::string& code, std::string& error) {
    if (code.empty()) {
        error = "Lua code is empty.";
        return false;
    }

    int loadResult = 0, callResult = 0;
    int rc = SehRunLua(code.c_str(), static_cast<int>(code.size()), &loadResult, &callResult);

    switch (rc) {
    case 0:  return true;
    case -1: error = "lua_State is null."; return false;
    case -2: error = "luaL_loadbuffer failed (" + std::to_string(loadResult) + ")."; return false;
    case -3: error = "lua_pcall failed (" + std::to_string(callResult) + ")."; return false;
    default: error = "Lua execution raised an SEH exception."; return false;
    }
}

// -----------------------------------------------------------------
// Timer-based dispatch – runs on the game thread via WM_TIMER
// No D3D vtable modification, no WndProc change.
// -----------------------------------------------------------------

VOID CALLBACK DispatchTimerProc(HWND, UINT, UINT_PTR, DWORD) {
    while (true) {
        std::shared_ptr<PendingLuaDispatch> pending;
        {
            std::lock_guard<std::mutex> lock(g_dispatchSync);
            if (g_dispatchQueue.empty()) {
                break;
            }

            pending = g_dispatchQueue.front();
            g_dispatchQueue.pop_front();
        }

        if (!pending) {
            continue;
        }

        std::string error;
        if (pending->wantsResult) {
            std::string result;
            pending->success = ExecuteLuaQuery(pending->lua, result, error);
            if (pending->success) {
                pending->result = std::move(result);
            } else {
                pending->error = error;
            }
        } else {
            pending->success = ExecuteLua(pending->lua, error);
            if (!pending->success) {
                pending->error = error;
            }
        }

        if (pending->doneEvent != nullptr) {
            SetEvent(pending->doneEvent);
        }
    }
}

struct GameWindowFindCtx {
    DWORD pid;
    HWND gxWindow;
    HWND largest;
    int largestArea;
};

static BOOL CALLBACK EnumChildWindowsRecursive(HWND hwnd, LPARAM lp);

static void ConsiderWindowForGame(HWND hwnd, GameWindowFindCtx* c) {
    if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) {
        return;
    }

    wchar_t cls[256];
    if (GetClassNameW(hwnd, cls, static_cast<int>(sizeof(cls) / sizeof(cls[0]))) <= 0) {
        return;
    }

    const bool isGx = _wcsicmp(cls, L"GxWindowClass") == 0;
    // Owned helper windows are common; GxWindowClass can still be owned by a shell — never skip it.
    if (GetWindow(hwnd, GW_OWNER) != nullptr && !isGx) {
        return;
    }

    RECT r{};
    if (!GetWindowRect(hwnd, &r)) {
        return;
    }
    const int area = (r.right - r.left) * (r.bottom - r.top);
    if (area < 50 * 50) {
        return;
    }

    if (isGx) {
        c->gxWindow = hwnd;
    }
    if (area > c->largestArea) {
        c->largestArea = area;
        c->largest = hwnd;
    }
}

static BOOL CALLBACK EnumChildWindowsRecursive(HWND hwnd, LPARAM lp) {
    auto* c = reinterpret_cast<GameWindowFindCtx*>(lp);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != c->pid) {
        return TRUE;
    }
    ConsiderWindowForGame(hwnd, c);
    EnumChildWindows(hwnd, EnumChildWindowsRecursive, lp);
    return TRUE;
}

HWND FindGameWindow() {
    // WoW 3.3.5a exposes the real client as GxWindowClass. EnumWindows order is not stable;
    // taking the "first visible top-level window" often hits a shell/helper HWND so SetTimer + Lua
    // dispatch run on the wrong thread and nothing appears in-game (no chat, no overlay).
    GameWindowFindCtx ctx = { GetCurrentProcessId(), nullptr, nullptr, 0 };

    EnumWindows(
        [](HWND hwnd, LPARAM lp) -> BOOL {
            auto* c = reinterpret_cast<GameWindowFindCtx*>(lp);
            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            if (pid != c->pid) {
                return TRUE;
            }
            ConsiderWindowForGame(hwnd, c);
            EnumChildWindows(hwnd, EnumChildWindowsRecursive, lp);

            return TRUE;
        },
        reinterpret_cast<LPARAM>(&ctx));

    if (ctx.gxWindow) {
        return ctx.gxWindow;
    }
    return ctx.largest;
}

bool InstallDispatchTimer(std::string& error) {
    error.clear();

    if (g_timerInstalled) {
        return true;
    }

    HWND wnd = nullptr;
    for (int attempt = 0; attempt < 80; attempt++) {
        wnd = FindGameWindow();
        if (wnd) break;
        Sleep(50);
    }

    if (!wnd) {
        error = "Could not find game window for timer dispatch.";
        return false;
    }

    UINT_PTR id = SetTimer(wnd, kDispatchTimerId, kDispatchTimerIntervalMs, DispatchTimerProc);
    if (!id) {
        error = "SetTimer failed.";
        return false;
    }

    g_gameWindow = wnd;
    g_timerInstalled = true;
    return true;
}

void UninstallDispatchTimer() {
    if (g_timerInstalled && g_gameWindow) {
        KillTimer(g_gameWindow, kDispatchTimerId);
    }

    g_timerInstalled = false;
    g_gameWindow = nullptr;

    FailAndDrainDispatchQueue("Dispatch timer uninstalled.");
}

void FailAndDrainDispatchQueue(const char* message) {
    std::deque<std::shared_ptr<PendingLuaDispatch>> pending;
    {
        std::lock_guard<std::mutex> lock(g_dispatchSync);
        pending.swap(g_dispatchQueue);
    }

    for (auto& item : pending) {
        if (!item) {
            continue;
        }

        item->success = false;
        item->error = message ? message : "Dispatch queue drained.";
        if (item->doneEvent != nullptr) {
            SetEvent(item->doneEvent);
        }
    }
}

bool DispatchLuaOnGameThread(const std::string& lua, int timeoutMs, std::string& error) {
    error.clear();
    if (lua.empty()) {
        error = "Lua code is empty.";
        return false;
    }

    if (!g_timerInstalled) {
        error = "Dispatch timer not installed.";
        return false;
    }

    auto pending = std::make_shared<PendingLuaDispatch>();
    pending->lua = lua;
    pending->doneEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    if (pending->doneEvent == nullptr) {
        error = "CreateEvent failed for dispatch command.";
        return false;
    }

    {
        std::lock_guard<std::mutex> lock(g_dispatchSync);
        g_dispatchQueue.push_back(pending);
    }

    const DWORD wait = WaitForSingleObject(
        pending->doneEvent,
        static_cast<DWORD>(timeoutMs > 0 ? timeoutMs : kDefaultCommandTimeoutMs));
    if (wait != WAIT_OBJECT_0) {
        error = "Lua dispatch timed out on game thread.";
        return false;
    }

    if (!pending->success) {
        error = pending->error.empty() ? "Lua dispatch failed." : pending->error;
        return false;
    }

    return true;
}

bool DispatchLuaQueryOnGameThread(const std::string& lua, int timeoutMs, std::string& result, std::string& error) {
    error.clear();
    result.clear();
    if (lua.empty()) {
        error = "Lua code is empty.";
        return false;
    }

    if (!g_timerInstalled) {
        error = "Dispatch timer not installed.";
        return false;
    }

    auto pending = std::make_shared<PendingLuaDispatch>();
    pending->lua = lua;
    pending->wantsResult = true;
    pending->doneEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    if (pending->doneEvent == nullptr) {
        error = "CreateEvent failed for dispatch query.";
        return false;
    }

    {
        std::lock_guard<std::mutex> lock(g_dispatchSync);
        g_dispatchQueue.push_back(pending);
    }

    const DWORD wait = WaitForSingleObject(
        pending->doneEvent,
        static_cast<DWORD>(timeoutMs > 0 ? timeoutMs : kDefaultCommandTimeoutMs));
    if (wait != WAIT_OBJECT_0) {
        error = "Lua query timed out on game thread.";
        return false;
    }

    if (!pending->success) {
        error = pending->error.empty() ? "Lua query failed." : pending->error;
        return false;
    }

    result = pending->result;
    return true;
}

// -----------------------------------------------------------------
// Internal memory read helpers (in-process, no RPM needed)
// -----------------------------------------------------------------

// SEH wrappers must be in separate functions with no C++ objects
static int SehReadBytes(uintptr_t addr, uint32_t size, char* hexBuf) {
    static const char hex[] = "0123456789abcdef";
    __try {
        const uint8_t* ptr = reinterpret_cast<const uint8_t*>(addr);
        for (uint32_t i = 0; i < size; i++) {
            hexBuf[i * 2]     = hex[ptr[i] >> 4];
            hexBuf[i * 2 + 1] = hex[ptr[i] & 0x0F];
        }
        return 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

bool HandleReadBytes(const std::string& payloadJson, std::string& result, std::string& error) {
    uint64_t addr = 0;
    double sizeVal = 0;
    if (!TryExtractJsonUInt64(payloadJson, "address", addr) || !TryExtractJsonNumber(payloadJson, "size", sizeVal)) {
        error = "Missing address or size.";
        return false;
    }
    uint32_t size = static_cast<uint32_t>(sizeVal);
    if (size == 0 || size > 65536) {
        error = "Invalid size (max 65536).";
        return false;
    }

    std::vector<char> hexBuf(size * 2);
    if (!SehReadBytes(static_cast<uintptr_t>(addr), size, hexBuf.data())) {
        error = "Access violation reading memory.";
        return false;
    }
    result.assign(hexBuf.data(), hexBuf.size());
    return true;
}

static int SehReadChain(uintptr_t base, const int32_t* offsets, size_t count, uint32_t* outAddr) {
    __try {
        uintptr_t current = base;
        for (size_t i = 0; i < count; i++) {
            if (i < count - 1) {
                current = *reinterpret_cast<uint32_t*>(current + offsets[i]);
                if (current == 0) return -1;
            } else {
                current = current + offsets[i];
            }
        }
        *outAddr = static_cast<uint32_t>(current);
        return 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

bool HandleReadChain(const std::string& payloadJson, std::string& result, std::string& error) {
    uint64_t base = 0;
    if (!TryExtractJsonUInt64(payloadJson, "base", base)) {
        error = "Missing base address.";
        return false;
    }

    std::string offsetsStr;
    if (!TryExtractJsonString(payloadJson, "offsets", offsetsStr)) {
        error = "Missing offsets.";
        return false;
    }

    std::vector<int32_t> offsets;
    std::istringstream iss(offsetsStr);
    std::string token;
    while (std::getline(iss, token, ',')) {
        try { offsets.push_back(std::stoi(token)); }
        catch (...) { error = "Invalid offset value."; return false; }
    }

    if (offsets.empty()) {
        error = "Empty offsets.";
        return false;
    }

    uint32_t addr = 0;
    int rc = SehReadChain(static_cast<uintptr_t>(base), offsets.data(), offsets.size(), &addr);
    if (rc == 0) {
        error = "Access violation in pointer chain.";
        return false;
    }
    if (rc < 0) {
        error = "Null pointer in chain.";
        return false;
    }
    char buf[32];
    snprintf(buf, sizeof(buf), "0x%08X", addr);
    result = buf;
    return true;
}

struct WalkObjectEntry {
    uint32_t ptr; uint64_t guid; int type;
    float x, y, z, facing;
    int combatFlag, castStart, castEnd, health, maxHealth;
    int mana, maxMana, level;
    uint32_t entryId, unitFlags, dynamicFlags;
    int factionTemplate;
    char name[64];
};

struct WalkObjectResult {
    uint64_t localGuid, targetGuid;
    WalkObjectEntry objects[8192];
    int count;
    int errorCode; // 0=ok, 1=clientConn null, 2=objMgr null, 3=AV
};

static void SehWalkObjects(WalkObjectResult* out) {
    out->count = 0;
    out->errorCode = 0;
    __try {
        uint32_t clientConn = *reinterpret_cast<uint32_t*>(0x00C79CE0);
        if (!clientConn) { out->errorCode = 1; return; }

        uint32_t objMgr = *reinterpret_cast<uint32_t*>(clientConn + 0x2ED0);
        if (!objMgr) { out->errorCode = 2; return; }

        out->localGuid = *reinterpret_cast<uint64_t*>(objMgr + 0x00C0);
        out->targetGuid = *reinterpret_cast<uint64_t*>(0x00BD07B0);
        uint32_t current = *reinterpret_cast<uint32_t*>(objMgr + 0x00AC);

        uint32_t visited[8192];
        int visitCount = 0;
        while (current && out->count < 8192) {
            bool seen = false;
            for (int v = 0; v < visitCount; v++) { if (visited[v] == current) { seen = true; break; } }
            if (seen) break;
            if (visitCount < 8192) visited[visitCount++] = current;

            auto& e = out->objects[out->count];
            e.ptr = current;
            e.guid = *reinterpret_cast<uint64_t*>(current + 0x0030);
            e.type = *reinterpret_cast<int*>(current + 0x0014);
            e.x = *reinterpret_cast<float*>(current + 0x079C);
            e.y = *reinterpret_cast<float*>(current + 0x0798);
            e.z = *reinterpret_cast<float*>(current + 0x07A0);
            e.facing = *reinterpret_cast<float*>(current + 0x07A8);
            e.combatFlag = 0; e.castStart = 0; e.castEnd = 0;
            e.health = -1; e.maxHealth = -1;
            e.mana = -1; e.maxMana = -1; e.level = -1;
            e.entryId = 0; e.unitFlags = 0; e.dynamicFlags = 0;
            e.factionTemplate = 0;
            e.name[0] = '\0';

            if (e.type == 3 || e.type == 4) {
                e.combatFlag = *reinterpret_cast<int*>(current + 0x0BEC);
                e.castStart = *reinterpret_cast<int*>(current + 0x0A78);
                e.castEnd = *reinterpret_cast<int*>(current + 0x0A7C);

                e.health = static_cast<int>(*reinterpret_cast<uint32_t*>(current + 0x1068));
                e.maxHealth = static_cast<int>(*reinterpret_cast<uint32_t*>(current + 0x1088));
                e.mana = static_cast<int>(*reinterpret_cast<uint32_t*>(current + 0x106C));
                e.maxMana = static_cast<int>(*reinterpret_cast<uint32_t*>(current + 0x108C));

                uint32_t descPtr = *reinterpret_cast<uint32_t*>(current + 0x0008);
                if (descPtr) {
                    e.entryId = *reinterpret_cast<uint32_t*>(descPtr + 0x000C);
                    e.level = *reinterpret_cast<int*>(descPtr + 0x00D8);
                    e.unitFlags = *reinterpret_cast<uint32_t*>(descPtr + 0x00EC);
                    e.dynamicFlags = *reinterpret_cast<uint32_t*>(descPtr + 0x013C);
                    e.factionTemplate = *reinterpret_cast<int*>(descPtr + 0x00DC);
                }

                uint32_t nameInfoPtr = *reinterpret_cast<uint32_t*>(current + 0x0964);
                if (nameInfoPtr) {
                    uint32_t nameStrPtr = *reinterpret_cast<uint32_t*>(nameInfoPtr + 0x005C);
                    if (nameStrPtr) {
                        const char* src = reinterpret_cast<const char*>(nameStrPtr);
                        for (int n = 0; n < 63 && src[n]; n++) {
                            e.name[n] = src[n];
                            e.name[n + 1] = '\0';
                        }
                    }
                }
            }
            if (e.type == 4) {
                e.health = *reinterpret_cast<int*>(current + 0x19B8);
                e.maxHealth = *reinterpret_cast<int*>(current + 0x19D8);
            }
            out->count++;
            current = *reinterpret_cast<uint32_t*>(current + 0x003C);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        out->errorCode = 3;
    }
}

bool HandleWalkObjects(const std::string&, std::string& result, std::string& error) {
    static WalkObjectResult walkResult;
    SehWalkObjects(&walkResult);

    if (walkResult.errorCode == 1) { error = "ClientConnection null."; return false; }
    if (walkResult.errorCode == 2) { error = "ObjectManager null."; return false; }
    if (walkResult.errorCode == 3) { error = "Access violation walking object manager."; return false; }

    std::ostringstream json;
    json << "{\"localGuid\":" << walkResult.localGuid
         << ",\"targetGuid\":" << walkResult.targetGuid << ",\"objects\":[";

    for (int i = 0; i < walkResult.count; i++) {
        auto& e = walkResult.objects[i];
        if (i > 0) json << ",";
        json << "{\"p\":" << e.ptr
             << ",\"g\":" << e.guid
             << ",\"t\":" << e.type
             << ",\"x\":" << e.x << ",\"y\":" << e.y << ",\"z\":" << e.z
             << ",\"f\":" << e.facing
             << ",\"cf\":" << e.combatFlag
             << ",\"cs\":" << e.castStart << ",\"ce\":" << e.castEnd
             << ",\"hp\":" << e.health << ",\"mhp\":" << e.maxHealth
             << ",\"mp\":" << e.mana << ",\"mmp\":" << e.maxMana
             << ",\"lv\":" << e.level
             << ",\"eid\":" << e.entryId
             << ",\"uf\":" << e.unitFlags
             << ",\"df\":" << e.dynamicFlags
             << ",\"ft\":" << e.factionTemplate;
        if (e.name[0]) {
            json << ",\"nm\":\"";
            for (int n = 0; e.name[n]; n++) {
                char c = e.name[n];
                if (c == '"') json << "\\\"";
                else if (c == '\\') json << "\\\\";
                else json << c;
            }
            json << "\"";
        }
        json << "}";
    }

    json << "],\"count\":" << walkResult.count << "}";
    result = json.str();
    return true;
}

bool HandleLuaQuery(const std::string& payload, std::string& result, std::string& error) {
    std::string code;
    if (!TryExtractJsonString(payload, "code", code)) {
        error = "Missing 'code' field for Lua query.";
        return false;
    }

    int timeoutMs = kDefaultCommandTimeoutMs;
    double tv = 0;
    if (TryExtractJsonNumber(payload, "timeoutMs", tv) && tv > 0) {
        timeoutMs = static_cast<int>(tv);
    }

    return DispatchLuaQueryOnGameThread(code, timeoutMs, result, error);
}

bool HandleQuerySpellInfo(const std::string& payload, std::string& result, std::string& error) {
    std::string spell;
    if (!TryExtractJsonString(payload, "spell", spell)) {
        error = "Missing 'spell' field.";
        return false;
    }

    std::string lua =
        "return (function() "
        "local s,d,e=GetSpellCooldown('" + EscapeLua(spell) + "') "
        "local u,n=IsUsableSpell('" + EscapeLua(spell) + "') "
        "local gs,gd=GetSpellCooldown(61304) "
        "return tostring(s or 0)..','..tostring(d or 0)..','..tostring(e or 0)..','"
        "..tostring(u or false)..','..tostring(n or false)..','"
        "..tostring(gs or 0)..','..tostring(gd or 0) end)()";

    return DispatchLuaQueryOnGameThread(lua, kDefaultCommandTimeoutMs, result, error);
}

bool HandleQueryBags(const std::string&, std::string& result, std::string& error) {
    std::string lua =
        "return (function() "
        "local r='' "
        "for b=0,4 do "
        "  local slots=GetContainerNumSlots(b) "
        "  for s=1,slots do "
        "    local _,c,_,_,_,_,link=GetContainerItemInfo(b,s) "
        "    if link then "
        "      local id=link:match('item:(%d+)') "
        "      local name=GetItemInfo(link) "
        "      if id then "
        "        if r~='' then r=r..';' end "
        "        r=r..b..','..s..','..id..','..(c or 1)..','..(name or '') "
        "      end "
        "    end "
        "  end "
        "end "
        "return r end)()";

    return DispatchLuaQueryOnGameThread(lua, kDefaultCommandTimeoutMs, result, error);
}

bool HandleQueryAuras(const std::string& payload, std::string& result, std::string& error) {
    std::string unit = "player";
    TryExtractJsonString(payload, "unit", unit);

    std::string lua =
        "return (function() "
        "local r='' "
        "for i=1,40 do "
        "  local name,_,_,count,_,dur,exp,_,_,_,id=UnitBuff('" + EscapeLua(unit) + "',i) "
        "  if not name then break end "
        "  if r~='' then r=r..';' end "
        "  r=r..'B,'..tostring(id or 0)..','..tostring(count or 0)..','..tostring(dur or 0)..','..tostring(exp or 0)..','..name "
        "end "
        "for i=1,40 do "
        "  local name,_,_,count,_,dur,exp,_,_,_,id=UnitDebuff('" + EscapeLua(unit) + "',i) "
        "  if not name then break end "
        "  if r~='' then r=r..';' end "
        "  r=r..'D,'..tostring(id or 0)..','..tostring(count or 0)..','..tostring(dur or 0)..','..tostring(exp or 0)..','..name "
        "end "
        "return r end)()";

    return DispatchLuaQueryOnGameThread(lua, kDefaultCommandTimeoutMs, result, error);
}

bool HandleInternalOpcode(const std::string& opcode, const std::string& payload,
                          bool& handled, std::string& result, std::string& error) {
    handled = false;
    if (opcode == "ReadBytes") {
        handled = true;
        return HandleReadBytes(payload, result, error);
    }
    if (opcode == "ReadChain") {
        handled = true;
        return HandleReadChain(payload, result, error);
    }
    if (opcode == "WalkObjects") {
        handled = true;
        return HandleWalkObjects(payload, result, error);
    }
    if (opcode == "LuaQuery") {
        handled = true;
        return HandleLuaQuery(payload, result, error);
    }
    if (opcode == "QuerySpellInfo") {
        handled = true;
        return HandleQuerySpellInfo(payload, result, error);
    }
    if (opcode == "QueryBags") {
        handled = true;
        return HandleQueryBags(payload, result, error);
    }
    if (opcode == "QueryAuras") {
        handled = true;
        return HandleQueryAuras(payload, result, error);
    }
    return false;
}

std::string EscapeLua(const std::string& text) {
    std::string out;
    out.reserve(text.size() + 8);
    for (char ch : text) {
        if (ch == '\\' || ch == '\'') {
            out.push_back('\\');
        }

        out.push_back(ch);
    }

    return out;
}

bool BuildLuaFromOpcode(
    const std::string& opcode,
    const std::string& payloadJson,
    std::string& lua,
    std::string& error) {
    lua.clear();
    error.clear();

    if (opcode == "LuaDoString") {
        std::string code;
        if (!TryExtractJsonString(payloadJson, "code", code)) {
            error = "Missing code.";
            return false;
        }

        lua = code;
        return true;
    }

    if (opcode == "CastSpellByName") {
        std::string spell;
        if (!TryExtractJsonString(payloadJson, "spell", spell)) {
            error = "Missing spell.";
            return false;
        }

        lua = "CastSpellByName('" + EscapeLua(spell) + "')";
        return true;
    }

    if (opcode == "SetTargetGuid") {
        uint64_t guid = 0;
        if (!TryExtractJsonUInt64(payloadJson, "guid", guid)) {
            error = "Missing guid.";
            return false;
        }

        lua = "if _G.SetTargetGuid then SetTargetGuid('" + std::to_string(guid) + "') else error('SetTargetGuid unavailable') end";
        return true;
    }

    if (opcode == "Face") {
        double facing = 0;
        double smoothing = 0;
        if (!TryExtractJsonNumber(payloadJson, "facing", facing) ||
            !TryExtractJsonNumber(payloadJson, "smoothing", smoothing)) {
            error = "Missing facing/smoothing.";
            return false;
        }

        lua = "if _G.Face then Face(" + std::to_string(facing) + "," + std::to_string(smoothing) + ") else error('Face unavailable') end";
        return true;
    }

    if (opcode == "MoveTo") {
        double x = 0;
        double y = 0;
        double z = 0;
        double overshoot = 0;
        if (!TryExtractJsonNumber(payloadJson, "x", x) ||
            !TryExtractJsonNumber(payloadJson, "y", y) ||
            !TryExtractJsonNumber(payloadJson, "z", z) ||
            !TryExtractJsonNumber(payloadJson, "overshootThreshold", overshoot)) {
            error = "Missing move parameters.";
            return false;
        }

        lua = "if _G.MoveTo then MoveTo(" + std::to_string(x) + "," + std::to_string(y) + "," + std::to_string(z) + "," + std::to_string(overshoot) + ") else error('MoveTo unavailable') end";
        return true;
    }

    if (opcode == "Interact") {
        uint64_t guid = 0;
        if (TryExtractJsonUInt64(payloadJson, "guid", guid)) {
            lua = "if _G.Interact then Interact('" + std::to_string(guid) + "') elseif _G.InteractGuid then InteractGuid('" + std::to_string(guid) + "') else error('Interact unavailable') end";
        }
        else {
            lua = "if _G.Interact then Interact() elseif _G.InteractUnit then InteractUnit('target') else error('Interact unavailable') end";
        }

        return true;
    }

    if (opcode == "Stop") {
        lua = "if _G.Stop then Stop() else if _G.MoveForwardStop then MoveForwardStop() end if _G.MoveBackwardStop then MoveBackwardStop() end if _G.StrafeLeftStop then StrafeLeftStop() end if _G.StrafeRightStop then StrafeRightStop() end end";
        return true;
    }

    if (opcode == "ClickToMove") {
        double x = 0, y = 0, z = 0;
        if (!TryExtractJsonNumber(payloadJson, "x", x) ||
            !TryExtractJsonNumber(payloadJson, "y", y) ||
            !TryExtractJsonNumber(payloadJson, "z", z)) {
            error = "Missing coordinates for ClickToMove.";
            return false;
        }
        lua = std::string("if _G.MoveTo then MoveTo(") +
              std::to_string(x) + "," + std::to_string(y) + "," + std::to_string(z) + ",0) " +
              "elseif _G.MoveForwardStart then MoveForwardStart() end";
        return true;
    }

    if (opcode == "CastSpellByID") {
        double spellId = 0;
        if (!TryExtractJsonNumber(payloadJson, "spellId", spellId)) {
            error = "Missing spellId.";
            return false;
        }
        lua = "CastSpellByID(" + std::to_string(static_cast<int>(spellId)) + ")";
        return true;
    }

    error = "Unsupported opcode.";
    return false;
}

DWORD WINAPI PipeServerThreadProc(LPVOID) {
    while (!g_stop.load()) {
        SECURITY_DESCRIPTOR sd;
        InitializeSecurityDescriptor(&sd, SECURITY_DESCRIPTOR_REVISION);
        SetSecurityDescriptorDacl(&sd, TRUE, nullptr, FALSE);
        SECURITY_ATTRIBUTES sa = { sizeof(sa), &sd, FALSE };

        HANDLE pipe = CreateNamedPipeA(
            g_pipeName.c_str(),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            8192,
            8192,
            200,
            &sa);
        if (pipe == INVALID_HANDLE_VALUE) {
            Sleep(100);
            continue;
        }

        const BOOL connected = ConnectNamedPipe(pipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);
        if (!connected) {
            CloseHandle(pipe);
            continue;
        }

        while (!g_stop.load()) {
            std::string opcode;
            std::string payload;
            std::string timeoutRaw;
            if (!ReadLine(pipe, opcode) || !ReadLine(pipe, payload) || !ReadLine(pipe, timeoutRaw)) {
                break;
            }

            {
                std::lock_guard<std::mutex> lock(g_sync);
                ++g_queueDepth;
            }

            g_heartbeatUnixMs.store(NowUnixMs());
            std::string lua;
            std::string error;
            std::string internalResult;
            bool internalHandled = false;
            bool success = HandleInternalOpcode(opcode, payload, internalHandled, internalResult, error);
            std::string code;
            std::string message;

            if (internalHandled) {
                code = success ? "OK" : "AGENT_EXECUTION_FAILED";
                message = success ? internalResult : error;
            } else {
                success = BuildLuaFromOpcode(opcode, payload, lua, error);
                code = success ? "OK" : "AGENT_INVALID_REQUEST";
                message = success ? ("ACK:" + opcode) : error;

                int timeoutMs = kDefaultCommandTimeoutMs;
                if (!timeoutRaw.empty()) {
                    try {
                        const int parsed = std::stoi(timeoutRaw);
                        if (parsed > 0) {
                            timeoutMs = parsed;
                        }
                    }
                    catch (...) {}
                }

                if (success) {
                    success = DispatchLuaOnGameThread(lua, timeoutMs, error);
                    if (!success) {
                        code = "AGENT_EXECUTION_FAILED";
                        message = error;
                    }
                }
            }

            WriteLine(pipe, success ? "1" : "0");
            WriteLine(pipe, code);
            WriteLine(pipe, message);
            WriteLine(pipe, payload);

            {
                std::lock_guard<std::mutex> lock(g_sync);
                if (g_queueDepth > 0) {
                    --g_queueDepth;
                }

                if (!success) {
                    SetErrorLocked(message.c_str());
                }
                else {
                    g_state.store(AgentState::Ready);
                    g_lastError.clear();
                }
            }
        }

        FlushFileBuffers(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }

    return 0;
}

bool StartPipeServer(std::string& error) {
    error.clear();

    {
        std::lock_guard<std::mutex> lock(g_sync);
        if (g_serverThread != nullptr) {
            return true;
        }

        g_pipeName = GenerateObfuscatedPipeName();
        g_stop.store(false);
    }

    HANDLE thread = CreateThread(nullptr, 0, PipeServerThreadProc, nullptr, 0, nullptr);
    if (thread == nullptr) {
        error = "CreateThread failed for pipe server.";
        return false;
    }

    {
        std::lock_guard<std::mutex> lock(g_sync);
        if (g_serverThread == nullptr) {
            g_serverThread = thread;
            return true;
        }
    }

    CloseHandle(thread);
    return true;
}

void StopPipeServer() {
    HANDLE serverThread = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_sync);
        g_stop.store(true);
        serverThread = g_serverThread;
        g_serverThread = nullptr;
    }

    FailAndDrainDispatchQueue("Agent shutdown.");
    UninstallDispatchTimer();
    if (serverThread != nullptr) {
        WaitForSingleObject(serverThread, 1000);
        CloseHandle(serverThread);
    }
}

bool StartAsyncInitialization(std::string& error) {
    error.clear();

    {
        std::lock_guard<std::mutex> lock(g_sync);
        if (g_initialized) {
            return true;
        }

        if (g_startupInProgress.load()) {
            return true;
        }

        g_startupInProgress.store(true);
        g_state.store(AgentState::Booting);
        g_lastError.clear();
    }

    HANDLE startupThread = CreateThread(nullptr, 0, StartupThread, g_module, 0, nullptr);
    if (startupThread == nullptr) {
        g_startupInProgress.store(false);
        error = "CreateThread failed for startup.";
        SetFaultState(error.c_str());
        return false;
    }

    CloseHandle(startupThread);
    return true;
}

bool WaitForReadyState(int timeoutMs) {
    const int waitBudget = timeoutMs > 0 ? timeoutMs : kStartupWaitTimeoutMs;
    int waitedMs = 0;

    while (waitedMs < waitBudget) {
        {
            std::lock_guard<std::mutex> lock(g_sync);
            if (g_initialized && g_state.load() == AgentState::Ready) {
                return true;
            }

            if (g_state.load() == AgentState::Faulted) {
                return false;
            }
        }

        if (!g_startupInProgress.load()) {
            break;
        }

        Sleep(kStartupPollIntervalMs);
        waitedMs += kStartupPollIntervalMs;
    }

    std::lock_guard<std::mutex> lock(g_sync);
    return g_initialized && g_state.load() == AgentState::Ready;
}

// ------------------------------------------------------------------
// Write the obfuscated pipe name to a discovery file so the host can find it
// ------------------------------------------------------------------
void WritePipeDiscoveryFile() {
    char tempPath[MAX_PATH];
    if (GetTempPathA(MAX_PATH, tempPath) == 0) return;

    DWORD pid = GetCurrentProcessId();
    char filePath[MAX_PATH];
    snprintf(filePath, sizeof(filePath), "%sTalosForge.pipe.%u", tempPath, pid);

    HANDLE hFile = CreateFileA(filePath, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (hFile == INVALID_HANDLE_VALUE) return;

    DWORD written;
    WriteFile(hFile, g_pipeName.c_str(), static_cast<DWORD>(g_pipeName.size()), &written, nullptr);
    CloseHandle(hFile);
}

// ------------------------------------------------------------------
// StartupThread – all initialization outside of DllMain
// ------------------------------------------------------------------
DWORD WINAPI StartupThread(LPVOID lpParam)
{
    using namespace TalosForge::Native;

    g_startupInProgress.store(true);
    (void)lpParam;

    if (g_processDetaching.load()) {
        g_startupInProgress.store(false);
        return 0;
    }

    // Phase 1: Optional deferred activation (kDeferredActivationMs; 0 = skip)
    if constexpr (kDeferredActivationMs > 0) {
        Log("Agent: deferred activation (%dms)...\n", kDeferredActivationMs);
        for (int waited = 0; waited < kDeferredActivationMs; waited += kStartupPollIntervalMs) {
            if (g_processDetaching.load() || g_stop.load()) {
                g_startupInProgress.store(false);
                return 0;
            }
            Sleep(kStartupPollIntervalMs);
        }
    }

    // Phase 2: Install timer-based dispatch for game-thread Lua execution
    {
        std::string timerError;
        if (!InstallDispatchTimer(timerError)) {
            Log("Agent: dispatch timer failed: %s\n", timerError.c_str());
            SetFaultState(timerError.c_str());
            g_startupInProgress.store(false);
            return 0;
        }
        Log("Agent: dispatch timer installed (hwnd=0x%08X)\n",
            reinterpret_cast<uint32_t>(g_gameWindow));
    }

    // One-shot chat line (runs as soon as the timer queue works).
    {
        static std::atomic<bool> welcomeSent{false};
        if (!welcomeSent.exchange(true)) {
            std::string welcomeErr;
            const std::string welcomeLua =
                R"(DEFAULT_CHAT_FRAME:AddMessage("Ad lapides mittere", 1.0, 1.0, 1.0))";
            if (!DispatchLuaOnGameThread(welcomeLua, 5000, welcomeErr)) {
                Log("Agent: welcome chat line failed: %s\n", welcomeErr.c_str());
            }
        }
    }

    // Phase 2b: WoW Lua UI frame (same script as Core TalosForgeFrameLua.CreateHub — no D3D hook).
    {
        std::string frameErr;
        const std::string frameLua(TalosForge::LuaFrameOverlay::GetCreateFrameScript());
        if (!DispatchLuaOnGameThread(frameLua, 8000, frameErr)) {
            Log("Agent: Lua frame overlay failed: %s\n", frameErr.c_str());
        } else {
            Log("Agent: Lua frame overlay ready (TF_Debug)\n");
        }
    }

    // Phase 3: Start command pipe server
    std::string startError;
    if (!StartPipeServer(startError)) {
        SetFaultState(startError.c_str());
        g_startupInProgress.store(false);
        return 0;
    }

    WritePipeDiscoveryFile();

    {
        std::lock_guard<std::mutex> lock(g_sync);
        g_initialized = true;
        g_state.store(AgentState::Ready);
        g_heartbeatUnixMs.store(NowUnixMs());
        g_lastError.clear();
    }

    Log("Agent: startup complete, pipe=%s\n", g_pipeName.c_str());
    g_startupInProgress.store(false);
    return 0;
}

} // namespace

// ------------------------------------------------------------------
// DllMain
// ------------------------------------------------------------------
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_module = module;
        DisableThreadLibraryCalls(module);
        TalosForge::Native::g_enableDebugOutput = false;
        TalosForge::Native::g_enableFileLogging = false;
        TalosForge::Native::HideModuleFromPeb(module);
        TalosForge::Native::CleanPebDebugFlags();
        TalosForge::Native::ErasePeHeader(module);
        {
            std::string startError;
            StartAsyncInitialization(startError);
        }
        break;

    case DLL_PROCESS_DETACH:
        g_processDetaching.store(true);
        g_stop.store(true);
        break;
    }
    return TRUE;
}

// ------------------------------------------------------------------
// Exported Agent API
// ------------------------------------------------------------------
AGENT_API bool AGENT_CALL AgentInitialize(const TalosForge::NativeAgent::AgentInitConfig* config) {
    if (config == nullptr || config->version == 0) {
        SetFaultState("Invalid config.");
        return false;
    }

    if (g_processDetaching.load()) {
        SetFaultState("Process is detaching.");
        return false;
    }

    {
        std::lock_guard<std::mutex> lock(g_sync);
        if (g_initialized && g_state.load() == AgentState::Ready) {
            return true;
        }

        g_lastError.clear();
    }

    std::string startError;
    if (!StartAsyncInitialization(startError)) {
        return false;
    }

    if (!WaitForReadyState(kStartupWaitTimeoutMs)) {
        std::lock_guard<std::mutex> lock(g_sync);
        if (g_lastError.empty()) {
            SetErrorLocked("Initialization timed out.");
        }

        return false;
    }

    return true;
}

AGENT_API bool AGENT_CALL AgentShutdown() {
    {
        std::lock_guard<std::mutex> lock(g_sync);
        if (!g_initialized && g_serverThread == nullptr) {
            g_state.store(AgentState::Booting);
            g_heartbeatUnixMs.store(NowUnixMs());
            g_lastError.clear();
            g_queueDepth = 0;
            return true;
        }

        g_state.store(AgentState::Booting);
    }

    StopPipeServer();

    {
        std::lock_guard<std::mutex> lock(g_sync);
        g_initialized = false;
        g_heartbeatUnixMs.store(NowUnixMs());
        g_lastError.clear();
        g_queueDepth = 0;
    }

    return true;
}

AGENT_API bool AGENT_CALL AgentEnqueueCommand(const char* opcode, const char* payloadJson, uint32_t) {
    {
        std::lock_guard<std::mutex> lock(g_sync);
        if (!g_initialized || g_state.load() != AgentState::Ready) {
            SetErrorLocked("Agent not ready.");
            return false;
        }
    }

    if (opcode == nullptr || opcode[0] == '\0') {
        SetFaultState("Opcode is required.");
        return false;
    }

    std::string lua;
    std::string error;
    if (!BuildLuaFromOpcode(opcode, payloadJson ? payloadJson : "{}", lua, error)) {
        SetFaultState(error.c_str());
        return false;
    }

    if (!DispatchLuaOnGameThread(lua, kDefaultCommandTimeoutMs, error)) {
        SetFaultState(error.c_str());
        return false;
    }

    {
        std::lock_guard<std::mutex> lock(g_sync);
        g_heartbeatUnixMs.store(NowUnixMs());
        g_state.store(AgentState::Ready);
        g_lastError.clear();
    }

    return true;
}

AGENT_API bool AGENT_CALL AgentTryGetStatus(AgentStatus* status) {
    if (status == nullptr) {
        return false;
    }

    std::lock_guard<std::mutex> lock(g_sync);
    status->state = static_cast<uint32_t>(g_state.load());
    status->heartbeatUnixMs = g_heartbeatUnixMs.load();
    status->queueDepth = g_queueDepth;
    std::memset(status->lastError, 0, sizeof(status->lastError));
    if (!g_lastError.empty()) {
        std::strncpy(status->lastError, g_lastError.c_str(), sizeof(status->lastError) - 1);
    }

    return true;
}

AGENT_API const char* AGENT_CALL AgentGetPipeName() {
    return g_pipeName.c_str();
}
