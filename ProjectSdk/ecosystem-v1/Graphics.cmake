include_guard(GLOBAL)
find_package(Vulkan 1.0 QUIET)
if(NOT Vulkan_FOUND)
    message(FATAL_ERROR "Native.Graphics requires installed Vulkan headers and loader. Set VULKAN_SDK or CMAKE_PREFIX_PATH.")
endif()
find_program(CMWF_GLSLC NAMES glslc HINTS "$ENV{VULKAN_SDK}/bin")
if(NOT CMWF_GLSLC OR NOT EXISTS "${CMWF_GLSLC}")
    message(FATAL_ERROR "Native.Graphics requires glslc. Install it explicitly or set CMWF_GLSLC to its absolute path.")
endif()
execute_process(COMMAND "${CMWF_GLSLC}" --version RESULT_VARIABLE _cmwf_glslc_result
    OUTPUT_QUIET ERROR_QUIET TIMEOUT 10)
if(NOT _cmwf_glslc_result EQUAL 0)
    message(FATAL_ERROR "Native.Graphics found an incompatible glslc: ${CMWF_GLSLC}")
endif()
file(SHA256 "${CMWF_GLSLC}" _cmwf_glslc_hash)
file(SHA256 "${Vulkan_LIBRARY}" _cmwf_vulkan_hash)
cmwf_sdk_dependency(Vulkan "${Vulkan_VERSION}|${Vulkan_INCLUDE_DIR}|${Vulkan_LIBRARY}|${_cmwf_vulkan_hash}")
cmwf_sdk_dependency(glslc "${CMWF_GLSLC}|${_cmwf_glslc_hash}")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${Vulkan_LIBRARY}" "${CMWF_GLSLC}")

function(cmwf_sdk_shader target source output)
    get_filename_component(filename "${source}" NAME)
    get_filename_component(directory "${source}" DIRECTORY)
    set(artifact "${CMAKE_CURRENT_BINARY_DIR}/shaders/${filename}.spv")
    add_custom_command(OUTPUT "${artifact}"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${CMAKE_CURRENT_BINARY_DIR}/shaders"
        COMMAND "${CMWF_GLSLC}" -I "${directory}" -MD -MF "${artifact}.d" -o "${artifact}" "${source}"
        DEPENDS "${source}" "${CMWF_GLSLC}"
        DEPFILE "${artifact}.d" VERBATIM)
    target_sources(${target} PRIVATE "${artifact}")
    set(${output} "${artifact}" PARENT_SCOPE)
endfunction()
