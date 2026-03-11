#include "UnlockerAgentExports.h"

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

namespace {

using TalosForge::NativeAgent::AgentState;
using TalosForge::NativeAgent::AgentStatus;

constexpr uint32_t kLuaExecuteAddress = 0x00819210;
constexpr uint32_t kHardwareFlagAddress = 0x00B499A4;
constexpr UINT kDispatchLuaMessage = WM_APP + 0x4A1;
constexpr int kDefaultCommandTimeoutMs = 2500;

std::mutex g_sync;
std::atomic<uint64_t> g_heartbeatUnixMs{0};
std::atomic<AgentState> g_state{AgentState::Booting};
std::atomic<bool> g_stop{false};
std::string g_lastError;
bool g_initialized = false;
HANDLE g_serverThread = nullptr;
HMODULE g_module = nullptr;
std::string g_pipeName;
uint32_t g_queueDepth = 0;
std::mutex g_dispatchSync;
HWND g_dispatchWindow = nullptr;
WNDPROC g_dispatchPrevWndProc = nullptr;

struct PendingLuaDispatch {
    std::string lua;
    std::string error;
    bool success = false;
    HANDLE doneEvent = nullptr;

    ~PendingLuaDispatch() {
        if (doneEvent != nullptr) {
            CloseHandle(doneEvent);
            doneEvent = nullptr;
        }
    }
};

std::deque<std::shared_ptr<PendingLuaDispatch>> g_dispatchQueue;

using FrameScriptExecuteFn = void(__cdecl*)(const char*, const char*, int);

uint64_t NowUnixMs() {
    const auto now = std::chrono::time_point_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now());
    return static_cast<uint64_t>(now.time_since_epoch().count());
}

void SetErrorLocked(const char* message) {
    g_lastError = message ? message : "";
    g_state.store(AgentState::Faulted);
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

bool ExecuteLua(const std::string& code, std::string& error) {
    if (code.empty()) {
        error = "Lua code is empty.";
        return false;
    }

    auto fn = reinterpret_cast<FrameScriptExecuteFn>(kLuaExecuteAddress);
    volatile uint32_t* flag = reinterpret_cast<volatile uint32_t*>(kHardwareFlagAddress);

    __try {
        *flag = 1;
        fn(code.c_str(), "TalosForge", 0);
        *flag = 0;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        *flag = 0;
        error = "FrameScript_Execute raised an exception.";
        return false;
    }
}

BOOL CALLBACK EnumVisibleTopLevelWindowsProc(HWND hwnd, LPARAM lParam) {
    if (GetWindow(hwnd, GW_OWNER) != nullptr || !IsWindowVisible(hwnd)) {
        return TRUE;
    }

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != GetCurrentProcessId()) {
        return TRUE;
    }

    auto out = reinterpret_cast<HWND*>(lParam);
    *out = hwnd;
    return FALSE;
}

BOOL CALLBACK EnumAnyTopLevelWindowsProc(HWND hwnd, LPARAM lParam) {
    if (GetWindow(hwnd, GW_OWNER) != nullptr) {
        return TRUE;
    }

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != GetCurrentProcessId()) {
        return TRUE;
    }

    auto out = reinterpret_cast<HWND*>(lParam);
    *out = hwnd;
    return FALSE;
}

HWND FindCurrentProcessWindow() {
    HWND window = nullptr;
    EnumWindows(EnumVisibleTopLevelWindowsProc, reinterpret_cast<LPARAM>(&window));
    if (window != nullptr) {
        return window;
    }

    EnumWindows(EnumAnyTopLevelWindowsProc, reinterpret_cast<LPARAM>(&window));
    return window;
}

LRESULT CALLBACK DispatchWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (msg == kDispatchLuaMessage) {
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
            pending->success = ExecuteLua(pending->lua, error);
            if (!pending->success) {
                pending->error = error;
            }

            if (pending->doneEvent != nullptr) {
                SetEvent(pending->doneEvent);
            }
        }

        return 0;
    }

    WNDPROC previous = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_dispatchSync);
        previous = g_dispatchPrevWndProc;
    }

    if (previous != nullptr) {
        return CallWindowProcA(previous, hwnd, msg, wParam, lParam);
    }

    return DefWindowProcA(hwnd, msg, wParam, lParam);
}

bool EnsureDispatchWindow(std::string& error) {
    error.clear();

    {
        std::lock_guard<std::mutex> lock(g_dispatchSync);
        if (g_dispatchWindow != nullptr &&
            g_dispatchPrevWndProc != nullptr &&
            IsWindow(g_dispatchWindow)) {
            return true;
        }
    }

    HWND window = nullptr;
    for (int attempt = 0; attempt < 40; attempt++) {
        window = FindCurrentProcessWindow();
        if (window != nullptr) {
            break;
        }

        Sleep(50);
    }

    if (window == nullptr) {
        error = "Unable to locate WoW window for game-thread dispatch.";
        return false;
    }

    SetLastError(0);
    const LONG_PTR previous = SetWindowLongPtrA(
        window,
        GWLP_WNDPROC,
        reinterpret_cast<LONG_PTR>(&DispatchWndProc));
    const DWORD setError = GetLastError();
    if (previous == 0 && setError != 0) {
        error = "SetWindowLongPtrA failed for dispatch window.";
        return false;
    }

    {
        std::lock_guard<std::mutex> lock(g_dispatchSync);
        g_dispatchWindow = window;
        g_dispatchPrevWndProc = reinterpret_cast<WNDPROC>(previous);
    }

    return true;
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

void UninstallDispatchWindow() {
    HWND window = nullptr;
    WNDPROC previous = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_dispatchSync);
        window = g_dispatchWindow;
        previous = g_dispatchPrevWndProc;
        g_dispatchWindow = nullptr;
        g_dispatchPrevWndProc = nullptr;
    }

    if (window != nullptr && previous != nullptr && IsWindow(window)) {
        SetWindowLongPtrA(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(previous));
    }

    FailAndDrainDispatchQueue("Dispatch window was uninstalled.");
}

bool DispatchLuaOnGameThread(const std::string& lua, int timeoutMs, std::string& error) {
    error.clear();
    if (lua.empty()) {
        error = "Lua code is empty.";
        return false;
    }

    if (!EnsureDispatchWindow(error)) {
        return false;
    }

    auto pending = std::make_shared<PendingLuaDispatch>();
    pending->lua = lua;
    pending->doneEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    if (pending->doneEvent == nullptr) {
        error = "CreateEvent failed for dispatch command.";
        return false;
    }

    HWND dispatchWindow = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_dispatchSync);
        dispatchWindow = g_dispatchWindow;
        g_dispatchQueue.push_back(pending);
    }

    if (dispatchWindow == nullptr || !PostMessageA(dispatchWindow, kDispatchLuaMessage, 0, 0)) {
        error = "PostMessage failed for Lua dispatch.";
        return false;
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

    error = "Unsupported opcode.";
    return false;
}

DWORD WINAPI PipeServerThreadProc(LPVOID) {
    while (!g_stop.load()) {
        HANDLE pipe = CreateNamedPipeA(
            g_pipeName.c_str(),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            PIPE_UNLIMITED_INSTANCES,
            8192,
            8192,
            200,
            nullptr);
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
            bool success = BuildLuaFromOpcode(opcode, payload, lua, error);
            std::string code = success ? "OK" : "AGENT_INVALID_REQUEST";
            std::string message = success ? ("ACK:" + opcode) : error;

            int timeoutMs = kDefaultCommandTimeoutMs;
            if (!timeoutRaw.empty()) {
                try {
                    const int parsed = std::stoi(timeoutRaw);
                    if (parsed > 0) {
                        timeoutMs = parsed;
                    }
                }
                catch (...) {
                    // Keep default timeout.
                }
            }

            if (success) {
                success = DispatchLuaOnGameThread(lua, timeoutMs, error);
                if (!success) {
                    code = "AGENT_EXECUTION_FAILED";
                    message = error;
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

void StartPipeServerLocked() {
    if (g_serverThread != nullptr) {
        return;
    }

    const DWORD pid = GetCurrentProcessId();
    g_pipeName = "\\\\.\\pipe\\TalosForge.Agent.Native." + std::to_string(pid);
    g_stop.store(false);
    g_serverThread = CreateThread(nullptr, 0, PipeServerThreadProc, nullptr, 0, nullptr);
}

void StopPipeServerLocked() {
    g_stop.store(true);
    FailAndDrainDispatchQueue("Agent shutdown.");
    UninstallDispatchWindow();
    if (g_serverThread != nullptr) {
        WaitForSingleObject(g_serverThread, 1000);
        CloseHandle(g_serverThread);
        g_serverThread = nullptr;
    }
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        g_module = module;
        DisableThreadLibraryCalls(module);
        {
            std::lock_guard<std::mutex> lock(g_sync);
            g_initialized = true;
            g_state.store(AgentState::Ready);
            g_heartbeatUnixMs.store(NowUnixMs());
            g_lastError.clear();
            StartPipeServerLocked();
        }
        break;
    case DLL_PROCESS_DETACH:
        {
            std::lock_guard<std::mutex> lock(g_sync);
            StopPipeServerLocked();
            g_initialized = false;
            g_state.store(AgentState::Booting);
        }
        break;
    default:
        break;
    }

    return TRUE;
}

AGENT_API bool AGENT_CALL AgentInitialize(const TalosForge::NativeAgent::AgentInitConfig* config) {
    std::lock_guard<std::mutex> lock(g_sync);
    g_lastError.clear();

    if (config == nullptr || config->version == 0) {
        SetErrorLocked("Invalid config.");
        return false;
    }

    g_initialized = true;
    g_state.store(AgentState::Ready);
    g_heartbeatUnixMs.store(NowUnixMs());
    StartPipeServerLocked();
    return true;
}

AGENT_API bool AGENT_CALL AgentShutdown() {
    std::lock_guard<std::mutex> lock(g_sync);
    StopPipeServerLocked();
    g_initialized = false;
    g_state.store(AgentState::Booting);
    g_heartbeatUnixMs.store(NowUnixMs());
    g_lastError.clear();
    g_queueDepth = 0;
    return true;
}

AGENT_API bool AGENT_CALL AgentEnqueueCommand(const char* opcode, const char* payloadJson, uint32_t) {
    std::lock_guard<std::mutex> lock(g_sync);
    if (!g_initialized) {
        SetErrorLocked("Agent not initialized.");
        return false;
    }

    if (opcode == nullptr || opcode[0] == '\0') {
        SetErrorLocked("Opcode is required.");
        return false;
    }

    std::string lua;
    std::string error;
    if (!BuildLuaFromOpcode(opcode, payloadJson ? payloadJson : "{}", lua, error)) {
        SetErrorLocked(error.c_str());
        return false;
    }

    if (!DispatchLuaOnGameThread(lua, kDefaultCommandTimeoutMs, error)) {
        SetErrorLocked(error.c_str());
        return false;
    }

    g_heartbeatUnixMs.store(NowUnixMs());
    g_state.store(AgentState::Ready);
    g_lastError.clear();
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
