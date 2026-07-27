set(
  EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY
  "${CMAKE_CURRENT_BINARY_DIR}/integration/Release"
)

add_library(EMKE.NativeAudio.IntegrationHost SHARED
  ../EMKE.NativeAudio/src/audio_runtime.cpp
  ../EMKE.NativeAudio/src/device_catalog.cpp
  ../EMKE.NativeAudio/src/endpoint_snapshot.cpp
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
  PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/../EMKE.NativeAudio/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../shared"
)
get_target_property(
  EMKE_NATIVE_AUDIO_INTEGRATION_HOST_INCLUDE_DIRECTORIES
  EMKE.NativeAudio.IntegrationHost
  INCLUDE_DIRECTORIES
)
list(FIND
  EMKE_NATIVE_AUDIO_INTEGRATION_HOST_INCLUDE_DIRECTORIES
  "${CMAKE_CURRENT_SOURCE_DIR}/../../shared"
  EMKE_NATIVE_AUDIO_INTEGRATION_HOST_SHARED_INCLUDE_INDEX
)
if(EMKE_NATIVE_AUDIO_INTEGRATION_HOST_SHARED_INCLUDE_INDEX EQUAL -1)
  message(FATAL_ERROR
    "EMKE.NativeAudio.IntegrationHost must include the Windows shared contract directory."
  )
endif()
target_compile_features(EMKE.NativeAudio.IntegrationHost PUBLIC cxx_std_20)
if(WIN32)
  # IntegrationHost now owns real MMDevice/C ABI discovery objects, so it must
  # link the same Windows system libraries as the production DLL.
  target_link_libraries(EMKE.NativeAudio.IntegrationHost PRIVATE
    ole32
    propsys
    winmm
  )
  get_target_property(
    EMKE_NATIVE_AUDIO_INTEGRATION_HOST_LINK_LIBRARIES
    EMKE.NativeAudio.IntegrationHost
    LINK_LIBRARIES
  )
  foreach(EMKE_NATIVE_AUDIO_REQUIRED_LIBRARY IN ITEMS ole32 propsys winmm)
    list(FIND
      EMKE_NATIVE_AUDIO_INTEGRATION_HOST_LINK_LIBRARIES
      ${EMKE_NATIVE_AUDIO_REQUIRED_LIBRARY}
      EMKE_NATIVE_AUDIO_REQUIRED_LIBRARY_INDEX
    )
    if(EMKE_NATIVE_AUDIO_REQUIRED_LIBRARY_INDEX EQUAL -1)
      message(FATAL_ERROR
        "EMKE.NativeAudio.IntegrationHost must link ${EMKE_NATIVE_AUDIO_REQUIRED_LIBRARY}."
      )
    endif()
  endforeach()
endif()
set_target_properties(EMKE.NativeAudio.IntegrationHost PROPERTIES
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES
  ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY}"
  LIBRARY_OUTPUT_DIRECTORY_RELEASE "${EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY}"
  RUNTIME_OUTPUT_DIRECTORY_RELEASE "${EMKE_NATIVE_AUDIO_TEST_RUNTIME_DIRECTORY}"
)

add_executable(EMKE.NativeAudio.Tests
  ../EMKE.NativeAudio/src/device_catalog.cpp
  ../EMKE.NativeAudio/src/endpoint_snapshot.cpp
  ../EMKE.NativeAudio/src/device_notifications.cpp
  ../EMKE.NativeAudio/src/fake_audio_backend.cpp
  ../EMKE.NativeAudio/src/pcm_converter.cpp
  ../EMKE.NativeAudio/src/audio_worker.cpp
  ../EMKE.NativeAudio/src/wasapi_stream.cpp
  src/abi_tests.cpp
  src/audio_worker_tests.cpp
  src/device_catalog_tests.cpp
  src/endpoint_snapshot_tests.cpp
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

add_executable(EMKE.DeviceCatalog.HeaderSelfContainment.Tests
  src/device_catalog_header_self_containment_tests.cpp
)
target_include_directories(EMKE.DeviceCatalog.HeaderSelfContainment.Tests PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/../EMKE.NativeAudio/src"
  "${CMAKE_CURRENT_SOURCE_DIR}/../../shared"
)
target_compile_features(EMKE.DeviceCatalog.HeaderSelfContainment.Tests PRIVATE cxx_std_20)
add_test(
  NAME EMKE.DeviceCatalog.HeaderSelfContainment
  COMMAND EMKE.DeviceCatalog.HeaderSelfContainment.Tests
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

add_executable(EMKE.DriverBridgeRouting.Tests
  ../../driver/EMKE.VirtualAudio/src/emke_audio_bridge.cpp
  ../../driver/EMKE.VirtualAudio/src/emke_bridge_routing.cpp
  ../../driver/tests/bridge-routing-tests.cpp
)
target_include_directories(EMKE.DriverBridgeRouting.Tests PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/../../shared"
  "${CMAKE_CURRENT_SOURCE_DIR}/../../driver/EMKE.VirtualAudio/src"
)
target_compile_features(EMKE.DriverBridgeRouting.Tests PRIVATE cxx_std_17)
add_test(
  NAME EMKE.DriverBridge.Routing
  COMMAND EMKE.DriverBridgeRouting.Tests
)

add_executable(EMKE.DriverWaveBufferContract.Tests
  ../../driver/tests/wave-buffer-contract-tests.cpp
)
target_include_directories(EMKE.DriverWaveBufferContract.Tests PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/../../shared"
  "${CMAKE_CURRENT_SOURCE_DIR}/../../driver/EMKE.VirtualAudio/src"
)
target_compile_features(
  EMKE.DriverWaveBufferContract.Tests PRIVATE cxx_std_17
)
add_test(
  NAME EMKE.DriverWaveBuffer.Contract
  COMMAND EMKE.DriverWaveBufferContract.Tests
)

add_executable(EMKE.VirtualFormatContract.Tests
  src/virtual_format_contract_tests.cpp
)
target_include_directories(EMKE.VirtualFormatContract.Tests PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/../EMKE.NativeAudio/src"
  "${CMAKE_CURRENT_SOURCE_DIR}/../../shared"
)
target_compile_features(EMKE.VirtualFormatContract.Tests PRIVATE cxx_std_20)
add_test(
  NAME EMKE.NativeAudio.VirtualFormatContract
  COMMAND EMKE.VirtualFormatContract.Tests
)

# Compile the production bridge through a kernel-shaped include boundary that
# rejects user-mode C++ standard headers. The WDK driver build remains the
# authoritative /kernel proof; this target catches the same include regression
# earlier in the native build.
add_library(EMKE.DriverBridge.KernelBoundary OBJECT
  ../../driver/EMKE.VirtualAudio/src/emke_audio_bridge.cpp
  ../../driver/EMKE.VirtualAudio/src/emke_bridge_routing.cpp
  ../../driver/tests/kernel-compile-shim-types.cpp
)
target_include_directories(EMKE.DriverBridge.KernelBoundary PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/../../driver/tests/kernel-compile-shim"
  "${CMAKE_CURRENT_SOURCE_DIR}/../../shared"
  "${CMAKE_CURRENT_SOURCE_DIR}/../../driver/EMKE.VirtualAudio/src"
)
target_compile_definitions(EMKE.DriverBridge.KernelBoundary PRIVATE
  _WIN32
  _KERNEL_MODE
)
target_compile_features(EMKE.DriverBridge.KernelBoundary PRIVATE cxx_std_17)
if(MSVC)
  target_compile_options(EMKE.DriverBridge.KernelBoundary PRIVATE /W4 /WX)
else()
  target_compile_options(
    EMKE.DriverBridge.KernelBoundary PRIVATE -Wall -Wextra -Werror
  )
endif()
