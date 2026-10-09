# Changelog

All notable changes to DSLR Webcam Studio. Versions follow [Semantic Versioning](https://semver.org/);
the current version is in [`VERSION`](VERSION).

## [1.0.0] - 2026-10-09

First public release for Windows, macOS and Linux.

- **Virtual webcam "DSLR Webcam Studio"** for OBS, Streamlabs, Zoom, Teams, browsers etc.: Windows 11 (own Media
  Foundation media source registered with Windows' virtual-camera API, one-time install) and Linux (v4l2loopback).
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

Platform status: the Windows build is tested end to end on a Canon EOS 700D. The macOS and Linux
builds pass their self-tests in CI; camera testing on those platforms is in progress (please send a
Diagnostics report if your camera works or doesn't).
