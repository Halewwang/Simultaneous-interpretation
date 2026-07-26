add_library(EMKE.NativeAudio SHARED
  src/audio_runtime.cpp
  src/fake_audio_backend.cpp
  src/native_audio_api.cpp
)

target_compile_definitions(EMKE.NativeAudio PRIVATE EMKE_NATIVE_AUDIO_EXPORTS)
target_include_directories(EMKE.NativeAudio
  PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
  PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src"
)
target_compile_features(EMKE.NativeAudio PUBLIC cxx_std_20)
set_target_properties(EMKE.NativeAudio PROPERTIES
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES
)
