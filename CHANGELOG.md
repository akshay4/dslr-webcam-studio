# Changelog

All notable changes to DSLR Webcam Studio. Versions follow [Semantic Versioning](https://semver.org/);
the current version is in [`VERSION`](VERSION).

## [1.1.0] - 2026-10-10

**First macOS preview** (macOS 12+, Apple silicon and Intel). It hasn't been tested with a camera yet, so please
try it and send a Diagnostics report either way. Windows is unchanged from 1.0.0.

### macOS (preview)
- Live preview from Canon EOS cameras over USB, with the same output, image and exposure controls as Windows.
- **Virtual webcam** code for Teams, Google Meet, Zoom, OBS and FaceTime: a CoreMediaIO Camera Extension bundled in the
  app, installed with **Install virtual camera** (needs a Developer ID-signed build; see the README).
- Fixed: Stop and Quit froze the window for 4 seconds and left the camera in PC live-view mode.
- Fixed: reconnecting after unplugging the camera took about 30 seconds.
- Fixed: File > New Window started a second camera session; Start was available while Diagnostics used the camera.
- Fixed: the ISO and shutter pickers went blank when the camera was set to a value outside the presets.

## [1.0.0] - 2026-10-09

First public release, for Windows 10/11 (64-bit), as a single portable exe.

- **Virtual webcam "DSLR Webcam Studio"** for OBS, Streamlabs, Zoom, Teams, browsers etc.: Windows 11 (own Media
  Foundation media source registered with Windows' virtual-camera API, one-time install).
- Live preview from Canon EOS cameras over USB. No Canon software or DLLs: the apps speak Canon's
  PTP live-view protocol directly.
- **Streaming Video Output Resolution**: 640x360, 1280x720, 1920x1080.
- **Target Streaming Framerate**: 15, 24, 25, 30 fps, held exactly by a fixed-timeline pacer
  (frames are repeated or dropped to match the camera's real rate).
- Aspect handling: Fit (black side bars) or Fill (crop 3:2 live view to 16:9).
- Mirror left/right and flip upside-down.
- Noise reduction (motion-adaptive temporal filter) and sharpness (unsharp mask), Off/Low/Medium/High.
- Camera ISO and shutter speed control (mode dial on M, or Tv/Av/P for ISO), plus a live readout of
  mode, ISO, shutter and aperture.
- Snapshot to PNG, Diagnostics report, automatic reconnect when the camera is unplugged or sleeps.
- Settings saved to `config.ini`, shared format across platforms.

Tested end to end on a Canon EOS 700D. Other Canon EOS models should work. Please send a Diagnostics
report either way. macOS and Linux versions are in progress and will be released later.
