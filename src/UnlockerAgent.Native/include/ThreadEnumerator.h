#pragma once
#include <windows.h>
#include <vector>

namespace TalosForge { namespace Native {

    bool EnumerateProcessThreads(DWORD pid, std::vector<DWORD>& outTids);

}} // namespace
