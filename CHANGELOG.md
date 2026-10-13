# Changelog

All notable changes to DSLR Webcam Studio. Versions follow [Semantic Versioning](https://semver.org/);
the current version is in [`VERSION`](VERSION).

## [2.0.0] - 2026-10-13

A redesigned app on Windows and macOS, and camera handling reworked on all platforms to match what Canon's own
EOS Webcam Utility does. **Tested end to end on macOS with a Canon EOS 700D** (live view at ~17 fps, 30 fps output).

### New window (Windows and macOS)
- The live preview fills the window, with a LIVE / Connecting badge and the camera model, the output format and a
  compact statistics bar drawn over the picture, and short notices (snapshot saved, virtual camera installed...)
  instead of a crowded status bar.
- Settings moved to an inspector on the right, in cards: **Camera** (source, mode / ISO / shutter / aperture readout,
  ISO and shutter pickers), **Output** (resolution, frame rate, fit or fill), **Image** (mirror, flip, noise
  reduction, sharpness) and **Virtual Camera** (status and install).
- Segmented buttons instead of drop-down lists, switches for mirror and flip, and a Start button in the middle of
  the preview when the camera is stopped.
- Keyboard: Ctrl/⌘+R start or stop, Ctrl/⌘+S snapshot (also F5 on Windows); macOS has a **Camera** menu.
- macOS: native SwiftUI layout with SF Symbols and materials. Windows: dark title bar on Windows 10/11, custom
  rounded controls, scaled for high-DPI screens.

### Camera handling
Camera session handling reworked on **Windows, macOS and Linux** to match what Canon's own EOS Webcam Utility does
(its EDSDK camera layer was studied for this; the apps still use no Canon code or DLLs).

#### All platforms
- Live view restarts by itself when the camera turns it off (Live View button, mode dial, menu): the app
  watches the camera's live view output and turns it back on, and reconnects if no picture arrives for 12 seconds.
- The camera no longer powers off mid-call: the app answers the camera's "will soon shut down" warning with
  KeepDeviceOn (0x911D) and also sends it every minute.
- Connecting is more reliable: remote mode, event mode and live view output are retried while the camera
  reports "busy" (up to 10 times), the same as Canon's SDK.
- Cameras that reject the usual GetViewFinderData parameter are retried with the parameters Canon's SDK uses.
- Windows and macOS: camera errors now say when Canon software (EOS Utility, EOS Webcam Utility and its services)
  is running and may be holding the camera.

#### macOS
- New **Camera > Pause Canon webcam service** menu: EOS Webcam Utility's background service opens every Canon
  camera it sees and fights over live view. The app can pause it and restores it when you quit (also
  `--pause-canon-service` / `--resume-canon-service`).
- ImageCaptureCore no longer reads thumbnails and metadata of every photo on the card while streaming, and the
  app waits for the camera to report ready before sending commands. Devices that macOS doesn't allow to be
  controlled (e.g. in mass-storage mode) get a clear message.
- Virtual camera: each frame is scaled once, not on every output tick, when a video app picks another size.

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
