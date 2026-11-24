include_guard(GLOBAL)
include(GNUInstallDirs)
include(CMakePackageConfigHelpers)

# The caller owns project(), targets and CPack. The SDK contributes target-local
# defaults and installation rules; it does not mutate compiler or generator selection.
include("${CMAKE_CURRENT_LIST_DIR}/Policy.cmake")

function(cmwf_sdk_target target visibility)
    target_compile_features(${target} ${visibility} cxx_std_${CMWF_SDK_CXX_STANDARD})
    set_target_properties(${target} PROPERTIES
        CXX_EXTENSIONS ${CMWF_SDK_CXX_EXTENSIONS}
        COMPILE_WARNING_AS_ERROR ${CMWF_SDK_WARNINGS_AS_ERRORS})
endfunction()

function(cmwf_sdk_application target)
    cmwf_sdk_target(${target} PRIVATE)
    install(TARGETS ${target} RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR})
endfunction()

# The target must declare its public headers in the HEADERS file set.
# External public dependencies remain the caller's responsibility.
function(cmwf_sdk_library target)
    cmwf_sdk_target(${target} PUBLIC)
    add_library(${target}::${target} ALIAS ${target})
    install(TARGETS ${target} EXPORT ${target}Targets
        RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
        LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
        ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
        FILE_SET HEADERS DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
    set(package_dir "${CMAKE_INSTALL_LIBDIR}/cmake/${target}")
    configure_package_config_file(
        "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/PackageConfig.cmake.in"
        "${CMAKE_CURRENT_BINARY_DIR}/${target}Config.cmake"
        INSTALL_DESTINATION "${package_dir}")
    write_basic_package_version_file(
        "${CMAKE_CURRENT_BINARY_DIR}/${target}ConfigVersion.cmake"
        VERSION "${PROJECT_VERSION}" COMPATIBILITY ExactVersion)
    install(EXPORT ${target}Targets NAMESPACE ${target}:: DESTINATION "${package_dir}")
    install(FILES "${CMAKE_CURRENT_BINARY_DIR}/${target}Config.cmake"
                  "${CMAKE_CURRENT_BINARY_DIR}/${target}ConfigVersion.cmake"
        DESTINATION "${package_dir}")
endfunction()

# Retains local discovery evidence in the build tree and configure log.
function(cmwf_sdk_dependency name identity)
    string(SHA256 fingerprint "${identity}")
    if(DEFINED CMWF_DEPENDENCY_${name} AND NOT "${CMWF_DEPENDENCY_${name}}" STREQUAL "${fingerprint}")
        message(FATAL_ERROR "Installed SDK dependency changed: ${name}. Configure a fresh build tree.")
    endif()
    set(CMWF_DEPENDENCY_${name} "${fingerprint}" CACHE INTERNAL "Local SDK dependency fingerprint")
    message(STATUS "SDK dependency ${name}: ambient, unpinned; SHA-256=${fingerprint}")
endfunction()
