add_library(EMKE.NativeAudio SHARED
  src/audio_runtime.cpp
  src/audio_worker.cpp
  src/device_catalog.cpp
  src/endpoint_snapshot.cpp
  src/device_notifications.cpp
  src/fake_audio_backend.cpp
  src/native_audio_api.cpp
  src/pcm_converter.cpp
  src/wasapi_stream.cpp
)

target_compile_definitions(EMKE.NativeAudio PRIVATE EMKE_NATIVE_AUDIO_EXPORTS)
target_include_directories(EMKE.NativeAudio
  PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
  PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/../include"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../shared"
)
target_compile_features(EMKE.NativeAudio PUBLIC cxx_std_20)
if(WIN32)
  target_link_libraries(EMKE.NativeAudio PRIVATE ole32 propsys winmm)
endif()
set_target_properties(EMKE.NativeAudio PROPERTIES
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES
)
