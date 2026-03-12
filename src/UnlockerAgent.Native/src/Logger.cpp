#include "Logger.h"
#include <cstdio>
#include <cstdarg>
#include <ctime>
#include <Windows.h>

namespace TalosForge { namespace Native {

    Logger::Entry Logger::s_buffer[MAX_ENTRIES];
    size_t Logger::s_head = 0;
    size_t Logger::s_count = 0;
    std::mutex Logger::s_mutex;

    bool g_enableDebugOutput = false;   // OutputDebugString – off by default (security)
    bool g_enableFileLogging = false;   // file logging – off by default

    void Logger::Log(Severity sev, const char* category, const char* fmt, ...)
    {
        char buf[512];
        va_list ap;
        va_start(ap, fmt);
        vsnprintf_s(buf, _TRUNCATE, fmt, ap);
        va_end(ap);

        Entry e;
        e.severity = sev;
        e.message = buf;

        {
            std::lock_guard<std::mutex> lock(s_mutex);
            s_buffer[s_head] = e;
            s_head = (s_head + 1) % MAX_ENTRIES;
            if (s_count < MAX_ENTRIES) ++s_count;
        }

        // Optional debug output (highly detectable!)
        if (g_enableDebugOutput) {
            OutputDebugStringA(buf);
        }

        // Optional file logging – implement if needed
    }

    std::vector<Logger::Entry> Logger::GetEntries()
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        std::vector<Entry> result;
        if (s_count == MAX_ENTRIES) {
            // buffer is full: start from s_head (oldest)
            for (size_t i = s_head; i < MAX_ENTRIES; ++i) result.push_back(s_buffer[i]);
            for (size_t i = 0; i < s_head; ++i) result.push_back(s_buffer[i]);
        } else {
            // not full: 0 .. s_head-1 are valid
            for (size_t i = 0; i < s_count; ++i) result.push_back(s_buffer[i]);
        }
        return result;
    }

    void Logger::Clear()
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        s_head = 0;
        s_count = 0;
    }

    void Log(const char* fmt, ...)
    {
        char buf[512];
        va_list ap;
        va_start(ap, fmt);
        vsnprintf_s(buf, _TRUNCATE, fmt, ap);
        va_end(ap);
        Logger::Log(Logger::Severity::Info, "General", "%s", buf);
    }

}} // namespace