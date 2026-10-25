# CMake build of the game client. It builds the same six projects as TClient.sln
# (TClient, Engine Lib, TChart, TCML, TComp, HwidLib). See BUILD-CMAKE.md.

cmake_minimum_required(VERSION 3.25)
project(TClient LANGUAGES C CXX)
if(WIN32)
    enable_language(RC)
endif()

include(cmake/TClientCommon.cmake)

set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")

# bgfx, bx and bimg for the bgfx render backend (Lib/3rdParty/bgfx.cmake/VERSION.txt).
# Only the libraries: the shader compiler is a separate 64-bit build (build-shaderc.ps1).
set(BGFX_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
set(BGFX_BUILD_TOOLS_BIN2C OFF CACHE BOOL "" FORCE)
set(BGFX_BUILD_TOOLS_GEOMETRY OFF CACHE BOOL "" FORCE)
set(BGFX_BUILD_TOOLS_TEXTURE OFF CACHE BOOL "" FORCE)
set(BGFX_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(BGFX_INSTALL OFF CACHE BOOL "" FORCE)
set(BGFX_CUSTOM_TARGETS OFF CACHE BOOL "" FORCE)
set(BGFX_CONFIG_VIDEO OFF CACHE BOOL "" FORCE)
# The engine calls the render layer from its main thread and its loader thread itself.
set(BGFX_CONFIG_MULTITHREADED OFF CACHE BOOL "" FORCE)
add_subdirectory("${TCLIENT_LIB_3RD}/bgfx.cmake" bgfx EXCLUDE_FROM_ALL)
# Each text string of the engine is its own texture: a busy town goes past bgfx's 4096.
target_compile_definitions(bgfx PRIVATE BGFX_CONFIG_MAX_TEXTURES=16384)
# bimg's ASTC encoder uses _mm_popcnt_u64 when clang defines __POPCNT__; it does not exist in
# 32-bit code.
target_compile_definitions(bimg PRIVATE ASTCENC_POPCNT=0)
# renderer_webgpu.cpp narrows 64-bit sizes to size_t, an error for clang in 32-bit code.
if(NOT CMAKE_CXX_COMPILER_ID STREQUAL "MSVC")
    target_compile_options(bgfx PRIVATE -Wno-c++11-narrowing)
endif()

# SDL3 for the window, its events and the text input (Lib/3rdParty/SDL3/VERSION.txt). A static
# library with only the subsystems the client uses: audio is miniaudio, rendering is bgfx.
set(SDL_SHARED OFF CACHE BOOL "" FORCE)
set(SDL_STATIC ON CACHE BOOL "" FORCE)
set(SDL_TEST_LIBRARY OFF CACHE BOOL "" FORCE)
set(SDL_TESTS OFF CACHE BOOL "" FORCE)
set(SDL_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SDL_INSTALL OFF CACHE BOOL "" FORCE)
foreach(_SDL_SUB AUDIO GPU RENDER CAMERA JOYSTICK HAPTIC HIDAPI SENSOR DIALOG TRAY)
    set(SDL_${_SDL_SUB} OFF CACHE BOOL "" FORCE)
endforeach()
add_subdirectory("${TCLIENT_LIB_3RD}/SDL3" SDL3 EXCLUDE_FROM_ALL)

add_subdirectory("${TCLIENT_LIB_OWN}/TCML/TCML" TCML)
add_subdirectory("${TCLIENT_LIB_OWN}/Engine Lib/Engine Lib" EngineLib)
add_subdirectory("${TCLIENT_LIB_OWN}/TComp/TComp" TComp)
add_subdirectory("${TCLIENT_LIB_OWN}/TChart/TChart" TChart)
# The hardware ID reads Windows' WMI: Windows only.
if(WIN32)
    add_subdirectory("${TCLIENT_LIB_OWN}/HwidLib/HwidLib" HwidLib)
endif()
option(TCLIENT_DEPLOY_TO_GAME "Copy TClient.exe into the Game folder after building" OFF)
add_subdirectory(TClient)
