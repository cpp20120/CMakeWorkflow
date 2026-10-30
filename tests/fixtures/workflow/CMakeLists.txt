cmake_minimum_required(VERSION 3.26)
project(WorkflowProbe LANGUAGES CXX)
enable_testing()
add_executable(first first.cpp)
add_executable(second second.cpp)
target_include_directories(second PRIVATE "${CMAKE_BINARY_DIR}")
set_target_properties(first PROPERTIES OUTPUT_NAME "first worker")
set_target_properties(second PROPERTIES OUTPUT_NAME "second worker")
add_custom_target(existing_action COMMAND "${CMAKE_COMMAND}"
  "-DACTION_LOG=${CMAKE_BINARY_DIR}/action-invocations.txt"
  -P "${CMAKE_CURRENT_SOURCE_DIR}/action.cmake" VERBATIM)
add_test(NAME native_second COMMAND second)
set_tests_properties(native_second PROPERTIES LABELS smoke)
