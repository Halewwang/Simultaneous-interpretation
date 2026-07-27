set(
  EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY
  "${CMAKE_CURRENT_BINARY_DIR}/integration/Release"
)

add_library(EMKE.NativeAudio.IntegrationHost SHARED
  ../EMKE.NativeAudio/src/audio_runtime.cpp
  ../EMKE.NativeAudio/src/fake_audio_backend.cpp
  ../EMKE.NativeAudio/src/native_audio_api.cpp
)
target_compile_definitions(EMKE.NativeAudio.IntegrationHost PRIVATE
  EMKE_NATIVE_AUDIO_EXPORTS
  EMKE_NATIVE_AUDIO_TEST_HOOKS
  EMKE_NATIVE_AUDIO_TEST_HOOKS_EXPORTS
)
target_include_directories(EMKE.NativeAudio.IntegrationHost
  PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/../EMKE.NativeAudio/include"
  PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../EMKE.NativeAudio/src"
)
target_compile_features(EMKE.NativeAudio.IntegrationHost PUBLIC cxx_std_20)
set_target_properties(EMKE.NativeAudio.IntegrationHost PROPERTIES
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES
  ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY}"
  LIBRARY_OUTPUT_DIRECTORY_RELEASE "${EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY}"
  RUNTIME_OUTPUT_DIRECTORY_RELEASE "${EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY}"
)

add_executable(EMKE.NativeAudio.Tests
  ../EMKE.NativeAudio/src/device_catalog.cpp
  ../EMKE.NativeAudio/src/device_notifications.cpp
  ../EMKE.NativeAudio/src/fake_audio_backend.cpp
  ../EMKE.NativeAudio/src/pcm_converter.cpp
  ../EMKE.NativeAudio/src/audio_worker.cpp
  ../EMKE.NativeAudio/src/wasapi_stream.cpp
  src/abi_tests.cpp
  src/audio_worker_tests.cpp
  src/device_catalog_tests.cpp
  src/pcm_converter_tests.cpp
  src/spsc_ring_tests.cpp
  src/test_main.cpp
)

target_link_libraries(
  EMKE.NativeAudio.Tests PRIVATE EMKE.NativeAudio.IntegrationHost
)
target_include_directories(EMKE.NativeAudio.Tests PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/../EMKE.NativeAudio/src"
  "${CMAKE_CURRENT_SOURCE_DIR}/../include"
  "${CMAKE_CURRENT_SOURCE_DIR}/../../shared"
)
target_compile_definitions(EMKE.NativeAudio.Tests PRIVATE
  EMKE_NATIVE_AUDIO_DEVICE_TESTS
  EMKE_NATIVE_AUDIO_REALTIME_INSTRUMENTATION
  EMKE_AUDIO_PCM_FIXTURE_DIRECTORY="${CMAKE_CURRENT_SOURCE_DIR}/../../../Shared/TestVectors/Audio"
)
set_target_properties(EMKE.NativeAudio.Tests PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY_RELEASE
    "${EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY}"
)

if(WIN32)
  get_target_property(
    EMKE_NATIVE_AUDIO_HOST_RUNTIME_DIRECTORY
    EMKE.NativeAudio.IntegrationHost
    RUNTIME_OUTPUT_DIRECTORY_RELEASE
  )
  get_target_property(
    EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY_ACTUAL
    EMKE.NativeAudio.Tests
    RUNTIME_OUTPUT_DIRECTORY_RELEASE
  )
  if(NOT "${EMKE_NATIVE_AUDIO_HOST_RUNTIME_DIRECTORY}" STREQUAL
      "${EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY_ACTUAL}")
    message(FATAL_ERROR
      "Native tests and IntegrationHost must share a Windows runtime directory."
    )
  endif()
endif()

add_test(NAME EMKE.NativeAudio.Abi COMMAND EMKE.NativeAudio.Tests Abi)
add_test(NAME EMKE.NativeAudio.FakeBackend COMMAND EMKE.NativeAudio.Tests FakeBackend)
add_test(NAME EMKE.NativeAudio.Ring COMMAND EMKE.NativeAudio.Tests Ring)
add_test(NAME EMKE.NativeAudio.PCM COMMAND EMKE.NativeAudio.Tests PCM)
add_test(NAME EMKE.NativeAudio.Device COMMAND EMKE.NativeAudio.Tests Device)
add_test(
  NAME EMKE.NativeAudio.Lifecycle
  COMMAND EMKE.NativeAudio.Tests Lifecycle
)
add_test(NAME EMKE.NativeAudio.Worker COMMAND EMKE.NativeAudio.Tests Worker)
add_test(
  NAME EMKE.NativeAudio.Realtime
  COMMAND EMKE.NativeAudio.Tests Realtime
)

if(WIN32)
  target_link_libraries(EMKE.NativeAudio.Tests PRIVATE ole32 propsys winmm)
endif()

add_executable(EMKE.NativeAudio.CAbiIntegration.Tests
  src/c_abi_integration_tests.cpp
)
target_link_libraries(
  EMKE.NativeAudio.CAbiIntegration.Tests
  PRIVATE EMKE.NativeAudio.IntegrationHost
)
target_include_directories(EMKE.NativeAudio.CAbiIntegration.Tests PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/../EMKE.NativeAudio/src"
)
set_target_properties(EMKE.NativeAudio.CAbiIntegration.Tests PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY_RELEASE
    "${EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY}"
)

add_test(
  NAME EMKE.NativeAudio.CAbiIntegration
  COMMAND EMKE.NativeAudio.CAbiIntegration.Tests
)

add_executable(EMKE.DriverBridge.Tests
  ../../driver/EMKE.VirtualAudio/src/emke_audio_bridge.cpp
  ../../driver/tests/bridge-behavior-tests.cpp
)
target_include_directories(EMKE.DriverBridge.Tests PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/../../shared"
  "${CMAKE_CURRENT_SOURCE_DIR}/../../driver/EMKE.VirtualAudio/src"
)
target_compile_features(EMKE.DriverBridge.Tests PRIVATE cxx_std_17)
add_test(
  NAME EMKE.DriverBridge.Behavior
  COMMAND EMKE.DriverBridge.Tests
)
