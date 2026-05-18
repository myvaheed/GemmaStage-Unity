# ==============================================================================
# GemmaStage.dll — CMake project (Task 1.1.2)
#
# Builds:
#   - GemmaStage.dll   (thin wrapper around llama.cpp C API)
#   - test_load.exe    (smoke test: load DLL, verify exports)
#
# Prerequisites:
#   - MSVC toolchain (Visual Studio 2022+ recommended)
#   - llama.cpp prebuilt DLLs in ../llama_prebuilt/
#   - llama.cpp submodule at ../llama.cpp/ (headers only)
#
# Usage:
#   cmake -B build -G "Visual Studio 17 2022" -A x64
#   cmake --build build --config Release
# ==============================================================================

cmake_minimum_required(VERSION 3.21)
project(GemmaStage VERSION 5.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Default to Release if not specified
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

# ==============================================================================
# Paths
# ==============================================================================

# Repository root is one level up from this CMakeLists.txt
get_filename_component(REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/.." ABSOLUTE)

set(LLAMA_CPP_DIR      "${REPO_ROOT}/llama.cpp")
set(LLAMA_PREBUILT_DIR "${REPO_ROOT}/llama_prebuilt")

# Pick the CUDA prebuilt as the primary source (llama.dll and mtmd.dll are
# identical across CUDA and Vulkan builds; we grab extra backend DLLs from both).
set(LLAMA_CUDA_DIR   "${LLAMA_PREBUILT_DIR}/llama-b8816-bin-win-cuda-12.4-x64")
set(LLAMA_VULKAN_DIR "${LLAMA_PREBUILT_DIR}/llama-b8816-bin-win-vulkan-x64")
set(CUDA_RUNTIME_DIR "${LLAMA_PREBUILT_DIR}/cudart-llama-bin-win-cuda-12.4-x64")

# Output directory — all DLLs, .exe, and runtime deps land here
set(RUNTIME_OUTPUT "${CMAKE_BINARY_DIR}/output")
file(MAKE_DIRECTORY "${RUNTIME_OUTPUT}")

# Force all build outputs into the same flat directory
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY         "${RUNTIME_OUTPUT}")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY         "${RUNTIME_OUTPUT}")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY         "${RUNTIME_OUTPUT}")
foreach(_cfg DEBUG RELEASE RELWITHDEBINFO MINSIZEREL)
    set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_${_cfg} "${RUNTIME_OUTPUT}")
    set(CMAKE_LIBRARY_OUTPUT_DIRECTORY_${_cfg} "${RUNTIME_OUTPUT}")
    set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY_${_cfg} "${RUNTIME_OUTPUT}")
endforeach()

# ==============================================================================
# Generate import libraries from prebuilt DLLs
# ==============================================================================

list(APPEND CMAKE_MODULE_PATH "${CMAKE_CURRENT_SOURCE_DIR}/cmake")
include(GenerateImportLib)

set(IMPORT_LIB_DIR "${CMAKE_BINARY_DIR}/importlibs")

generate_import_lib(
    DLL_PATH   "${LLAMA_CUDA_DIR}/llama.dll"
    OUTPUT_DIR "${IMPORT_LIB_DIR}"
    OUTPUT_LIB LLAMA_IMPORT_LIB
)

generate_import_lib(
    DLL_PATH   "${LLAMA_CUDA_DIR}/mtmd.dll"
    OUTPUT_DIR "${IMPORT_LIB_DIR}"
    OUTPUT_LIB MTMD_IMPORT_LIB
)

generate_import_lib(
    DLL_PATH   "${LLAMA_CUDA_DIR}/ggml.dll"
    OUTPUT_DIR "${IMPORT_LIB_DIR}"
    OUTPUT_LIB GGML_IMPORT_LIB
)

generate_import_lib(
    DLL_PATH   "${LLAMA_CUDA_DIR}/ggml-base.dll"
    OUTPUT_DIR "${IMPORT_LIB_DIR}"
    OUTPUT_LIB GGML_BASE_IMPORT_LIB
)

# ==============================================================================
# GemmaStage.dll
# ==============================================================================

add_library(GemmaStage SHARED
    src/GemmaStageRuntime.cc
    src/GemmaStageConversation.cc
    src/GemmaStage.cc
    src/LlamaCommonChatSupport.cc
    "${LLAMA_CPP_DIR}/common/reasoning-budget.cpp"
    "${LLAMA_CPP_DIR}/common/chat-auto-parser-generator.cpp"
    "${LLAMA_CPP_DIR}/common/chat-auto-parser-helpers.cpp"
    "${LLAMA_CPP_DIR}/common/chat-diff-analyzer.cpp"
    "${LLAMA_CPP_DIR}/common/chat-peg-parser.cpp"
    "${LLAMA_CPP_DIR}/common/chat.cpp"
    "${LLAMA_CPP_DIR}/common/json-partial.cpp"
    "${LLAMA_CPP_DIR}/common/json-schema-to-grammar.cpp"
    "${LLAMA_CPP_DIR}/common/log.cpp"
    "${LLAMA_CPP_DIR}/common/peg-parser.cpp"
    "${LLAMA_CPP_DIR}/common/regex-partial.cpp"
    "${LLAMA_CPP_DIR}/common/sampling.cpp"
    "${LLAMA_CPP_DIR}/common/unicode.cpp"
    "${LLAMA_CPP_DIR}/common/jinja/caps.cpp"
    "${LLAMA_CPP_DIR}/common/jinja/lexer.cpp"
    "${LLAMA_CPP_DIR}/common/jinja/parser.cpp"
    "${LLAMA_CPP_DIR}/common/jinja/runtime.cpp"
    "${LLAMA_CPP_DIR}/common/jinja/string.cpp"
    "${LLAMA_CPP_DIR}/common/jinja/value.cpp"
)

target_include_directories(GemmaStage
    PUBLIC
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE
        "${LLAMA_CPP_DIR}/common"
        "${LLAMA_CPP_DIR}/include"              # llama.h
        "${LLAMA_CPP_DIR}/ggml/include"          # ggml.h, ggml-backend.h, etc.
        "${LLAMA_CPP_DIR}/tools/mtmd"            # mtmd.h, mtmd-helper.h
        "${LLAMA_CPP_DIR}/vendor"                # nlohmann/json.hpp
)

# Tell the llama/mtmd/ggml headers to use __declspec(dllimport) for their symbols
target_compile_definitions(GemmaStage PRIVATE
    LLAMA_SHARED
    GEMMA_STAGE_BUILD      # so GemmaStage.h emits __declspec(dllexport)
)

target_link_libraries(GemmaStage PRIVATE
    "${LLAMA_IMPORT_LIB}"
    "${MTMD_IMPORT_LIB}"
    "${GGML_IMPORT_LIB}"
    "${GGML_BASE_IMPORT_LIB}"
)

# ==============================================================================
# test_load.exe
# ==============================================================================

add_executable(test_load
    tests/test_load.cc
)

target_include_directories(test_load PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
)

target_compile_definitions(test_load PRIVATE
    LLAMA_SHARED
)

target_link_libraries(test_load PRIVATE
    GemmaStage
)

# ==============================================================================
# test_poc.exe
# ==============================================================================

add_executable(test_poc
    tests/test_poc.cc
)

target_include_directories(test_poc PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
)

target_compile_definitions(test_poc PRIVATE
    LLAMA_SHARED
)

target_link_libraries(test_poc PRIVATE
    GemmaStage
    dxgi
    psapi
)

# ==============================================================================
# Post-build: copy runtime DLLs into the output directory
# ==============================================================================

# Collect all DLLs that need to be co-located with GemmaStage.dll at runtime
set(RUNTIME_DLLS "")
set(RUNTIME_ASSETS
    "${CMAKE_CURRENT_SOURCE_DIR}/tests/res/audio_sample.wav"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests/res/image_sample.png"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests/res/whiteboard_sample.png"
)

# Core llama.cpp DLLs (identical between CUDA and Vulkan builds)
list(APPEND RUNTIME_DLLS
    "${LLAMA_CUDA_DIR}/llama.dll"
    "${LLAMA_CUDA_DIR}/mtmd.dll"
    "${LLAMA_CUDA_DIR}/ggml.dll"
    "${LLAMA_CUDA_DIR}/ggml-base.dll"
    "${LLAMA_CUDA_DIR}/ggml-rpc.dll"
    "${LLAMA_CUDA_DIR}/libomp140.x86_64.dll"
)

# CUDA backend
if(EXISTS "${LLAMA_CUDA_DIR}/ggml-cuda.dll")
    list(APPEND RUNTIME_DLLS "${LLAMA_CUDA_DIR}/ggml-cuda.dll")
endif()

# Vulkan backend
if(EXISTS "${LLAMA_VULKAN_DIR}/ggml-vulkan.dll")
    list(APPEND RUNTIME_DLLS "${LLAMA_VULKAN_DIR}/ggml-vulkan.dll")
endif()

# CUDA runtime redistributables
if(EXISTS "${CUDA_RUNTIME_DIR}")
    file(GLOB _cuda_rt_dlls "${CUDA_RUNTIME_DIR}/*.dll")
    list(APPEND RUNTIME_DLLS ${_cuda_rt_dlls})
endif()

# CPU backend DLLs (all variants — ggml auto-selects the optimal one)
file(GLOB _cpu_dlls "${LLAMA_CUDA_DIR}/ggml-cpu-*.dll")
list(APPEND RUNTIME_DLLS ${_cpu_dlls})

# Copy each DLL at configure time (so the output directory is ready immediately)
foreach(_dll IN LISTS RUNTIME_DLLS)
    get_filename_component(_fname "${_dll}" NAME)
    if(NOT EXISTS "${RUNTIME_OUTPUT}/${_fname}")
        file(COPY "${_dll}" DESTINATION "${RUNTIME_OUTPUT}")
    endif()
endforeach()

foreach(_asset IN LISTS RUNTIME_ASSETS)
    get_filename_component(_fname "${_asset}" NAME)
    if(EXISTS "${_asset}" AND NOT EXISTS "${RUNTIME_OUTPUT}/${_fname}")
        file(COPY "${_asset}" DESTINATION "${RUNTIME_OUTPUT}")
    endif()
endforeach()

# Also copy at build time (in case prebuilts are updated)
set(_copy_commands "")
foreach(_dll IN LISTS RUNTIME_DLLS)
    list(APPEND _copy_commands
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${_dll}" "${RUNTIME_OUTPUT}/"
    )
endforeach()

foreach(_asset IN LISTS RUNTIME_ASSETS)
    if(EXISTS "${_asset}")
        list(APPEND _copy_commands
            COMMAND ${CMAKE_COMMAND} -E copy_if_different "${_asset}" "${RUNTIME_OUTPUT}/"
        )
    endif()
endforeach()

add_custom_command(TARGET GemmaStage POST_BUILD
    ${_copy_commands}
    COMMENT "Syncing runtime DLLs to output directory..."
)

# ==============================================================================
# Summary
# ==============================================================================

message(STATUS "")
message(STATUS "=== GemmaStage build configuration ===")
message(STATUS "  Repository root:    ${REPO_ROOT}")
message(STATUS "  llama.cpp headers:  ${LLAMA_CPP_DIR}/include")
message(STATUS "  Prebuilt CUDA:      ${LLAMA_CUDA_DIR}")
message(STATUS "  Prebuilt Vulkan:    ${LLAMA_VULKAN_DIR}")
message(STATUS "  CUDA runtime:       ${CUDA_RUNTIME_DIR}")
message(STATUS "  Import libs dir:    ${IMPORT_LIB_DIR}")
message(STATUS "  Output directory:   ${RUNTIME_OUTPUT}")
message(STATUS "  llama.lib:          ${LLAMA_IMPORT_LIB}")
message(STATUS "  mtmd.lib:           ${MTMD_IMPORT_LIB}")
message(STATUS "  ggml.lib:           ${GGML_IMPORT_LIB}")
message(STATUS "  ggml-base.lib:      ${GGML_BASE_IMPORT_LIB}")
message(STATUS "")
