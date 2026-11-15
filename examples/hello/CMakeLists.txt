cmake_minimum_required(VERSION 3.26)
project(WorkflowHello LANGUAGES CXX)

add_executable(hello main.cpp)
find_package(Threads REQUIRED)
add_executable(workflow_demo workflow_demo.cpp)
target_compile_features(workflow_demo PRIVATE cxx_std_11)
target_link_libraries(workflow_demo PRIVATE Threads::Threads)

add_custom_target(stage_message
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/message.txt"
        "${CMAKE_CURRENT_BINARY_DIR}/workflow-message.txt"
    BYPRODUCTS "${CMAKE_CURRENT_BINARY_DIR}/workflow-message.txt"
    VERBATIM)

enable_testing()
add_test(NAME hello-smoke COMMAND hello)
set_tests_properties(hello-smoke PROPERTIES LABELS smoke)
option(ENABLE_SCHEDULER_DEMO_TEST "Enable the timed CTest task for the workflow scheduler example" OFF)
if(ENABLE_SCHEDULER_DEMO_TEST)
    add_test(NAME scheduler-smoke COMMAND workflow_demo --hold)
endif()
