add_executable(EMKE.AudioSmoke
  src/main.cpp
)
target_link_libraries(EMKE.AudioSmoke PRIVATE EMKE.NativeAudio)
target_compile_features(EMKE.AudioSmoke PRIVATE cxx_std_20)

if(BUILD_TESTING)
  add_executable(EMKE.AudioSmoke.Override.Tests
    src/main.cpp
    tests/smoke_override_tests.cpp
  )
  target_compile_definitions(EMKE.AudioSmoke.Override.Tests PRIVATE
    EMKE_AUDIO_SMOKE_NO_ENTRYPOINT
  )
  target_include_directories(EMKE.AudioSmoke.Override.Tests PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/../EMKE.NativeAudio/include"
  )
  target_compile_features(EMKE.AudioSmoke.Override.Tests PRIVATE cxx_std_20)
  add_test(
    NAME EMKE.AudioSmoke.Override
    COMMAND EMKE.AudioSmoke.Override.Tests
  )
  add_test(
    NAME EMKE.AudioSmoke.InvalidScenario
    COMMAND ${CMAKE_COMMAND}
      -DSMOKE=$<TARGET_FILE:EMKE.AudioSmoke>
      -DSCENARIO=unsupported
      "-DEXPECTED_OUTPUT=usage: EMKE.AudioSmoke"
      -P ${CMAKE_CURRENT_SOURCE_DIR}/tests/assert_smoke_process.cmake
  )
  if(WIN32)
    # The hosted image has no compatible EMKE driver. This is an executable
    # public-C-ABI gate, not a source inspection or a mock.
    add_test(
      NAME EMKE.AudioSmoke.NoCompatibleDriver
      COMMAND ${CMAKE_COMMAND}
        -DSMOKE=$<TARGET_FILE:EMKE.AudioSmoke>
        -DSCENARIO=enumerate
        -DEXPECTED_OUTPUT=discovery=driverMissing
        -P ${CMAKE_CURRENT_SOURCE_DIR}/tests/assert_smoke_process.cmake
    )
  endif()
endif()
