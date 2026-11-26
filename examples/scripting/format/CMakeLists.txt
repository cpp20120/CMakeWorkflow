cmake_minimum_required(VERSION 3.26)
project(FormatGate LANGUAGES CXX)

include(cmake/Tests.cmake)

add_executable(smoke smoke.cpp)
register_smoke(smoke)
