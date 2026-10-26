cmake_minimum_required(VERSION 3.26)
project(LifecycleProbe LANGUAGES CXX)
file(APPEND "${CMAKE_BINARY_DIR}/configured" "configure\n")
file(WRITE "${CMAKE_BINARY_DIR}/environment" "$ENV{LIFECYCLE_ENV}")
include(rules.cmake)
add_custom_target(campaign COMMAND "${CMAKE_COMMAND}" -E touch "${CMAKE_BINARY_DIR}/CAMPAIGN")
add_executable(slow slow.cpp)
add_custom_command(TARGET slow PRE_LINK
  COMMAND "${CMAKE_COMMAND}" -E touch "${CMAKE_BINARY_DIR}/building"
  COMMAND "${CMAKE_COMMAND}" -E sleep 30
  COMMAND "${CMAKE_COMMAND}" -E touch "${CMAKE_BINARY_DIR}/BUILD_COMPLETED")
