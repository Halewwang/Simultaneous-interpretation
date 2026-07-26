add_executable(EMKE.NativeAudio.Tests
  ../EMKE.NativeAudio/src/fake_audio_backend.cpp
  src/abi_tests.cpp
  src/test_main.cpp
)

target_link_libraries(EMKE.NativeAudio.Tests PRIVATE EMKE.NativeAudio)
target_include_directories(EMKE.NativeAudio.Tests PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/../EMKE.NativeAudio/src"
)

add_test(NAME EMKE.NativeAudio.Abi COMMAND EMKE.NativeAudio.Tests Abi)
add_test(NAME EMKE.NativeAudio.FakeBackend COMMAND EMKE.NativeAudio.Tests FakeBackend)
