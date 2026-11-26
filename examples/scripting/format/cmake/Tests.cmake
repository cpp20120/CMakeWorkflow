enable_testing()

function(register_smoke target)
  add_test(NAME smoke COMMAND "${target}")
endfunction()
