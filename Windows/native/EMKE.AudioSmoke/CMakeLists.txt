add_executable(EMKE.AudioSmoke
  src/main.cpp
)
target_link_libraries(EMKE.AudioSmoke PRIVATE EMKE.NativeAudio)
target_compile_features(EMKE.AudioSmoke PRIVATE cxx_std_20)
