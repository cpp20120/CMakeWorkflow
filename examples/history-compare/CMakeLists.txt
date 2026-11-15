cmake_minimum_required(VERSION 3.27)
project(WorkflowHistoryCompare LANGUAGES CXX)

# Publish the target metadata CMakeWorkflow reads when adopting this build tree.
cmake_file_api(QUERY API_VERSION 1 CODEMODEL 2)

option(USE_NTH_ELEMENT "Select the median with std::nth_element" OFF)

add_executable(median_bench main.cpp)
target_compile_features(median_bench PRIVATE cxx_std_17)
target_compile_definitions(median_bench PRIVATE USE_NTH_ELEMENT=$<BOOL:${USE_NTH_ELEMENT}>)
