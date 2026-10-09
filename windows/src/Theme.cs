// Window layout, colors and timings for the Windows app.
using System.Drawing;

namespace DslrWebcamStudio
{
    static class Theme
    {
        public const string FontName = "Segoe UI";
        public const string MonoFontName = "Consolas";
        public const float FontSize = 9f;

        public static readonly Size WindowSize = new Size(1000, 640);
        public static readonly Size WindowMinSize = new Size(760, 480);
        public static readonly Size DiagnosticsSize = new Size(760, 520);
        public const int ButtonHeight = 28;

        public static readonly Color Background = Color.FromArgb(32, 32, 36);
        public static readonly Color Bar = Color.FromArgb(40, 40, 46);
        public static readonly Color Text = Color.Gainsboro;
        public static readonly Color Muted = Color.Gray;
        public static readonly Color Input = Color.FromArgb(52, 52, 58);
        public static readonly Color Button = Color.FromArgb(58, 58, 66);
        public static readonly Color ButtonBorder = Color.FromArgb(80, 80, 90);
        public static readonly Color StartButton = Color.FromArgb(0, 102, 204);
        public static readonly Color StopButton = Color.FromArgb(170, 50, 50);
        public static readonly Color CoffeeButton = Color.FromArgb(255, 196, 57);
        public static readonly Color CoffeeText = Color.FromArgb(40, 30, 10);
        public static readonly Color PreviewBorder = Color.FromArgb(70, 70, 80);
    }

    static class Timing
    {
        public const int StatusRefreshMs = 250;          // status bar refresh
        public const int CameraRetryMs = 2000;           // reconnect delay after a camera error
        public const int CameraPollIdleMs = 5;           // wait when the camera has no new frame
        public const int ExposureRefreshMs = 500;        // re-read ISO/shutter/aperture
        public const int EventPollMs = 500;              // Canon GetEvent poll
        public const int ThreadJoinMs = 4000;            // wait for worker threads on stop
        public const int VcamReconnectMs = 1000;         // look for a camera consumer
        public const double TestPatternFps = 29.97;
    }
}
