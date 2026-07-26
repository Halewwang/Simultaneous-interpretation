add_executable(EMKE.NativeAudio.Tests
  src/test_main.cpp
)

target_link_libraries(EMKE.NativeAudio.Tests PRIVATE EMKE.NativeAudio)

add_test(NAME EMKE.NativeAudio.Tests COMMAND EMKE.NativeAudio.Tests)
