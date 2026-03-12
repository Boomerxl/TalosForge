#pragma once
#include <string>
#include <vector>
#include <mutex>
#include <cstdarg>

namespace TalosForge { namespace Native {

    // Simple ring buffer logger (in‑memory only)
    class Logger {
    public:
        enum class Severity { Info, Warning, Error };

        struct Entry {
            Severity severity;
            std::string message;
        };

        static void Log(Severity sev, const char* category, const char* fmt, ...);
        static std::vector<Entry> GetEntries();
        static void Clear();

    private:
        static const size_t MAX_ENTRIES = 256;
        static Entry s_buffer[MAX_ENTRIES];
        static size_t s_head;
        static size_t s_count;
        static std::mutex s_mutex;
    };

    // Convenience printf‑style log function (always goes to ring buffer)
    void Log(const char* fmt, ...);

    // Optional: toggle debug output (OutputDebugString) and file logging
    extern bool g_enableDebugOutput;
    extern bool g_enableFileLogging;

}} // namespace TalosForge::Native