#include <algorithm>
#include <cstring>
#include <span>

struct RegisteredTest {
  const char* group;
  const char* name;
  int (*function)();
};

std::span<const RegisteredTest> registered_tests();

int main(int argc, char** argv) {
  int failed_assertions = 0;
  const char* filter = argc == 1 ? nullptr : argv[1];

  for (const RegisteredTest& test : registered_tests()) {
    if (filter == nullptr || std::strcmp(filter, test.group) == 0) {
      failed_assertions += test.function();
    }
  }

  return std::min(failed_assertions, 255);
}
