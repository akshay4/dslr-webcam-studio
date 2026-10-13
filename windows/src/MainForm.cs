// Main window: the live preview on the left (with status, format and stats drawn over it), an
// inspector with camera, output, image and virtual camera cards on the right, and Start /
// Snapshot / Diagnostics in the toolbar. Same layout as the macOS app.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DslrWebcamStudio
{
    public sealed class MainForm : Form
    {
        const int SourceCanonIndex = 0, SourceTestIndex = 1;
        const string SnapshotTimeFormat = "yyyyMMdd_HHmmss";
        const int NoticeMs = 6000, CanonCheckMs = 2000;

        enum Phase { Stopped, Connecting, Waiting, Live, Problem }

        readonly string configPath;
        readonly float scale;
        StreamSettings settings;
        LiveSource source;
        OutputEngine engine;
        int invalidatePending;
        bool syncingCamera; // true while the UI mirrors camera values, so they aren't sent back
        string vcamError;
        string notice;
        DateTime noticeUntil;
        List<string> canonRunning = new List<string>();
        DateTime lastCanonCheck = DateTime.MinValue;

        // Toolbar
        readonly PillButton startBtn, snapBtn, diagBtn, coffeeBtn, centerStartBtn;
        // Inspector
        readonly Segmented sourceSeg, resSeg, fpsSeg, fitSeg, noiseSeg, sharpSeg;
        readonly ToggleSwitch mirrorBox, flipBox;
        readonly ComboBox isoBox, shutterBox;
        readonly Label modeValue, isoValue, shutterValue, apertureValue, dialHint, fitHint;
        readonly Label vcamStatus, canonText;
        readonly Panel exposurePanel;
        readonly Card canonCard;
        readonly PillButton vcamBtn;
        readonly Panel inspector;
        readonly PreviewPanel preview = new PreviewPanel();
        readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer { Interval = Timing.StatusRefreshMs };
        readonly VirtualCamera.Writer vcamWriter = new VirtualCamera.Writer();

        public MainForm(string configPath)
        {
            this.configPath = configPath;
            settings = StreamSettings.Load(configPath);

            AutoScaleMode = AutoScaleMode.None;
            using (var g = CreateGraphics()) scale = Math.Max(1f, g.DpiX / 96f);
            Text = Strings.AppName + " " + AppInfo.Version;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }
            ClientSize = S(Theme.WindowSize);
            MinimumSize = S(Theme.WindowMinSize);
            Font = new Font(Theme.FontName, Theme.FontSize);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            KeyPreview = true;

            startBtn = new PillButton(Strings.Start, Theme.GlyphPlay, S(6)) { Fill = Theme.Accent, Size = S(new Size(104, Theme.ButtonHeight)) };
            snapBtn = new PillButton(Strings.Snapshot, Theme.GlyphCamera, S(6)) { Size = S(new Size(118, Theme.ButtonHeight)) };
            diagBtn = new PillButton(Strings.Diagnostics, Theme.GlyphDiagnostic, S(6)) { Size = S(new Size(130, Theme.ButtonHeight)) };
            coffeeBtn = new PillButton(Strings.BuyCoffee, null, S(6))
            { Fill = Theme.CoffeeButton, TextColor = Theme.CoffeeText, Size = S(new Size(150, Theme.ButtonHeight)) };
            centerStartBtn = new PillButton(Strings.Start, Theme.GlyphPlay, S(8)) { Fill = Theme.Accent, Size = S(new Size(130, 38)) };

            sourceSeg = new Segmented(new[] { Strings.SourceCanon, Strings.SourceTestPattern }, new[] { SourceCanonIndex, SourceTestIndex });
            resSeg = new Segmented(Array.ConvertAll(StreamSettings.Resolutions, r => r + "p"), StreamSettings.Resolutions);
            fpsSeg = new Segmented(Array.ConvertAll(StreamSettings.FpsChoices, f => f.ToString()), StreamSettings.FpsChoices);
            fitSeg = new Segmented(new[] { Strings.AspectFit, Strings.AspectFill }, new[] { (int)FitMode.Fit, (int)FitMode.Fill });
            var levels = new[] { 0, 1, 2, 3 };
            noiseSeg = new Segmented(Strings.Levels, levels);
            sharpSeg = new Segmented(Strings.Levels, levels);
            mirrorBox = new ToggleSwitch(Strings.Mirror);
            flipBox = new ToggleSwitch(Strings.Flip);
            isoBox = Combo();
            shutterBox = Combo();
            modeValue = Value(); isoValue = Value(); shutterValue = Value(); apertureValue = Value();
            dialHint = Caption(Strings.ModeDialHintLong);
            fitHint = Caption("");
            vcamStatus = new Label { AutoSize = false, ForeColor = Theme.Text, Margin = Padding.Empty };
            canonText = Caption("");
            vcamBtn = new PillButton(Strings.InstallVirtualCamera, Theme.GlyphVideo, S(6)) { Size = S(new Size(Theme.CardWidth - 24, Theme.ButtonHeight)) };
            exposurePanel = new Panel { Margin = Padding.Empty, BackColor = Theme.Card };
            canonCard = new Card(S(Theme.Radius), S(new Padding(12))) { Fill = Theme.WarningCard, Border = Color.FromArgb(110, 80, 40), Visible = false };
            inspector = new Panel { Dock = DockStyle.Right, Width = S(Theme.InspectorWidth), AutoScroll = true, BackColor = Theme.Surface };

            FillChoices();
            WireEvents();
            BuildInspector();
            preview.Dock = DockStyle.Fill;
            preview.Padding = S(new Padding(Theme.Gap));
            preview.Paint += OnPreviewPaint;
            preview.Resize += delegate { PlaceCenterButton(); };
            preview.Controls.Add(centerStartBtn);
            Controls.Add(preview);
            Controls.Add(new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Theme.CardBorder });
            Controls.Add(inspector);
            Controls.Add(BuildToolbar());

            statusTimer.Tick += delegate { UpdateStatus(); };
            statusTimer.Start();
            Shown += delegate { StartVirtualCamera(); StartStreaming(); };
            UpdateStatus();
        }

        int S(int px) { return (int)Math.Round(px * scale); }
        Size S(Size s) { return new Size(S(s.Width), S(s.Height)); }
        Padding S(Padding p) { return new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom)); }

        // ---- layout --------------------------------------------------------

        void FillChoices()
        {
            sourceSeg.SelectedIndex = SourceCanonIndex;
            resSeg.SelectValue(settings.Resolution);
            fpsSeg.SelectValue(settings.Fps);
            fitSeg.SelectValue((int)settings.Fit);
            noiseSeg.SelectValue(settings.NoiseReduction);
            sharpSeg.SelectValue(settings.Sharpness);
            mirrorBox.Checked = settings.FlipHorizontal;
            flipBox.Checked = settings.FlipVertical;
            foreach (var kv in CameraValues.IsoChoices) isoBox.Items.Add(new Choice(kv.Value, (int)kv.Key));
            foreach (var kv in CameraValues.ShutterChoices) shutterBox.Items.Add(new Choice(kv.Value, (int)kv.Key));
            isoBox.Enabled = shutterBox.Enabled = false;
            coffeeBtn.Visible = AppInfo.DonateConfigured;
        }

        void WireEvents()
        {
            resSeg.SelectedIndexChanged += delegate { ApplySetting(settings.WithResolution(resSeg.Value)); };
            fpsSeg.SelectedIndexChanged += delegate { ApplySetting(settings.WithFps(fpsSeg.Value)); };
            fitSeg.SelectedIndexChanged += delegate { ApplySetting(settings.WithFit((FitMode)fitSeg.Value)); };
            EventHandler enhancement = delegate { ApplySetting(settings.WithEnhancement(noiseSeg.Value, sharpSeg.Value)); };
            noiseSeg.SelectedIndexChanged += enhancement;
            sharpSeg.SelectedIndexChanged += enhancement;
            EventHandler flips = delegate { ApplySetting(settings.WithFlip(mirrorBox.Checked, flipBox.Checked)); };
            mirrorBox.CheckedChanged += flips;
            flipBox.CheckedChanged += flips;
            isoBox.SelectedIndexChanged += delegate { SendCameraSetting(Ptp.DPC_EOS_ISOSpeed, isoBox); };
            shutterBox.SelectedIndexChanged += delegate { SendCameraSetting(Ptp.DPC_EOS_ShutterSpeed, shutterBox); };
            sourceSeg.SelectedIndexChanged += delegate { if (engine != null) { StopStreaming(); StartStreaming(); } };
            startBtn.Click += delegate { ToggleStreaming(); };
            centerStartBtn.Click += delegate { ToggleStreaming(); };
            snapBtn.Click += delegate { SaveSnapshot(); };
            diagBtn.Click += delegate { RunDiagnostics(); };
            vcamBtn.Click += delegate { InstallVirtualCamera(); };
            coffeeBtn.Click += delegate { OpenUrl(AppInfo.DonateUrl); };
        }

        Control BuildToolbar()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = S(Theme.ToolbarHeight), BackColor = Theme.Bar };
            var left = new FlowLayoutPanel
            {
                Dock = DockStyle.Left, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                Padding = S(new Padding(Theme.Gap, 10, 0, 0)), BackColor = Theme.Bar,
            };
            foreach (var b in new Control[] { startBtn, snapBtn, diagBtn }) { b.Margin = S(new Padding(0, 0, 8, 0)); left.Controls.Add(b); }
            var right = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                Padding = S(new Padding(0, 10, Theme.Gap, 0)), BackColor = Theme.Bar,
            };
            coffeeBtn.Margin = Padding.Empty;
            right.Controls.Add(coffeeBtn);
            bar.Controls.Add(left);
            bar.Controls.Add(right);
            bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.CardBorder });
            return bar;
        }

        void BuildInspector()
        {
            int inner = S(Theme.CardWidth - 24); // card width minus its padding
            var stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = S(new Padding(16, 14, 0, 16)), BackColor = Theme.Surface, Location = Point.Empty,
            };

            // Camera
            var camera = NewCard();
            Fit(camera, sourceSeg, inner, S(30));
            exposurePanel.Size = new Size(inner, S(84));
            AddTile(exposurePanel, Strings.TileMode, modeValue, 0, 0);
            AddTile(exposurePanel, Strings.TileIso, isoValue, 1, 0);
            AddTile(exposurePanel, Strings.TileShutter, shutterValue, 0, 1);
            AddTile(exposurePanel, Strings.TileAperture, apertureValue, 1, 1);
            exposurePanel.Margin = S(new Padding(0, 10, 0, 0));
            camera.Controls.Add(exposurePanel);
            var pickers = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Size = new Size(inner, S(52)), Margin = S(new Padding(0, 8, 0, 0)), BackColor = Theme.Card };
            pickers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pickers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pickers.RowStyles.Add(new RowStyle(SizeType.Absolute, S(20)));
            pickers.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pickers.Controls.Add(Caption(Strings.CameraIso), 0, 0);
            pickers.Controls.Add(Caption(Strings.Shutter), 1, 0);
            isoBox.Dock = shutterBox.Dock = DockStyle.Fill;
            isoBox.Margin = S(new Padding(0, 2, 6, 0));
            shutterBox.Margin = S(new Padding(6, 2, 0, 0));
            pickers.Controls.Add(isoBox, 0, 1);
            pickers.Controls.Add(shutterBox, 1, 1);
            camera.Controls.Add(pickers);
            Fit(camera, dialHint, inner, 0);
            AddSection(stack, Strings.SectionCamera, Theme.GlyphCamera, camera);

            // Canon software warning (shown only while the camera isn't streaming)
            var warnTitle = new Label
            {
                Text = Strings.CanonRunningTitle, AutoSize = true, ForeColor = Theme.Warning,
                Font = new Font(Theme.SemiboldFontName, Theme.FontSize), Margin = S(new Padding(0, 0, 0, 4)), BackColor = Theme.WarningCard,
            };
            canonCard.Controls.Add(warnTitle);
            canonText.BackColor = Theme.WarningCard;
            Fit(canonCard, canonText, inner, 0);
            canonCard.Margin = S(new Padding(0, 0, 0, 18));
            stack.Controls.Add(canonCard);

            // Output
            var output = NewCard();
            AddField(output, Strings.Resolution, resSeg, inner);
            AddField(output, Strings.Framerate, fpsSeg, inner);
            AddField(output, Strings.Aspect, fitSeg, inner);
            Fit(output, fitHint, inner, 0);
            fitHint.Margin = S(new Padding(0, 6, 0, 0));
            AddSection(stack, Strings.SectionOutput, Theme.GlyphMonitor, output);

            // Image
            var image = NewCard();
            Fit(image, mirrorBox, inner, S(26));
            Fit(image, flipBox, inner, S(26));
            flipBox.Margin = S(new Padding(0, 4, 0, 0));
            AddField(image, Strings.NoiseReduction, noiseSeg, inner);
            AddField(image, Strings.Sharpness, sharpSeg, inner);
            AddSection(stack, Strings.SectionImage, Theme.GlyphImage, image);

            // Virtual camera
            var vcam = NewCard();
            Fit(vcam, vcamStatus, inner, S(20));
            var vcamText = Caption(Strings.VcamDescription);
            Fit(vcam, vcamText, inner, 0);
            vcamText.Margin = S(new Padding(0, 4, 0, 8));
            vcamBtn.Margin = Padding.Empty;
            vcam.Controls.Add(vcamBtn);
            AddSection(stack, Strings.SectionVirtualCamera, Theme.GlyphVideo, vcam);

            inspector.Controls.Add(stack);
        }

        Card NewCard() { return new Card(S(Theme.Radius), S(new Padding(12))) { Margin = S(new Padding(0, 6, 0, 18)) }; }

        void AddSection(Control stack, string title, string glyph, Card card)
        {
            var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.Surface };
            header.Controls.Add(new Label
            {
                Text = glyph, AutoSize = true, Font = new Font(Theme.IconFontName, Theme.FontSize), ForeColor = Theme.Muted,
                Margin = S(new Padding(0, 2, 4, 0)),
            });
            header.Controls.Add(new Label
            {
                Text = title, AutoSize = true, Font = new Font(Theme.SemiboldFontName, Theme.FontSize), ForeColor = Theme.Muted,
                Margin = Padding.Empty,
            });
            stack.Controls.Add(header);
            stack.Controls.Add(card);
        }

        void AddField(Card card, string title, Control control, int width)
        {
            var caption = Caption(title);
            Fit(card, caption, width, 0);
            caption.Margin = S(new Padding(0, card.Controls.Count > 1 ? 10 : 0, 0, 4));
            Fit(card, control, width, S(30));
        }

        // Adds a control at full card width; height 0 = wrap the label's text to fit.
        static void Fit(Control parent, Control c, int width, int height)
        {
            c.Width = width;
            var label = c as Label;
            if (height == 0 && label != null)
            {
                label.AutoSize = false;
                label.Height = TextRenderer.MeasureText(label.Text.Length > 0 ? label.Text : " ", label.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height;
            }
            else c.Height = height;
            if (c.Margin == new Padding(3)) c.Margin = Padding.Empty;
            parent.Controls.Add(c);
        }

        void AddTile(Panel panel, string name, Label value, int col, int row)
        {
            int w = panel.Width / 2, h = panel.Height / 2;
            var n = new Label
            {
                Text = name.ToUpperInvariant(), AutoSize = false, Location = new Point(col * w, row * h), Size = new Size(w, S(16)),
                Font = new Font(Theme.SemiboldFontName, 7f), ForeColor = Theme.Muted, BackColor = Theme.Card,
            };
            value.Location = new Point(col * w, row * h + S(16));
            value.Size = new Size(w - S(6), S(22));
            panel.Controls.Add(n);
            panel.Controls.Add(value);
        }

        Label Value()
        {
            return new Label { AutoSize = false, Text = "–", Font = new Font(Theme.SemiboldFontName, 11f), ForeColor = Theme.Text, BackColor = Theme.Card, AutoEllipsis = true };
        }

        static Label Caption(string text)
        {
            return new Label { Text = text, ForeColor = Theme.Muted, BackColor = Theme.Card, Margin = Padding.Empty, AutoSize = false };
        }

        static ComboBox Combo()
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Text };
        }

        void PlaceCenterButton()
        {
            centerStartBtn.Location = new Point((preview.Width - centerStartBtn.Width) / 2, preview.Height / 2 + S(56));
        }

        // Dark title bar on Windows 10 (1809+) and 11.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref on, 4) != 0)
                    DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkModeOld, ref on, 4);
                int caption = ColorTranslator.ToWin32(Theme.Bar);
                DwmSetWindowAttribute(Handle, DwmCaptionColor, ref caption, 4); // Windows 11 only; ignored elsewhere
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        const int DwmUseImmersiveDarkModeOld = 19, DwmUseImmersiveDarkMode = 20, DwmCaptionColor = 35;
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.R) || keyData == Keys.F5) { ToggleStreaming(); return true; }
            if (keyData == (Keys.Control | Keys.S)) { SaveSnapshot(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- streaming ---------------------------------------------------

        void ToggleStreaming() { if (engine == null) StartStreaming(); else StopStreaming(); }

        void StartStreaming()
        {
            if (engine != null || !startBtn.Enabled) return; // already running, or diagnostics hold the camera
            source = sourceSeg.SelectedIndex == SourceTestIndex ? (LiveSource)new TestPatternSource() : new CanonSource();
            source.Start();
            StartEngine();
            startBtn.Text = Strings.Stop;
            startBtn.Glyph = Theme.GlyphStop;
            startBtn.Fill = Theme.StopButton;
            startBtn.Invalidate();
            diagBtn.Enabled = false;
            UpdateStatus();
        }

        void StartEngine()
        {
            engine = new OutputEngine(source, settings);
            engine.FrameReady += OnFrameReady;
            engine.Sink = vcamWriter;
            engine.Start();
        }

        void StopStreaming()
        {
            if (engine != null) { engine.Stop(); engine = null; }
            if (source != null) { source.Stop(); source = null; }
            startBtn.Text = Strings.Start;
            startBtn.Glyph = Theme.GlyphPlay;
            startBtn.Fill = Theme.Accent;
            startBtn.Invalidate();
            diagBtn.Enabled = true;
            preview.Invalidate();
            UpdateStatus();
        }

        // Format changes restart only the output; the camera session keeps running.
        void ApplySetting(StreamSettings next)
        {
            if (next.Equals(settings)) return;
            bool sameFormat = next.SameFormat(settings);
            settings = next;
            VirtualCamera.WritePreferredFormat(settings);
            try { settings.Save(configPath); }
            catch (IOException e) { Post(string.Format(Strings.SettingsSaveFailed, e.Message)); }
            catch (UnauthorizedAccessException e) { Post(string.Format(Strings.SettingsSaveFailed, e.Message)); }
            if (engine == null) { UpdateStatus(); return; }
            if (sameFormat)
            {
                // Flip and cleanup keep the output format, so update the running output in place.
                engine.FlipHorizontal = next.FlipHorizontal;
                engine.FlipVertical = next.FlipVertical;
                engine.NoiseReduction = next.NoiseReduction;
                engine.Sharpness = next.Sharpness;
            }
            else
            {
                engine.Stop();
                StartEngine();
            }
            UpdateStatus();
        }

        void OnFrameReady()
        {
            // Coalesce: at most one pending repaint.
            if (Interlocked.Exchange(ref invalidatePending, 1) == 0 && IsHandleCreated)
            {
                try { BeginInvoke((Action)(() => { invalidatePending = 0; preview.Invalidate(); })); }
                catch (InvalidOperationException) { } // window closing
            }
        }

        // ---- preview -------------------------------------------------------

        Phase CurrentPhase()
        {
            if (source == null) return Phase.Stopped;
            string s = source.State;
            if (s == Strings.Live) return Phase.Live;
            if (s == Strings.WaitingForLiveView) return Phase.Waiting;
            if (s == Strings.Connecting) return Phase.Connecting;
            return source.Faulted ? Phase.Problem : Phase.Connecting;
        }

        static string PhaseTitle(Phase p)
        {
            switch (p)
            {
                case Phase.Live: return Strings.PhaseLive;
                case Phase.Waiting: return Strings.PhaseWaiting;
                case Phase.Connecting: return Strings.PhaseConnecting;
                case Phase.Problem: return Strings.PhaseRetrying;
                default: return Strings.Stopped;
            }
        }

        static Color PhaseColor(Phase p)
        {
            switch (p)
            {
                case Phase.Live: return Theme.Live;
                case Phase.Waiting: case Phase.Connecting: return Theme.Warning;
                case Phase.Problem: return Color.Gold;
                default: return Theme.Muted;
            }
        }

        void OnPreviewPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = new Rectangle(preview.Padding.Left, preview.Padding.Top,
                preview.ClientSize.Width - preview.Padding.Horizontal, preview.ClientSize.Height - preview.Padding.Vertical);
            if (area.Width < 10 || area.Height < 10) return;
            float radius = S(10);
            Draw.FillRound(g, Theme.PreviewBackground, area, radius);

            var eng = engine;
            bool drawn = eng != null && eng.WithFrame(frame =>
            {
                var r = Geometry.FitRect(frame.Width, frame.Height, area.Width, area.Height);
                r.Offset(area.X, area.Y);
                using (var clip = Draw.RoundRect(area, radius))
                {
                    var state = g.Save();
                    g.SetClip(clip);
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(frame, r);
                    g.Restore(state);
                }
            });
            centerStartBtn.Visible = source == null;
            var phase = CurrentPhase();
            if (!drawn) DrawPlaceholder(g, area, phase);

            int pad = S(12);
            // Status badge, top left.
            string model = source != null ? source.Model : "";
            DrawBadge(g, new Point(area.X + pad, area.Y + pad), phase, model);
            // Output format, top right.
            if (engine != null)
            {
                string fmt = settings.Width + "×" + settings.Height + " · " + settings.Fps + " fps";
                var sz = TextRenderer.MeasureText(g, fmt, Font);
                DrawPill(g, new Rectangle(area.Right - pad - sz.Width - S(20), area.Y + pad, sz.Width + S(20), S(26)), fmt, Theme.Text);
            }
            // Notice and statistics, bottom center.
            int bottom = area.Bottom - pad;
            if (drawn && engine != null && source != null)
            {
                string stats = string.Format(Strings.StatsPill, engine.OutputFps, source.Meter.Current, engine.Repeated, engine.Dropped)
                               + (vcamWriter.Connected ? Strings.StatsVcam : "");
                var sz = TextRenderer.MeasureText(g, stats, Font);
                var r = new Rectangle(area.X + (area.Width - sz.Width - S(28)) / 2, bottom - S(28), sz.Width + S(28), S(28));
                DrawPill(g, r, stats, Theme.Text);
                bottom = r.Y - S(8);
            }
            if (notice != null)
            {
                var sz = TextRenderer.MeasureText(g, notice, Font);
                int w = Math.Min(sz.Width + S(32), area.Width - 2 * pad);
                DrawPill(g, new Rectangle(area.X + (area.Width - w) / 2, bottom - S(32), w, S(32)), notice, Theme.Text);
            }
        }

        void DrawPlaceholder(Graphics g, Rectangle area, Phase phase)
        {
            int cy = area.Y + area.Height / 2;
            using (var icon = new Font(Theme.IconFontName, 30f))
                TextRenderer.DrawText(g, Theme.GlyphVideo, icon, new Rectangle(area.X, cy - S(96), area.Width, S(56)), Color.FromArgb(120, 120, 120),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            string title = source == null ? Strings.CameraStopped : PhaseTitle(phase) + "…";
            using (var big = new Font(Theme.SemiboldFontName, 13f))
                TextRenderer.DrawText(g, title, big, new Rectangle(area.X, cy - S(36), area.Width, S(30)), Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            string detail = source == null ? Strings.PressStart : DetailText(phase);
            if (detail.Length > 0)
            {
                int w = Math.Min(S(460), area.Width - S(40));
                TextRenderer.DrawText(g, detail, Font, new Rectangle(area.X + (area.Width - w) / 2, cy - S(4), w, S(52)), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            }
        }

        string DetailText(Phase phase)
        {
            if (source == null || phase == Phase.Live) return "";
            if (source is CanonSource && phase == Phase.Connecting && canonRunning.Count > 0)
                return string.Format(Strings.CanonRunningDetail, string.Join(", ", canonRunning));
            return source.State == Strings.Connecting ? "" : source.State;
        }

        void DrawBadge(Graphics g, Point at, Phase phase, string model)
        {
            string title = PhaseTitle(phase).ToUpperInvariant();
            using (var bold = new Font(Theme.SemiboldFontName, 8f))
            {
                var ts = TextRenderer.MeasureText(g, title, bold);
                var ms = model.Length > 0 ? TextRenderer.MeasureText(g, model, Font) : Size.Empty;
                int dot = S(8), h = S(26);
                int w = S(12) + dot + S(7) + ts.Width + (model.Length > 0 ? S(6) + ms.Width : 0) + S(10);
                var r = new Rectangle(at.X, at.Y, w, h);
                Draw.FillRound(g, Theme.Overlay, r, h / 2f);
                bool dim = phase == Phase.Live && DateTime.Now.Millisecond < 500;
                using (var b = new SolidBrush(dim ? Color.FromArgb(110, PhaseColor(phase)) : PhaseColor(phase)))
                    g.FillEllipse(b, r.X + S(12), r.Y + (h - dot) / 2f, dot, dot);
                int x = r.X + S(12) + dot + S(7);
                TextRenderer.DrawText(g, title, bold, new Rectangle(x, r.Y, ts.Width, h), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (model.Length > 0)
                    TextRenderer.DrawText(g, model, Font, new Rectangle(x + ts.Width + S(6), r.Y, ms.Width, h), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        void DrawPill(Graphics g, Rectangle r, string text, Color color)
        {
            Draw.FillRound(g, Theme.Overlay, r, r.Height / 2f);
            TextRenderer.DrawText(g, text, Font, r, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        // ---- virtual camera ------------------------------------------------

        void StartVirtualCamera()
        {
            if (!VirtualCamera.Supported) { vcamBtn.Visible = false; vcamError = Strings.VcamNeedsWindows11; return; }
            vcamBtn.Visible = !VirtualCamera.Installed;
            if (!VirtualCamera.Installed) return;
            VirtualCamera.WritePreferredFormat(settings);
            vcamError = VirtualCamera.Start();
        }

        void InstallVirtualCamera()
        {
            vcamBtn.Enabled = false;
            Post(Strings.VcamInstalling);
            bool ok = VirtualCamera.RunElevated(VirtualCamera.InstallArg);
            vcamBtn.Enabled = true;
            if (!ok || !VirtualCamera.Installed) { Post(Strings.VcamInstallFailed); return; }
            StartVirtualCamera();
            Post(vcamError == null ? Strings.VcamInstalled : Strings.VcamPrefix + vcamError);
        }

        void UpdateVirtualCameraStatus()
        {
            string state = vcamError ?? (!VirtualCamera.Running ? Strings.VcamNotInstalled
                                         : vcamWriter.Connected ? Strings.VcamInUse : Strings.VcamReady);
            string text = "●  " + char.ToUpper(state[0]) + state.Substring(1);
            if (vcamStatus.Text != text) vcamStatus.Text = text;
            var color = vcamWriter.Connected ? Theme.Good : vcamError != null ? Theme.Muted : Theme.Text;
            if (vcamStatus.ForeColor != color) vcamStatus.ForeColor = color;
            vcamStatus.BackColor = Theme.Card;
        }

        // ---- camera exposure -----------------------------------------------

        void SendCameraSetting(uint prop, ComboBox box)
        {
            var cam = source as CanonSource;
            if (syncingCamera || cam == null || box.SelectedItem == null) return;
            cam.RequestProperty(prop, (uint)((Choice)box.SelectedItem).Value);
        }

        // Mirrors the camera's own settings into the inspector and enables ISO/shutter only where the mode allows it.
        void SyncCameraControls()
        {
            var cam = source as CanonSource;
            var ex = cam == null ? null : cam.Exposure;
            SetText(modeValue, ex == null ? "–" : ShortMode(CameraValues.ModeName(ex.Mode)));
            SetText(isoValue, ex == null ? "–" : CameraValues.IsoName(ex.Iso));
            SetText(shutterValue, ex == null ? "–" : CameraValues.ShutterName(ex.Shutter));
            SetText(apertureValue, ex == null ? "–" : CameraValues.ApertureName(ex.Aperture));
            bool hint = ex != null && !CameraValues.ShutterSettable(ex.Mode);
            if (dialHint.Visible != hint) dialHint.Visible = hint;
            syncingCamera = true;
            try
            {
                isoBox.Enabled = ex != null && CameraValues.IsoSettable(ex.Mode);
                shutterBox.Enabled = ex != null && CameraValues.ShutterSettable(ex.Mode);
                if (ex == null) return;
                if (!isoBox.DroppedDown && ex.Iso.HasValue) SelectOrClear(isoBox, (int)ex.Iso.Value);
                if (!shutterBox.DroppedDown && ex.Shutter.HasValue) SelectOrClear(shutterBox, (int)ex.Shutter.Value);
            }
            finally { syncingCamera = false; }
        }

        // "A+ (Scene Intelligent Auto)" -> "A+" for the small tile.
        static string ShortMode(string mode)
        {
            int i = mode.IndexOf(" (", StringComparison.Ordinal);
            return i > 0 ? mode.Substring(0, i) : mode;
        }

        static void SetText(Label l, string text) { if (l.Text != text) l.Text = text; }

        void UpdateStatus()
        {
            if (notice != null && DateTime.UtcNow > noticeUntil) notice = null;
            if ((DateTime.UtcNow - lastCanonCheck).TotalMilliseconds > CanonCheckMs)
            {
                lastCanonCheck = DateTime.UtcNow;
                canonRunning = CanonSoftware.Running();
            }
            SyncCameraControls();
            UpdateVirtualCameraStatus();

            var phase = CurrentPhase();
            bool warn = sourceSeg.SelectedIndex == SourceCanonIndex && canonRunning.Count > 0 && phase != Phase.Live;
            if (warn) SetText(canonText, string.Format(Strings.CanonRunningCard, string.Join(", ", canonRunning)));
            if (canonCard.Visible != warn) canonCard.Visible = warn;
            SetText(fitHint, settings.Fit == FitMode.Fill ? Strings.AspectFillHint : Strings.AspectFitHint);
            snapBtn.Enabled = engine != null;

            var canon = source as CanonSource;
            if (canon != null && canon.CommandError != null) { Post(canon.CommandError); canon.CommandError = null; }
            preview.Invalidate(); // badge, stats and notice
        }

        void Post(string message)
        {
            notice = message;
            noticeUntil = DateTime.UtcNow.AddMilliseconds(NoticeMs);
            preview.Invalidate();
        }

        // ---- snapshot & diagnostics ----------------------------------------

        void SaveSnapshot()
        {
            var eng = engine;
            if (eng == null) return;
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string path = Path.Combine(dir, Strings.SnapshotFilePrefix + DateTime.Now.ToString(SnapshotTimeFormat) + ".png");
            if (eng.WithFrame(f => f.Save(path, ImageFormat.Png)))
                Post(string.Format(Strings.SnapshotSaved, path));
        }

        void RunDiagnostics()
        {
            diagBtn.Enabled = startBtn.Enabled = centerStartBtn.Enabled = false;
            Post(Strings.DiagRunning);
            var t = new Thread(() =>
            {
                string report = Diagnostics.Run();
                string path = Diagnostics.SaveReport(report);
                BeginInvoke((Action)(() =>
                {
                    diagBtn.Enabled = startBtn.Enabled = centerStartBtn.Enabled = true;
                    Post(string.Format(Strings.DiagSaved, path));
                    ShowReport(report, path);
                }));
            }) { IsBackground = true };
            t.Start();
        }

        void ShowReport(string report, string path)
        {
            using (var f = new Form { Text = string.Format(Strings.DiagWindowTitle, path), ClientSize = S(Theme.DiagnosticsSize),
                                      StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Background, ForeColor = Theme.Text })
            {
                var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill,
                                        BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
                                        Font = new Font(Theme.MonoFontName, Theme.FontSize), Text = report.Replace("\n", "\r\n"), WordWrap = false };
                var bar = new Panel { Dock = DockStyle.Bottom, Height = S(52), BackColor = Theme.Bar, Padding = S(new Padding(12, 10, 12, 10)) };
                var copy = new PillButton(Strings.CopyToClipboard, null, S(6)) { Fill = Theme.Accent, Dock = DockStyle.Right, Width = S(160) };
                copy.Click += delegate { Clipboard.SetText(report); copy.Text = Strings.Copied; copy.Invalidate(); };
                bar.Controls.Add(copy);
                f.Controls.Add(box);
                f.Controls.Add(bar);
                f.ShowDialog(this);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            statusTimer.Stop();
            StopStreaming(); // returns the camera's live view to normal
            VirtualCamera.Stop();
            vcamWriter.Dispose();
            base.OnFormClosing(e);
        }

        // ---- small helpers -----------------------------------------------

        sealed class Choice
        {
            public readonly string Text; public readonly int Value;
            public Choice(string t, int v) { Text = t; Value = v; }
            public override string ToString() { return Text; }
        }

        // Only the project's own https links are ever opened.
        static void OpenUrl(string url)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { System.Diagnostics.Process.Start(url); } catch (System.ComponentModel.Win32Exception) { }
        }

        static void SelectOrClear(ComboBox box, int value)
        {
            for (int i = 0; i < box.Items.Count; i++)
                if (((Choice)box.Items[i]).Value == value) { if (box.SelectedIndex != i) box.SelectedIndex = i; return; }
            box.SelectedIndex = -1; // camera is on a value the app does not list
        }

        public void CaptureWindow(string path)
        {
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        public void SelectSource(int index) { sourceSeg.SelectedIndex = index; }
    }

    sealed class PreviewPanel : Panel
    {
        public PreviewPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Background;
        }
    }
}
