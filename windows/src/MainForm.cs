// Main window: source, Streaming Video Output Resolution, Target Streaming Framerate,
// aspect handling, live preview and status.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DslrWebcamStudio
{
    public sealed class MainForm : Form
    {
        readonly string configPath;
        StreamSettings settings;
        LiveSource source;
        OutputEngine engine;
        int invalidatePending;

        readonly ComboBox sourceBox = Combo(150), resBox = Combo(170), fpsBox = Combo(80), fitBox = Combo(170), noiseBox = Combo(90), sharpBox = Combo(90);
        readonly ComboBox isoBox = Combo(80), shutterBox = Combo(80);
        bool syncingCamera; // true while the UI mirrors camera values, so they aren't sent back
        readonly ToolStripStatusLabel exposureLabel = new ToolStripStatusLabel();
        readonly CheckBox mirrorBox = Check("Mirror left/right"), flipBox = Check("Flip upside-down");
        readonly Button startBtn = new Button { Text = "Start", Width = 90, Height = 28 };
        readonly Button snapBtn = new Button { Text = "Snapshot", Width = 90, Height = 28 };
        readonly Button diagBtn = new Button { Text = "Diagnostics", Width = 100, Height = 28 };
        readonly Button coffeeBtn = new Button { Text = "☕ Buy me a coffee", Width = 140, Height = 28 };
        readonly PreviewPanel preview = new PreviewPanel();
        readonly ToolStripStatusLabel stateLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly ToolStripStatusLabel statsLabel = new ToolStripStatusLabel();
        readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer { Interval = 250 };

        public MainForm(string configPath)
        {
            this.configPath = configPath;
            settings = StreamSettings.Load(configPath);

            Text = "DSLR Webcam Studio " + AppInfo.Version;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            ClientSize = new Size(1000, 640);
            MinimumSize = new Size(760, 480);
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.FromArgb(32, 32, 36);
            ForeColor = Color.Gainsboro;

            sourceBox.Items.AddRange(new object[] { "Canon EOS (USB)", "Test pattern" });
            sourceBox.SelectedIndex = 0;
            foreach (int r in StreamSettings.Resolutions)
            {
                int w, h;
                StreamSettings.ResolutionToSize(r, out w, out h);
                resBox.Items.Add(new Choice(w + " x " + h + "  (" + r + "p)", r));
            }
            foreach (int f in StreamSettings.FpsChoices) fpsBox.Items.Add(new Choice(f + " fps", f));
            fitBox.Items.Add(new Choice("Fit (black side bars)", (int)FitMode.Fit));
            fitBox.Items.Add(new Choice("Fill (crop to 16:9)", (int)FitMode.Fill));
            Select(resBox, settings.Resolution);
            Select(fpsBox, settings.Fps);
            Select(fitBox, (int)settings.Fit);
            for (int i = 0; i < StreamSettings.Levels.Length; i++)
            {
                noiseBox.Items.Add(new Choice(StreamSettings.Levels[i], i));
                sharpBox.Items.Add(new Choice(StreamSettings.Levels[i], i));
            }
            Select(noiseBox, settings.NoiseReduction);
            Select(sharpBox, settings.Sharpness);
            foreach (var kv in CameraValues.IsoChoices) isoBox.Items.Add(new Choice(kv.Value, (int)kv.Key));
            foreach (var kv in CameraValues.ShutterChoices) shutterBox.Items.Add(new Choice(kv.Value, (int)kv.Key));
            isoBox.Enabled = shutterBox.Enabled = false;
            isoBox.SelectedIndexChanged += delegate { SendCameraSetting(Ptp.DPC_EOS_ISOSpeed, isoBox); };
            shutterBox.SelectedIndexChanged += delegate { SendCameraSetting(Ptp.DPC_EOS_ShutterSpeed, shutterBox); };
            mirrorBox.Checked = settings.FlipHorizontal;
            flipBox.Checked = settings.FlipVertical;

            resBox.SelectedIndexChanged += delegate { ApplySetting(settings.WithResolution(Value(resBox))); };
            fpsBox.SelectedIndexChanged += delegate { ApplySetting(settings.WithFps(Value(fpsBox))); };
            fitBox.SelectedIndexChanged += delegate { ApplySetting(settings.WithFit((FitMode)Value(fitBox))); };
            noiseBox.SelectedIndexChanged += delegate { ApplySetting(settings.WithEnhancement(Value(noiseBox), Value(sharpBox))); };
            sharpBox.SelectedIndexChanged += delegate { ApplySetting(settings.WithEnhancement(Value(noiseBox), Value(sharpBox))); };
            mirrorBox.CheckedChanged += delegate { ApplySetting(settings.WithFlip(mirrorBox.Checked, flipBox.Checked)); };
            flipBox.CheckedChanged += delegate { ApplySetting(settings.WithFlip(mirrorBox.Checked, flipBox.Checked)); };
            sourceBox.SelectedIndexChanged += delegate { if (engine != null) { StopStreaming(); StartStreaming(); } };
            startBtn.Click += delegate { if (engine == null) StartStreaming(); else StopStreaming(); };
            snapBtn.Click += delegate { SaveSnapshot(); };
            diagBtn.Click += delegate { RunDiagnostics(); };
            coffeeBtn.Click += delegate { OpenUrl(AppInfo.DonateUrl); };
            coffeeBtn.Visible = AppInfo.DonateConfigured;

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 8, 8, 4), WrapContents = true, BackColor = Color.FromArgb(40, 40, 46),
            };
            bar.Controls.AddRange(new Control[] {
                startBtn, snapBtn, diagBtn, coffeeBtn, Spacer(),
                Pair("Source", sourceBox),
                Pair("Streaming Video Output Resolution", resBox),
                Pair("Target Streaming Framerate", fpsBox),
                Pair("Aspect", fitBox),
                mirrorBox, flipBox,
                Pair("Noise reduction", noiseBox),
                Pair("Sharpness", sharpBox),
                Pair("Camera ISO", isoBox),
                Pair("Shutter", shutterBox),
            });
            foreach (Control c in new Control[] { startBtn, snapBtn, diagBtn, coffeeBtn })
            {
                var b = (Button)c;
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = Color.FromArgb(58, 58, 66);
                b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 90);
                b.Margin = new Padding(0, 0, 6, 6);
            }
            startBtn.BackColor = Color.FromArgb(0, 102, 204);
            coffeeBtn.BackColor = Color.FromArgb(255, 196, 57);
            coffeeBtn.ForeColor = Color.FromArgb(40, 30, 10);

            var status = new StatusStrip { BackColor = Color.FromArgb(40, 40, 46), ForeColor = Color.Gainsboro, SizingGrip = false };
            status.Items.AddRange(new ToolStripItem[] { stateLabel, exposureLabel, statsLabel });

            preview.Dock = DockStyle.Fill;
            preview.Paint += OnPreviewPaint;
            Controls.Add(preview);
            Controls.Add(bar);
            Controls.Add(status);

            statusTimer.Tick += delegate { UpdateStatus(); };
            statusTimer.Start();
            Shown += delegate { StartStreaming(); };
            UpdateStatus();
        }

        // ---- streaming ---------------------------------------------------

        void StartStreaming()
        {
            source = sourceBox.SelectedIndex == 1 ? (LiveSource)new TestPatternSource() : new CanonSource();
            source.Start();
            StartEngine();
            startBtn.Text = "Stop";
            startBtn.BackColor = Color.FromArgb(170, 50, 50);
            diagBtn.Enabled = false;
        }

        void StartEngine()
        {
            engine = new OutputEngine(source, settings);
            engine.FrameReady += OnFrameReady;
            engine.Start();
        }

        void StopStreaming()
        {
            if (engine != null) { engine.Stop(); engine = null; }
            if (source != null) { source.Stop(); source = null; }
            startBtn.Text = "Start";
            startBtn.BackColor = Color.FromArgb(0, 102, 204);
            diagBtn.Enabled = true;
            preview.Invalidate();
            UpdateStatus();
        }

        // The output format changes instantly; the camera session keeps running.
        void ApplySetting(StreamSettings next)
        {
            if (next.Equals(settings)) return;
            bool sameFormat = next.SameFormat(settings);
            settings = next;
            try { settings.Save(configPath); }
            catch (Exception e) { stateLabel.Text = "Could not save settings: " + e.Message; }
            if (engine != null && sameFormat)
            {
                // Flip and cleanup keep the output format, so update the running output in place.
                engine.FlipHorizontal = next.FlipHorizontal;
                engine.FlipVertical = next.FlipVertical;
                engine.NoiseReduction = next.NoiseReduction;
                engine.Sharpness = next.Sharpness;
            }
            else if (engine != null)
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
                catch (InvalidOperationException) { }
            }
        }

        void OnPreviewPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Black);
            var eng = engine;
            bool drawn = eng != null && eng.WithFrame(frame =>
            {
                var r = Geometry.FitRect(frame.Width, frame.Height, preview.ClientSize.Width, preview.ClientSize.Height);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(frame, r);
                using (var pen = new Pen(Color.FromArgb(70, 70, 80)))
                    g.DrawRectangle(pen, r.X - 1, r.Y - 1, r.Width + 1, r.Height + 1);
            });
            if (!drawn)
            {
                string msg = source == null ? "Stopped. Press Start." : source.State;
                TextRenderer.DrawText(g, msg, Font, preview.ClientRectangle, Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
        }

        void SendCameraSetting(uint prop, ComboBox box)
        {
            var cam = source as CanonSource;
            if (syncingCamera || cam == null || box.SelectedItem == null) return;
            cam.RequestProperty(prop, (uint)Value(box));
        }

        // Mirrors the camera's own ISO/shutter into the boxes and enables them only where the mode allows it.
        void SyncCameraControls()
        {
            var cam = source as CanonSource;
            var ex = cam == null ? null : cam.Exposure;
            exposureLabel.Text = ex == null ? "" : ex.ToString();
            syncingCamera = true;
            try
            {
                isoBox.Enabled = ex != null && CameraValues.IsoSettable(ex.Mode);
                shutterBox.Enabled = ex != null && CameraValues.ShutterSettable(ex.Mode);
                if (ex != null)
                {
                    if (!isoBox.DroppedDown && ex.Iso.HasValue) SelectOrClear(isoBox, (int)ex.Iso.Value);
                    if (!shutterBox.DroppedDown && ex.Shutter.HasValue) SelectOrClear(shutterBox, (int)ex.Shutter.Value);
                }
            }
            finally { syncingCamera = false; }
        }

        static void SelectOrClear(ComboBox box, int value)
        {
            for (int i = 0; i < box.Items.Count; i++)
                if (((Choice)box.Items[i]).Value == value) { if (box.SelectedIndex != i) box.SelectedIndex = i; return; }
            box.SelectedIndex = -1; // camera is on a value the app does not list
        }

        void UpdateStatus()
        {
            SyncCameraControls();
            if (source == null || engine == null)
            {
                stateLabel.Text = "Stopped";
                statsLabel.Text = "Output " + settings;
                return;
            }
            stateLabel.Text = (source.Model != "" ? source.Model + ": " : "") + source.State;
            var canon = source as CanonSource;
            if (canon != null && canon.CommandError != null) stateLabel.Text = canon.CommandError;
            else if (canon != null && canon.Exposure != null && !CameraValues.IsoSettable(canon.Exposure.Mode))
                stateLabel.Text += "  (turn the mode dial to M to set ISO/shutter)";
            statsLabel.Text = string.Format("Output {0}x{1} @ {2} fps target, {3:F1} actual  |  camera {4:F1} fps  |  repeated {5}  dropped {6}",
                settings.Width, settings.Height, settings.Fps, engine.OutputFps, source.Meter.Current, engine.Repeated, engine.Dropped);
        }

        void SaveSnapshot()
        {
            var eng = engine;
            if (eng == null) return;
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string path = Path.Combine(dir, "DSLRWebcam_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            if (eng.WithFrame(f => f.Save(path, ImageFormat.Png)))
                stateLabel.Text = "Saved " + path;
        }

        void RunDiagnostics()
        {
            diagBtn.Enabled = false;
            startBtn.Enabled = false;
            stateLabel.Text = "Running diagnostics (about 10 seconds)...";
            var t = new Thread(() =>
            {
                string report = Diagnostics.Run();
                string path = Diagnostics.SaveReport(report);
                BeginInvoke((Action)(() =>
                {
                    diagBtn.Enabled = startBtn.Enabled = true;
                    stateLabel.Text = "Diagnostics saved to " + path;
                    ShowReport(report, path);
                }));
            }) { IsBackground = true };
            t.Start();
        }

        void ShowReport(string report, string path)
        {
            using (var f = new Form { Text = "Diagnostics - " + path, ClientSize = new Size(760, 520), StartPosition = FormStartPosition.CenterParent })
            {
                var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill,
                                        Font = new Font("Consolas", 9f), Text = report.Replace("\n", "\r\n"), WordWrap = false };
                var copy = new Button { Text = "Copy to clipboard", Dock = DockStyle.Bottom, Height = 32 };
                copy.Click += delegate { Clipboard.SetText(report); copy.Text = "Copied"; };
                f.Controls.Add(box);
                f.Controls.Add(copy);
                f.ShowDialog(this);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            statusTimer.Stop();
            StopStreaming(); // returns the camera's live view to normal
            base.OnFormClosing(e);
        }

        // ---- small helpers -----------------------------------------------

        sealed class Choice
        {
            public readonly string Text; public readonly int Value;
            public Choice(string t, int v) { Text = t; Value = v; }
            public override string ToString() { return Text; }
        }

        static ComboBox Combo(int width)
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(4, 1, 14, 6),
                                  FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(52, 52, 58), ForeColor = Color.Gainsboro };
        }

        static Label Label(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 5, 0, 0) };
        }

        // Keeps a label and its control together when the toolbar wraps.
        static Control Pair(string text, Control control)
        {
            var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                                          Margin = Padding.Empty, Padding = Padding.Empty };
            p.Controls.Add(Label(text));
            p.Controls.Add(control);
            return p;
        }

        static CheckBox Check(string text)
        {
            return new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 4, 14, 6) };
        }

        public void SetFlip(bool h, bool v) { mirrorBox.Checked = h; flipBox.Checked = v; }

        static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(url); } catch (Exception) { }
        }

        static Control Spacer() { return new Panel { Width = 12, Height = 1, Margin = Padding.Empty }; }

        static void Select(ComboBox box, int value)
        {
            for (int i = 0; i < box.Items.Count; i++)
                if (((Choice)box.Items[i]).Value == value) { box.SelectedIndex = i; return; }
        }

        static int Value(ComboBox box) { return ((Choice)box.SelectedItem).Value; }

        public void CaptureWindow(string path)
        {
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        public void SelectSource(int index) { sourceBox.SelectedIndex = index; }
    }

    sealed class PreviewPanel : Panel
    {
        public PreviewPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.Black;
        }
    }
}
