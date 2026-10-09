# Build: sudo apt install build-essential pkg-config libgtk-3-dev libusb-1.0-0-dev && make
VERSION := $(shell cat ../VERSION)
DONATE  := $(shell cat ../DONATE_URL)
PKGS    := gtk+-3.0 libusb-1.0
CFLAGS  ?= -O2
CFLAGS  += -std=gnu11 -Wall -Wextra -Wno-unused-parameter -pthread \
           $(shell pkg-config --cflags $(PKGS)) \
           -DAPP_VERSION='"$(VERSION)"' -DDONATE_URL='"$(DONATE)"'
LDLIBS  += $(shell pkg-config --libs $(PKGS)) -lm -pthread

SRC := src/main.c src/ptp.c src/camera.c src/image.c src/engine.c src/vcam.c src/selftest.c
BIN := dslr-webcam-studio

$(BIN): $(SRC) $(wildcard src/*.h)
	$(CC) $(CFLAGS) -o $@ $(SRC) $(LDLIBS)

test: $(BIN)
	./$(BIN) --selftest

install: $(BIN)
	install -Dm755 $(BIN) $(DESTDIR)/usr/bin/$(BIN)
	install -Dm644 dslr-webcam-studio.desktop $(DESTDIR)/usr/share/applications/dslr-webcam-studio.desktop
	install -Dm644 ../assets/icon_256.png $(DESTDIR)/usr/share/icons/hicolor/256x256/apps/dslr-webcam-studio.png
	install -Dm644 60-dslr-webcam-studio.rules $(DESTDIR)/usr/lib/udev/rules.d/60-dslr-webcam-studio.rules

clean:
	rm -f $(BIN)

.PHONY: test install clean
