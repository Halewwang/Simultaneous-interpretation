if(NOT DEFINED SMOKE OR NOT DEFINED SCENARIO OR NOT DEFINED EXPECTED_OUTPUT)
  message(FATAL_ERROR "SMOKE, SCENARIO, and EXPECTED_OUTPUT are required.")
endif()

execute_process(
  COMMAND "${SMOKE}" --scenario "${SCENARIO}"
  RESULT_VARIABLE smoke_exit
  OUTPUT_VARIABLE smoke_stdout
  ERROR_VARIABLE smoke_stderr
)
set(smoke_output "${smoke_stdout}${smoke_stderr}")
if(smoke_exit EQUAL 0)
  message(FATAL_ERROR
    "Smoke scenario '${SCENARIO}' unexpectedly succeeded. Output: ${smoke_output}"
  )
endif()
if(NOT smoke_output MATCHES "${EXPECTED_OUTPUT}")
  message(FATAL_ERROR
    "Smoke scenario '${SCENARIO}' did not emit '${EXPECTED_OUTPUT}'. Output: ${smoke_output}"
  )
endif()
