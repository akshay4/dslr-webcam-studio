// Every user-facing text in the Windows app, in one place (ready for translation).
namespace DslrWebcamStudio
{
    static class Strings
    {
        public const string AppName = "DSLR Webcam Studio";

        // Toolbar
        public const string Start = "Start";
        public const string Stop = "Stop";
        public const string Snapshot = "Snapshot";
        public const string Diagnostics = "Diagnostics";
        public const string InstallVirtualCamera = "Install virtual camera";
        public const string BuyCoffee = "☕ Buy me a coffee";
        public const string SourceCanon = "Canon EOS (USB)";
        public const string SourceTestPattern = "Test pattern";
        public const string Aspect = "Aspect";
        public const string AspectFit = "Fit";
        public const string AspectFill = "Fill 16:9";
        public const string AspectFitHint = "Shows the whole picture with black side bars.";
        public const string AspectFillHint = "Crops the camera's 3:2 picture to fill the frame.";
        public const string Mirror = "Mirror left/right";
        public const string Flip = "Flip upside down";
        public const string NoiseReduction = "Noise reduction";
        public const string Sharpness = "Sharpness";
        public const string CameraIso = "ISO";
        public const string Shutter = "Shutter";
        public static readonly string[] Levels = { "Off", "Low", "Medium", "High" };
        public const string Resolution = "Resolution";
        public const string Framerate = "Frame rate";

        // Inspector
        public const string SectionCamera = "Camera";
        public const string SectionOutput = "Output";
        public const string SectionImage = "Image";
        public const string SectionVirtualCamera = "Virtual Camera";
        public const string TileMode = "Mode", TileIso = "ISO", TileShutter = "Shutter", TileAperture = "Aperture";
        public const string ModeDialHintLong = "Turn the mode dial to M to set ISO and shutter here.";
        public const string CanonRunningTitle = "Canon software is running";
        public const string CanonRunningCard = "{0} can hold the camera. Quit it if the picture doesn't start.";
        public const string CanonRunningDetail = "Canon software is running and may be holding the camera ({0}). Quit it if the picture doesn't start.";
        public const string VcamDescription = "Zoom, Teams, Meet, OBS and other video apps see this app's picture as the camera \"DSLR Webcam Studio\".";

        // Preview
        public const string PhaseLive = "Live", PhaseWaiting = "Waiting", PhaseConnecting = "Connecting", PhaseRetrying = "Retrying";
        public const string CameraStopped = "Camera stopped";
        public const string PressStart = "Press Start (Ctrl+R) to connect to your camera.";
        public const string StatsPill = "Output {0:F1} fps    Camera {1:F1} fps    Repeated {2}    Dropped {3}";
        public const string StatsVcam = "    ● Virtual camera";

        // Status
        public const string Stopped = "Stopped";
        public const string Live = "Live";
        public const string Connecting = "Connecting to camera...";
        public const string CameraErrorRetrying = "Camera: {0} Retrying...";
        public const string CameraRefused = "Camera refused the change ({0}). Set the mode dial to M.";
        public const string ExposureFormat = "{0}  |  ISO {1}  |  {2}  |  {3}";
        public const string SettingsSaveFailed = "Could not save settings: {0}";
        public const string SnapshotSaved = "Saved {0}";
        public const string SnapshotFilePrefix = "DSLRWebcam_";
        public const string TestPatternModel = "Test pattern 960x640";
        public const string TestPatternFrame = "frame {0}";

        // Virtual camera
        public const string VcamPrefix = "Virtual camera: ";
        public const string VcamNeedsWindows11 = "needs Windows 11";
        public const string VcamNotInstalled = "not installed";
        public const string VcamReady = "ready";
        public const string VcamInUse = "ON (in use)";
        public const string VcamInstalling = "Installing the virtual camera (approve the Windows prompt)...";
        public const string VcamInstallFailed = "Virtual camera was not installed.";
        public const string VcamInstalled = "Virtual camera installed. Choose \"DSLR Webcam Studio\" as the camera in OBS, Streamlabs, Zoom or Teams.";
        public const string VcamLoadFailed = "could not load the camera component";
        public const string VcamRefused = "Windows refused the virtual camera (0x{0:X8})";

        // Diagnostics
        public const string DiagRunning = "Running diagnostics (about 10 seconds)...";
        public const string DiagSaved = "Diagnostics saved to {0}";
        public const string DiagWindowTitle = "Diagnostics - {0}";
        public const string CopyToClipboard = "Copy to clipboard";
        public const string Copied = "Copied";

        // Camera errors
        public const string NoCanonCamera = "No Canon camera found. Connect your Canon EOS camera over USB and switch it on.";
        public const string NoLiveViewOp = "Camera does not advertise PTP op 0x{0:X4}; live view over USB isn't available.";
        public const string PtpFailed = "{0} failed: {1}";
        public const string TransferTooLarge = "Camera sent an unexpectedly large transfer ({0} bytes).";
        public const string MalformedData = "Camera sent malformed data.";
        public const string WaitingForLiveView = "Waiting for live view from the camera...";
        public const string NoLiveViewPicture = "No live view picture from the camera. Enable Live View shooting in the camera menu and close the camera's own Live View.";
        public const string CanonSoftwareHint = " Canon software is running and may be using the camera ({0}): quit it.";
    }
}
