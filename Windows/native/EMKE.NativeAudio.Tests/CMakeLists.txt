add_executable(EMKE.NativeAudio.Tests
  ../EMKE.NativeAudio/src/device_catalog.cpp
  ../EMKE.NativeAudio/src/device_notifications.cpp
  ../EMKE.NativeAudio/src/fake_audio_backend.cpp
  ../EMKE.NativeAudio/src/pcm_converter.cpp
  src/abi_tests.cpp
  src/device_catalog_tests.cpp
  src/pcm_converter_tests.cpp
  src/spsc_ring_tests.cpp
  src/test_main.cpp
)

target_link_libraries(EMKE.NativeAudio.Tests PRIVATE EMKE.NativeAudio)
target_include_directories(EMKE.NativeAudio.Tests PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/../EMKE.NativeAudio/src"
  "${CMAKE_CURRENT_SOURCE_DIR}/../include"
)
target_compile_definitions(EMKE.NativeAudio.Tests PRIVATE
  EMKE_NATIVE_AUDIO_DEVICE_TESTS
  EMKE_AUDIO_PCM_FIXTURE_DIRECTORY="${CMAKE_CURRENT_SOURCE_DIR}/../../../Shared/TestVectors/Audio"
)

add_test(NAME EMKE.NativeAudio.Abi COMMAND EMKE.NativeAudio.Tests Abi)
add_test(NAME EMKE.NativeAudio.FakeBackend COMMAND EMKE.NativeAudio.Tests FakeBackend)
add_test(NAME EMKE.NativeAudio.Ring COMMAND EMKE.NativeAudio.Tests Ring)
add_test(NAME EMKE.NativeAudio.PCM COMMAND EMKE.NativeAudio.Tests PCM)
add_test(NAME EMKE.NativeAudio.Device COMMAND EMKE.NativeAudio.Tests Device)

if(WIN32)
  target_link_libraries(EMKE.NativeAudio.Tests PRIVATE ole32 propsys)
endif()

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
  ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_BINARY_DIR}/integration/Release"
  LIBRARY_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_BINARY_DIR}/integration/Release"
  RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_BINARY_DIR}/integration/Release"
)

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
  RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_BINARY_DIR}/integration/Release"
)

add_test(
  NAME EMKE.NativeAudio.CAbiIntegration
  COMMAND EMKE.NativeAudio.CAbiIntegration.Tests
)
