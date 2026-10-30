if(NOT DEFINED OUTPUT)
  message(FATAL_ERROR "Expected OUTPUT path")
endif()
foreach(_family IN ITEMS first second)
  set(_dir "${OUTPUT}/${_family}")
  file(MAKE_DIRECTORY "${_dir}")
  file(WRITE "${_dir}/manifest.json"
    "{\"cases\":{\"${_family}\":{\"args\":[\"--scenario\",\"chain\"]}}}\n")
  file(WRITE "${_dir}/summary.json"
    "{\"status\":\"passed\",\"cases\":{\"${_family}\":{\"metrics\":{\"run_p50_us\":{\"scale\":1000000,\"count\":10}}}}}\n")
  file(WRITE "${_dir}/runs.jsonl" "")
  foreach(_round RANGE 0 9)
    file(APPEND "${_dir}/runs.jsonl"
      "{\"case\":\"${_family}\",\"round\":${_round},\"warmup\":false,\"result\":\"0\",\"output\":{\"run_p50_us\":${_round}}}\n")
  endforeach()
  message(STATUS "Harness result: ${_dir}/summary.json")
endforeach()
