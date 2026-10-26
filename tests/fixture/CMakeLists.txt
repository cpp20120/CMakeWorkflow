cmake_minimum_required(VERSION 3.26)
project(CMakeWorkflowFixture LANGUAGES CXX)
add_executable(fixture_worker worker.cpp)
include("${CMAKE_CURRENT_LIST_DIR}/../../cmake/CMakeWorkflowTargetBridge.cmake")
if(WORKFLOW_RUNNER_EXECUTABLE)
  cmake_workflow_add_target_bridge(fixture_bridge_worker
    TARGET fixture_worker ARGS "--fixture" "hello world")
endif()
