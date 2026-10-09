<p align="center"><img src="assets/icon_128.png" width="96" alt=""></p>

<h1 align="center">DSLR Webcam Studio</h1>

<p align="center">
  Use your Canon EOS camera as a webcam: free, open source, no Canon software needed.<br>
  Windows · macOS · Linux
</p>

<p align="center">
  <a href="https://github.com/akshay4/dslr-webcam-studio/releases/latest"><img src="https://img.shields.io/github/v/release/akshay4/dslr-webcam-studio?label=download&style=for-the-badge" alt="Download"></a>
  <a href="https://paypal.me/shekhawatakshay"><img src="https://img.shields.io/badge/Buy%20me%20a%20coffee-PayPal-ffc439?style=for-the-badge&logo=paypal&logoColor=003087" alt="Buy me a coffee with PayPal"></a>
</p>

<p align="center"><img src="docs/screenshot-windows.png" width="760" alt="DSLR Webcam Studio on Windows"></p>

## Features

- **Live preview** from a Canon EOS camera over USB. The app talks to the camera directly; no Canon
  drivers, DLLs or subscriptions.
- **Streaming Video Output Resolution**: 640×360, 1280×720 or 1920×1080.
- **Target Streaming Framerate**: 15, 24, 25 or 30 fps, held exactly (see [How it works](#how-it-works)).
- **Fit or Fill**: show the camera's 3:2 picture with side bars, or crop it to 16:9.
- **Mirror** left/right and **flip** upside-down.
- **Noise reduction** and **sharpness**: four levels each, cleans up grainy low-light video.
- **Camera ISO and shutter** control, plus a live readout of mode, ISO, shutter and aperture.
- **Snapshot** to PNG, **Diagnostics** report, automatic reconnect.

## Download

Get the file for your system from the [latest release](https://github.com/akshay4/dslr-webcam-studio/releases/latest):

| System | File | First launch |
|---|---|---|
| Windows 10/11 (64-bit) | `DSLR_Webcam_Studio-…-Windows-x64.exe` | Just run it (single ~90 KB file, nothing to install). If SmartScreen appears: **More info → Run anyway**. |
| macOS 12+ (Apple silicon & Intel) | `DSLR_Webcam_Studio-…-macOS.zip` | Unzip, move to Applications, then **right-click → Open** the first time (the app is not notarized). |
| Linux x86-64 | `DSLR_Webcam_Studio-…-x86_64.AppImage` | `chmod +x` it and run. For USB access without root, install [the udev rule](linux/60-dslr-webcam-studio.rules) once. |

## Quick start

1. Connect the camera with its USB cable and switch it on.
2. Set the mode dial to **M** (or P/Tv/Av, or movie mode) and make sure **Live View** is enabled in the camera menu.
3. Start DSLR Webcam Studio. Live view starts automatically.
4. Pick the resolution, framerate and image options. Settings are saved for next time.

**For a clean picture:** in a dim room the camera's Auto ISO climbs high and the image gets grainy. Set the dial to
**M**, choose **ISO 400–800** and **shutter 1/30–1/60** in the app, open the aperture as wide as your lens allows,
and keep **Noise reduction** on Medium. More light on your face helps most of all.

**If it doesn't connect:** close Canon EOS Utility / EOS Webcam Utility (they hold the camera). On Linux, unmount
the camera in your file manager if the desktop auto-mounted it. Then press **Diagnostics** and include the report
when you [open an issue](https://github.com/akshay4/dslr-webcam-studio/issues).

## Supported cameras

Canon EOS DSLR and mirrorless bodies that support live view over USB (roughly 2009 onward) use the same protocol
and should work. Tested so far:

| Camera | Windows | macOS | Linux |
|---|---|---|---|
| EOS 700D / Rebel T5i / Kiss X7i | ✅ 960×640 live view, ~17 fps | ⏳ | ⏳ |

Tested your camera? Please open an issue with your Diagnostics report and this table will be updated.

## How it works

```
Camera ──USB/PTP──▶ Live-view JPEG ──▶ Noise reduction ──▶ Sharpen ──▶ Bicubic scale + flip ──▶ Fixed-rate output ──▶ Preview
           (thread 1: as fast as the camera delivers)          (thread 2: once per output tick, on a fixed timeline)
```

**Talking to the camera.** Canon cameras speak PTP (Picture Transfer Protocol) with Canon-specific extensions. Each
app sends the same sequence: `SetRemoteMode (0x9114)` → `SetEventMode (0x9115)` → set property `EVF output device
(0xD1B0)` to *PC* with `SetDevicePropValueEx (0x9110)` → poll `GetViewFinderData (0x9153)`, which returns a JPEG
frame (or *not ready*). `GetEvent (0x9116)` is polled every 0.5 s; besides keeping the camera responsive, it reports
property changes, which is how the app knows the current mode, ISO, shutter and aperture. These are the same public
opcodes used by [libgphoto2](https://github.com/gphoto/libgphoto2). Each platform uses its native USB path:

| Platform | Language / UI | Camera transport |
|---|---|---|
| Windows | C# (.NET Framework 4.8, built into Windows) + WinForms | Windows Portable Devices API, raw PTP via the MTP-extension commands of the built-in `WUDFWpdMtp` driver |
| macOS | Swift + SwiftUI | Apple ImageCaptureCore `requestSendPTPCommand` |
| Linux | C + GTK 3 | libusb, PTP bulk transfers (USB still-image class) |

**Holding the framerate.** The camera delivers frames at its own pace (about 17 fps on a 700D in photo live view) and
that pace drifts. Like a real webcam, the output runs on a fixed timeline at exactly the target rate. Each tick sends
the newest processed frame: if the camera is faster, frames are dropped; if slower, the last one is repeated. If a
tick is ever late, the pacer skips to the next slot instead of sending a burst. The status bar shows the real camera
rate and how many frames were repeated or dropped.

**Sending on time.** Each tick first sends the frame prepared during the previous slot, then processes the newest
camera frame for the next one. Processing time therefore never shifts send times (cost: one frame of latency).

**Cleaning up the picture.**
- *Noise reduction* is a motion-adaptive temporal filter. Each pixel blends with its running average, but the blend
  is decided from the brightness change averaged over a 3×3 neighbourhood: random sensor noise cancels out in that
  average while real movement doesn't. Static areas (walls, background) get averaged over several frames; moving
  areas use the new frame directly, so there's no ghosting. On a 700D at high ISO it cuts frame-to-frame noise by
  42 / 54 / 62 % (Low / Medium / High).
- *Sharpness* is a 3×3 unsharp mask with a small dead zone so leftover noise isn't amplified.
- *Scaling* is separable Catmull-Rom bicubic (crisper than bilinear when upscaling the 960×640 live view), with the
  kernel widened when shrinking to avoid aliasing. Mirroring and flipping are done inside the scaler for free.
  All of this runs across CPU cores: about 4–6 ms per frame at 1080p.

**Settings** live in `config.ini` (`[Global] StreamWidth / StreamHeight / StreamFps / FitMode / FlipHorizontal /
FlipVertical / NoiseReduction / Sharpness`), in `%APPDATA%\DSLR Webcam Studio\` on Windows,
`~/Library/Application Support/DSLR Webcam Studio/` on macOS and `~/.config/dslr-webcam-studio/` on Linux.

## Building from source

| Platform | Command | Needs |
|---|---|---|
| Windows | `powershell -ExecutionPolicy Bypass -File windows\build.ps1` | Nothing extra (uses the C# compiler that ships with Windows) |
| macOS | `bash macos/build.sh` | Xcode command line tools |
| Linux | `cd linux && make` (or `bash package-appimage.sh`) | `build-essential pkg-config libgtk-3-dev libusb-1.0-0-dev` |

Every app has a built-in test mode (`--selftest`) that checks settings, geometry, pacing, scaling, flips, noise
reduction and protocol parsing; CI runs it on all three platforms. Windows also supports
`DSLRWebcamStudio.exe --selftest <dir> camera` to test the whole pipeline against a connected camera.

```
VERSION          version shared by all apps (also used for release tags)
DONATE_URL       PayPal link shown by the in-app coffee button
windows/         C# app        macos/   Swift app        linux/   C + GTK app
assets/          icon (SVG + PNG + ICO)    tools/MakeIcons.cs renders them
.github/         CI: builds + self-tests every push; a vX.Y.Z tag publishes a release
```

## Releasing a new version

1. Update `VERSION` and add a section to [`CHANGELOG.md`](CHANGELOG.md).
2. Commit, then tag and push: `git tag v1.0.1 && git push origin main v1.0.1`.
3. GitHub Actions builds all three apps, runs their self-tests and publishes the release with the downloads attached.

## Support the project

DSLR Webcam Studio is free. If it saved you buying a webcam or a subscription, you can
[**buy me a coffee ☕**](https://paypal.me/shekhawatakshay). Thank you!

## License and trademarks

[MIT](LICENSE) © Akshay Singh Shekhawat.

This is an independent project, not affiliated with or endorsed by Canon Inc. Canon and EOS are trademarks of
Canon Inc., used here only to describe compatibility.
