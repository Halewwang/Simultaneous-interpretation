ROOT_DIR := ..
BUILD_DIR := ../.build/driver
BUNDLE := $(BUILD_DIR)/EMKEAudioDriver.driver
CONTENTS := $(BUNDLE)/Contents
EXECUTABLE := $(CONTENTS)/MacOS/EMKEAudioDriver
SDK := $(shell xcrun --show-sdk-path)

DRIVER_SOURCE := $(ROOT_DIR)/Driver/EMKEAudioDriver/EMKEAudioDriver.c
BRIDGE_SOURCES := \
	$(ROOT_DIR)/Sources/EMKEAudioBridge/EMKEAudioRingBuffer.c \
	$(ROOT_DIR)/Sources/EMKEAudioBridge/EMKEAudioRoutes.c \
	$(ROOT_DIR)/Sources/EMKEAudioBridge/EMKEAudioDriverObjects.c
PUBLIC_HEADERS := $(wildcard $(ROOT_DIR)/Sources/EMKEAudioBridge/include/*.h)

CFLAGS := \
	-std=c11 \
	-arch arm64 \
	-mmacosx-version-min=14.0 \
	-isysroot $(SDK) \
	-fPIC \
	-O2 \
	-Wall \
	-Wextra \
	-Werror \
	-I$(ROOT_DIR)/Sources/EMKEAudioBridge/include

.PHONY: all clean verify

all: $(EXECUTABLE)

$(EXECUTABLE): $(DRIVER_SOURCE) $(BRIDGE_SOURCES) $(PUBLIC_HEADERS) $(ROOT_DIR)/Driver/EMKEAudioDriver/Info.plist
	mkdir -p $(CONTENTS)/MacOS $(CONTENTS)/Resources
	xcrun clang $(CFLAGS) -bundle \
		$(DRIVER_SOURCE) $(BRIDGE_SOURCES) \
		-framework CoreAudio -framework CoreFoundation \
		-o $(EXECUTABLE)
	cp $(ROOT_DIR)/Driver/EMKEAudioDriver/Info.plist $(CONTENTS)/Info.plist
	cp $(ROOT_DIR)/Driver/EMKEAudioDriver/LICENSE-Apple-Sample.txt $(CONTENTS)/Resources/LICENSE-Apple-Sample.txt

verify: all
	bash $(ROOT_DIR)/Driver/verify-bundle.sh $(BUNDLE)

clean:
	rm -rf $(BUILD_DIR)
