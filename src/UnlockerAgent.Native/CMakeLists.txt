cmake_minimum_required(VERSION 3.20)
project(TalosForgeUnlockerAgentNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

option(TALOSFORGE_USE_POLYHOOK "Enable PolyHook integration points." ON)
set(TALOSFORGE_POLYHOOK_DIR "" CACHE PATH "Path to PolyHook_2_0 checkout")

add_library(TalosForgeUnlockerAgentNative SHARED
    src/UnlockerAgentExports.cpp
)

target_include_directories(TalosForgeUnlockerAgentNative
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

if(MSVC)
    target_compile_options(TalosForgeUnlockerAgentNative PRIVATE /W4 /EHsc)
    target_compile_definitions(TalosForgeUnlockerAgentNative PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()

if(TALOSFORGE_USE_POLYHOOK)
    if(TALOSFORGE_POLYHOOK_DIR AND EXISTS "${TALOSFORGE_POLYHOOK_DIR}/polyhook2")
        target_include_directories(TalosForgeUnlockerAgentNative
            PUBLIC
                ${TALOSFORGE_POLYHOOK_DIR}
        )
        target_compile_definitions(TalosForgeUnlockerAgentNative PRIVATE TALOSFORGE_POLYHOOK_AVAILABLE=1)
    else()
        target_compile_definitions(TalosForgeUnlockerAgentNative PRIVATE TALOSFORGE_POLYHOOK_AVAILABLE=0)
    endif()
else()
    target_compile_definitions(TalosForgeUnlockerAgentNative PRIVATE TALOSFORGE_POLYHOOK_AVAILABLE=0)
endif()

set_target_properties(TalosForgeUnlockerAgentNative PROPERTIES
    OUTPUT_NAME "TalosForge.UnlockerAgent.Native"
)
