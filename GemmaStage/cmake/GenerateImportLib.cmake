# GenerateImportLib.cmake
# Generate a MSVC import library (.lib) from a Windows DLL.
#
# Usage:
#   generate_import_lib(
#       DLL_PATH    "path/to/some.dll"
#       OUTPUT_DIR  "${CMAKE_BINARY_DIR}/importlibs"
#       OUTPUT_LIB  output_lib_var          # variable receives path to .lib
#   )
#
# Requires: dumpbin.exe and lib.exe from the MSVC toolchain.

function(generate_import_lib)
    cmake_parse_arguments(GEN "" "DLL_PATH;OUTPUT_DIR;OUTPUT_LIB" "" ${ARGN})

    if(NOT GEN_DLL_PATH)
        message(FATAL_ERROR "generate_import_lib: DLL_PATH is required")
    endif()
    if(NOT GEN_OUTPUT_DIR)
        message(FATAL_ERROR "generate_import_lib: OUTPUT_DIR is required")
    endif()
    if(NOT GEN_OUTPUT_LIB)
        message(FATAL_ERROR "generate_import_lib: OUTPUT_LIB is required")
    endif()

    get_filename_component(_dll_name "${GEN_DLL_PATH}" NAME_WE)
    set(_def_file "${GEN_OUTPUT_DIR}/${_dll_name}.def")
    set(_lib_file "${GEN_OUTPUT_DIR}/${_dll_name}.lib")

    file(MAKE_DIRECTORY "${GEN_OUTPUT_DIR}")

    # --- Step 1: Dump exports and generate .def -------------------------------------------
    if(NOT EXISTS "${_lib_file}")
        message(STATUS "Generating import library for ${_dll_name}.dll ...")

        # Find dumpbin and lib in the MSVC toolchain directory
        # They live in the same directory as the compiler (cl.exe)
        get_filename_component(_compiler_dir "${CMAKE_CXX_COMPILER}" DIRECTORY)
        
        find_program(_dumpbin dumpbin
            HINTS "${_compiler_dir}"
            PATHS "${_compiler_dir}"
            NO_DEFAULT_PATH
        )
        if(NOT _dumpbin)
            find_program(_dumpbin dumpbin)
        endif()
        if(NOT _dumpbin)
            message(FATAL_ERROR "Cannot find dumpbin.exe. Make sure MSVC Build Tools are installed.")
        endif()

        execute_process(
            COMMAND "${_dumpbin}" /EXPORTS "${GEN_DLL_PATH}"
            OUTPUT_VARIABLE _exports_raw
            RESULT_VARIABLE _rc
        )
        if(NOT _rc EQUAL 0)
            message(FATAL_ERROR "dumpbin /EXPORTS failed for ${GEN_DLL_PATH}")
        endif()

        # Parse dumpbin output.
        # Exported lines look like:
        #    ordinal hint RVA  name
        # e.g.  1    0 00001234 llama_backend_init
        # We extract the fourth column (the name).
        string(REPLACE "\r" "" _exports_raw "${_exports_raw}")
        string(REPLACE "\n" ";" _lines "${_exports_raw}")

        set(_names "")
        set(_in_exports FALSE)
        foreach(_line IN LISTS _lines)
            if(_line MATCHES "ordinal.*hint.*RVA.*name")
                set(_in_exports TRUE)
                continue()
            endif()
            if(_line MATCHES "^[ \t]*Summary")
                set(_in_exports FALSE)
                continue()
            endif()
            if(_in_exports AND _line MATCHES "^[ \t]+[0-9]+[ \t]+[0-9A-Fa-f]+[ \t]+[0-9A-Fa-f]+[ \t]+([^ \t]+)")
                list(APPEND _names "${CMAKE_MATCH_1}")
            endif()
        endforeach()

        list(LENGTH _names _n)
        if(_n EQUAL 0)
            message(FATAL_ERROR "No exports found in ${GEN_DLL_PATH}")
        endif()
        message(STATUS "  Found ${_n} exported symbols in ${_dll_name}.dll")

        # Write .def
        set(_def_content "LIBRARY ${_dll_name}\nEXPORTS\n")
        foreach(_sym IN LISTS _names)
            string(APPEND _def_content "    ${_sym}\n")
        endforeach()
        file(WRITE "${_def_file}" "${_def_content}")

        # --- Step 2: Generate .lib from .def -----------------------------------------------
        find_program(_lib_tool lib
            HINTS "${_compiler_dir}"
            PATHS "${_compiler_dir}"
            NO_DEFAULT_PATH
        )
        if(NOT _lib_tool)
            find_program(_lib_tool lib)
        endif()
        if(NOT _lib_tool)
            message(FATAL_ERROR "Cannot find lib.exe. Make sure MSVC Build Tools are installed.")
        endif()

        execute_process(
            COMMAND "${_lib_tool}" "/DEF:${_def_file}" "/OUT:${_lib_file}" /MACHINE:X64
            RESULT_VARIABLE _rc2
        )
        if(NOT _rc2 EQUAL 0)
            message(FATAL_ERROR "lib.exe failed to generate ${_lib_file}")
        endif()
        message(STATUS "  Generated ${_lib_file}")
    else()
        message(STATUS "Import library ${_lib_file} already exists, skipping generation.")
    endif()

    set(${GEN_OUTPUT_LIB} "${_lib_file}" PARENT_SCOPE)
endfunction()
