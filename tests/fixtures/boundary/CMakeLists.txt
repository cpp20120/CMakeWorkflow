cmake_minimum_required(VERSION 3.26)
project(BoundaryProbe LANGUAGES CXX)
file(APPEND "${CMAKE_BINARY_DIR}/configure-count.txt" "configure\n")
file(WRITE "${CMAKE_BINARY_DIR}/preset-env.txt" "$ENV{PRESET_ENV}")
if(FAIL_CONFIGURE)
  message(FATAL_ERROR "REQUESTED_CONFIGURE_FAILURE")
endif()
if(SLOW_CONFIGURE)
  file(WRITE "${CMAKE_BINARY_DIR}/configuring" "started")
  execute_process(COMMAND "${CMAKE_COMMAND}" -E sleep 30)
endif()
add_executable(worker worker.cpp)
add_library(native_library STATIC library.cpp)
add_custom_target(fuzz_campaign COMMAND "${CMAKE_COMMAND}" -E touch "${CMAKE_BINARY_DIR}/CAMPAIGN_RAN")
file(WRITE "${CMAKE_BINARY_DIR}/replacement.bin" "BROKEN_EXECUTABLE")
add_custom_target(clobber_worker
  COMMAND "${CMAKE_COMMAND}" -E copy "${CMAKE_BINARY_DIR}/replacement.bin" "$<TARGET_FILE:worker>"
  DEPENDS worker)
add_custom_target(nested
  COMMAND "${TEST_CLI}" target build --build-dir "${CMAKE_BINARY_DIR}" --target worker)
add_executable(prepared_worker prepared.cpp)
add_custom_target(prepared_nested
  COMMAND "${TEST_CLI}" target run --prepared --build-dir "${CMAKE_BINARY_DIR}"
    --target prepared_worker --config "$<CONFIG>" -- "${TEST_CLI}" "${CMAKE_BINARY_DIR}"
  DEPENDS prepared_worker VERBATIM)
enable_testing()
add_test(NAME worker_test COMMAND worker)
