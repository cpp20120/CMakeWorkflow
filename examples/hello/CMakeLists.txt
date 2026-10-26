cmake_minimum_required(VERSION 3.26)
project(WorkflowHello LANGUAGES CXX)

add_executable(hello main.cpp)
enable_testing()
add_test(NAME hello-smoke COMMAND hello)
set_tests_properties(hello-smoke PROPERTIES LABELS smoke)
