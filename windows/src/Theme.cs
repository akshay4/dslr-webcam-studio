// Window layout, colors and timings for the Windows app.
using System.Drawing;

namespace DslrWebcamStudio
{
    static class Theme
    {
        public const string FontName = "Segoe UI";
        public const string SemiboldFontName = "Segoe UI Semibold";
        public const string MonoFontName = "Consolas";
        public const string IconFontName = "Segoe MDL2 Assets"; // Windows 10 and 11
        public const float FontSize = 9f;

        // Sizes in 96-DPI pixels; MainForm scales them for the screen.
        public static readonly Size WindowSize = new Size(1180, 720);
        public static readonly Size WindowMinSize = new Size(900, 560);
        public static readonly Size DiagnosticsSize = new Size(760, 520);
        public const int ButtonHeight = 32;
        public const int ToolbarHeight = 52;
        public const int InspectorWidth = 316;
        public const int CardWidth = 272;          // inspector card, scrollbar included in the margin
        public const int Gap = 12;
        public const int Radius = 8;

        // Dark palette in the spirit of Windows 11.
        public static readonly Color Background = Color.FromArgb(32, 32, 32);
        public static readonly Color Bar = Color.FromArgb(39, 39, 39);
        public static readonly Color Surface = Color.FromArgb(36, 36, 36);
        public static readonly Color Card = Color.FromArgb(45, 45, 45);
        public static readonly Color CardBorder = Color.FromArgb(58, 58, 58);
        public static readonly Color PreviewBackground = Color.FromArgb(20, 20, 20);
        public static readonly Color Text = Color.FromArgb(242, 242, 242);
        public static readonly Color Muted = Color.FromArgb(160, 160, 160);
        public static readonly Color Input = Color.FromArgb(56, 56, 56);
        public static readonly Color InputHover = Color.FromArgb(66, 66, 66);
        public static readonly Color Selected = Color.FromArgb(88, 88, 88);
        public static readonly Color Button = Color.FromArgb(56, 56, 56);
        public static readonly Color ButtonBorder = Color.FromArgb(80, 80, 80);
        public static readonly Color Accent = Color.FromArgb(0, 120, 212);
        public static readonly Color StopButton = Color.FromArgb(196, 43, 28);
        public static readonly Color CoffeeButton = Color.FromArgb(255, 196, 57);
        public static readonly Color CoffeeText = Color.FromArgb(40, 30, 10);
        public static readonly Color Live = Color.FromArgb(232, 72, 72);
        public static readonly Color Warning = Color.FromArgb(247, 160, 40);
        public static readonly Color Good = Color.FromArgb(80, 200, 120);
        public static readonly Color Overlay = Color.FromArgb(170, 16, 16, 16);
        public static readonly Color WarningCard = Color.FromArgb(62, 48, 30);

        // Segoe MDL2 Assets glyphs.
        public const string GlyphPlay = "\uE768", GlyphStop = "\uE71A", GlyphCamera = "\uE722", GlyphDiagnostic = "\uE9D9";
        public const string GlyphMonitor = "\uE7F4", GlyphImage = "\uE790", GlyphVideo = "\uE714", GlyphWarning = "\uE7BA";
        public const string GlyphInfo = "\uE946", GlyphVideoOff = "\uE8AA";
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
