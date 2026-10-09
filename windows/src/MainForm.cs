// Main window: source, Streaming Video Output Resolution, Target Streaming Framerate,
// aspect, flips, image cleanup, camera exposure, virtual camera, live preview and status.
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
        const int SourceCanonIndex = 0, SourceTestIndex = 1;
        const string SnapshotTimeFormat = "yyyyMMdd_HHmmss";

        readonly string configPath;
        StreamSettings settings;
        LiveSource source;
        OutputEngine engine;
        int invalidatePending;
        bool syncingCamera; // true while the UI mirrors camera values, so they aren't sent back
        string vcamError;

        readonly ComboBox sourceBox = Combo(150), resBox = Combo(170), fpsBox = Combo(80), fitBox = Combo(170);
        readonly ComboBox noiseBox = Combo(90), sharpBox = Combo(90), isoBox = Combo(80), shutterBox = Combo(80);
        readonly CheckBox mirrorBox = Check(Strings.Mirror), flipBox = Check(Strings.Flip);
        readonly Button startBtn = MakeButton(Strings.Start, 90), snapBtn = MakeButton(Strings.Snapshot, 90);
        readonly Button diagBtn = MakeButton(Strings.Diagnostics, 100), vcamBtn = MakeButton(Strings.InstallVirtualCamera, 170);
        readonly Button coffeeBtn = MakeButton(Strings.BuyCoffee, 140);
        readonly PreviewPanel preview = new PreviewPanel();
        readonly ToolStripStatusLabel stateLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly ToolStripStatusLabel vcamLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel exposureLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel statsLabel = new ToolStripStatusLabel();
        readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer { Interval = Timing.StatusRefreshMs };
        readonly VirtualCamera.Writer vcamWriter = new VirtualCamera.Writer();

        public MainForm(string configPath)
        {
            this.configPath = configPath;
            settings = StreamSettings.Load(configPath);

            Text = Strings.AppName + " " + AppInfo.Version;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }
            ClientSize = Theme.WindowSize;
            MinimumSize = Theme.WindowMinSize;
            Font = new Font(Theme.FontName, Theme.FontSize);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            FillChoices();
            WireEvents();
            Controls.Add(preview);
            Controls.Add(BuildToolbar());
            Controls.Add(BuildStatusBar());
            preview.Dock = DockStyle.Fill;
            preview.Paint += OnPreviewPaint;

            statusTimer.Tick += delegate { UpdateStatus(); };
            statusTimer.Start();
            Shown += delegate { StartVirtualCamera(); StartStreaming(); };
            UpdateStatus();
        }

        // ---- layout --------------------------------------------------------

        void FillChoices()
        {
            sourceBox.Items.AddRange(new object[] { Strings.SourceCanon, Strings.SourceTestPattern });
            sourceBox.SelectedIndex = SourceCanonIndex;
            foreach (int r in StreamSettings.Resolutions)
            {
                int w, h;
                StreamSettings.ResolutionToSize(r, out w, out h);
                resBox.Items.Add(new Choice(string.Format(Strings.ResolutionItemFormat, w, h, r), r));
            }
            foreach (int f in StreamSettings.FpsChoices) fpsBox.Items.Add(new Choice(string.Format(Strings.FpsItemFormat, f), f));
            fitBox.Items.Add(new Choice(Strings.AspectFit, (int)FitMode.Fit));
            fitBox.Items.Add(new Choice(Strings.AspectFill, (int)FitMode.Fill));
            for (int i = 0; i < Strings.Levels.Length; i++)
            {
                noiseBox.Items.Add(new Choice(Strings.Levels[i], i));
                sharpBox.Items.Add(new Choice(Strings.Levels[i], i));
            }
            foreach (var kv in CameraValues.IsoChoices) isoBox.Items.Add(new Choice(kv.Value, (int)kv.Key));
            foreach (var kv in CameraValues.ShutterChoices) shutterBox.Items.Add(new Choice(kv.Value, (int)kv.Key));

            Select(resBox, settings.Resolution);
            Select(fpsBox, settings.Fps);
            Select(fitBox, (int)settings.Fit);
            Select(noiseBox, settings.NoiseReduction);
            Select(sharpBox, settings.Sharpness);
            mirrorBox.Checked = settings.FlipHorizontal;
            flipBox.Checked = settings.FlipVertical;
            isoBox.Enabled = shutterBox.Enabled = false;
            coffeeBtn.Visible = AppInfo.DonateConfigured;
        }

        void WireEvents()
        {
            resBox.SelectedIndexChanged += delegate { ApplySetting(settings.WithResolution(Value(resBox))); };
            fpsBox.SelectedIndexChanged += delegate { ApplySetting(settings.WithFps(Value(fpsBox))); };
            fitBox.SelectedIndexChanged += delegate { ApplySetting(settings.WithFit((FitMode)Value(fitBox))); };
            EventHandler enhancement = delegate { ApplySetting(settings.WithEnhancement(Value(noiseBox), Value(sharpBox))); };
            noiseBox.SelectedIndexChanged += enhancement;
            sharpBox.SelectedIndexChanged += enhancement;
            EventHandler flips = delegate { ApplySetting(settings.WithFlip(mirrorBox.Checked, flipBox.Checked)); };
            mirrorBox.CheckedChanged += flips;
            flipBox.CheckedChanged += flips;
            isoBox.SelectedIndexChanged += delegate { SendCameraSetting(Ptp.DPC_EOS_ISOSpeed, isoBox); };
            shutterBox.SelectedIndexChanged += delegate { SendCameraSetting(Ptp.DPC_EOS_ShutterSpeed, shutterBox); };
            sourceBox.SelectedIndexChanged += delegate { if (engine != null) { StopStreaming(); StartStreaming(); } };
            startBtn.Click += delegate { if (engine == null) StartStreaming(); else StopStreaming(); };
            snapBtn.Click += delegate { SaveSnapshot(); };
            diagBtn.Click += delegate { RunDiagnostics(); };
            vcamBtn.Click += delegate { InstallVirtualCamera(); };
            coffeeBtn.Click += delegate { OpenUrl(AppInfo.DonateUrl); };
        }

        Control BuildToolbar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 8, 8, 4), WrapContents = true, BackColor = Theme.Bar,
            };
            bar.Controls.AddRange(new Control[] {
                startBtn, snapBtn, diagBtn, vcamBtn, coffeeBtn, Spacer(),
                Pair(Strings.Source, sourceBox),
                Pair(Strings.Resolution, resBox),
                Pair(Strings.Framerate, fpsBox),
                Pair(Strings.Aspect, fitBox),
                mirrorBox, flipBox,
                Pair(Strings.NoiseReduction, noiseBox),
                Pair(Strings.Sharpness, sharpBox),
                Pair(Strings.CameraIso, isoBox),
                Pair(Strings.Shutter, shutterBox),
            });
            startBtn.BackColor = Theme.StartButton;
            coffeeBtn.BackColor = Theme.CoffeeButton;
            coffeeBtn.ForeColor = Theme.CoffeeText;
            return bar;
        }

        Control BuildStatusBar()
        {
            var status = new StatusStrip { BackColor = Theme.Bar, ForeColor = Theme.Text, SizingGrip = false };
            status.Items.AddRange(new ToolStripItem[] { stateLabel, vcamLabel, exposureLabel, statsLabel });
            return status;
        }

        // ---- streaming ---------------------------------------------------

        void StartStreaming()
        {
            source = sourceBox.SelectedIndex == SourceTestIndex ? (LiveSource)new TestPatternSource() : new CanonSource();
            source.Start();
            StartEngine();
            startBtn.Text = Strings.Stop;
            startBtn.BackColor = Theme.StopButton;
            diagBtn.Enabled = false;
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
            startBtn.BackColor = Theme.StartButton;
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
            catch (IOException e) { stateLabel.Text = string.Format(Strings.SettingsSaveFailed, e.Message); }
            catch (UnauthorizedAccessException e) { stateLabel.Text = string.Format(Strings.SettingsSaveFailed, e.Message); }
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
                using (var pen = new Pen(Theme.PreviewBorder))
                    g.DrawRectangle(pen, r.X - 1, r.Y - 1, r.Width + 1, r.Height + 1);
            });
            if (!drawn)
            {
                string msg = source == null ? Strings.StoppedPressStart : source.State;
                TextRenderer.DrawText(g, msg, Font, preview.ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
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
            stateLabel.Text = Strings.VcamInstalling;
            bool ok = VirtualCamera.RunElevated(VirtualCamera.InstallArg);
            vcamBtn.Enabled = true;
            if (!ok || !VirtualCamera.Installed) { stateLabel.Text = Strings.VcamInstallFailed; return; }
            StartVirtualCamera();
            stateLabel.Text = vcamError == null ? Strings.VcamInstalled : Strings.VcamPrefix + vcamError;
        }

        void UpdateVirtualCameraStatus()
        {
            string state = vcamError ?? (!VirtualCamera.Running ? Strings.VcamNotInstalled
                                         : vcamWriter.Connected ? Strings.VcamInUse : Strings.VcamReady);
            vcamLabel.Text = Strings.VcamPrefix + state;
        }

        // ---- camera exposure -----------------------------------------------

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
                if (ex == null) return;
                if (!isoBox.DroppedDown && ex.Iso.HasValue) SelectOrClear(isoBox, (int)ex.Iso.Value);
                if (!shutterBox.DroppedDown && ex.Shutter.HasValue) SelectOrClear(shutterBox, (int)ex.Shutter.Value);
            }
            finally { syncingCamera = false; }
        }

        void UpdateStatus()
        {
            SyncCameraControls();
            UpdateVirtualCameraStatus();
            if (source == null || engine == null)
            {
                stateLabel.Text = Strings.Stopped;
                statsLabel.Text = string.Format(Strings.OutputIdle, settings);
                return;
            }
            stateLabel.Text = source.Model != "" ? string.Format(Strings.ModelState, source.Model, source.State) : source.State;
            var canon = source as CanonSource;
            if (canon != null && canon.CommandError != null) stateLabel.Text = canon.CommandError;
            else if (canon != null && canon.Exposure != null && !CameraValues.IsoSettable(canon.Exposure.Mode))
                stateLabel.Text += Strings.ModeDialHint;
            statsLabel.Text = string.Format(Strings.OutputStats, settings.Width, settings.Height, settings.Fps,
                engine.OutputFps, source.Meter.Current, engine.Repeated, engine.Dropped);
        }

        // ---- snapshot & diagnostics ----------------------------------------

        void SaveSnapshot()
        {
            var eng = engine;
            if (eng == null) return;
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string path = Path.Combine(dir, Strings.SnapshotFilePrefix + DateTime.Now.ToString(SnapshotTimeFormat) + ".png");
            if (eng.WithFrame(f => f.Save(path, ImageFormat.Png)))
                stateLabel.Text = string.Format(Strings.SnapshotSaved, path);
        }

        void RunDiagnostics()
        {
            diagBtn.Enabled = startBtn.Enabled = false;
            stateLabel.Text = Strings.DiagRunning;
            var t = new Thread(() =>
            {
                string report = Diagnostics.Run();
                string path = Diagnostics.SaveReport(report);
                BeginInvoke((Action)(() =>
                {
                    diagBtn.Enabled = startBtn.Enabled = true;
                    stateLabel.Text = string.Format(Strings.DiagSaved, path);
                    ShowReport(report, path);
                }));
            }) { IsBackground = true };
            t.Start();
        }

        void ShowReport(string report, string path)
        {
            using (var f = new Form { Text = string.Format(Strings.DiagWindowTitle, path), ClientSize = Theme.DiagnosticsSize,
                                      StartPosition = FormStartPosition.CenterParent })
            {
                var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill,
                                        Font = new Font(Theme.MonoFontName, Theme.FontSize), Text = report.Replace("\n", "\r\n"), WordWrap = false };
                var copy = new Button { Text = Strings.CopyToClipboard, Dock = DockStyle.Bottom, Height = Theme.ButtonHeight + 4 };
                copy.Click += delegate { Clipboard.SetText(report); copy.Text = Strings.Copied; };
                f.Controls.Add(box);
                f.Controls.Add(copy);
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

        static Button MakeButton(string text, int width)
        {
            var b = new Button { Text = text, Width = width, Height = Theme.ButtonHeight, FlatStyle = FlatStyle.Flat,
                                 BackColor = Theme.Button, Margin = new Padding(0, 0, 6, 6) };
            b.FlatAppearance.BorderColor = Theme.ButtonBorder;
            return b;
        }

        static ComboBox Combo(int width)
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(4, 1, 14, 6),
                                  FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Text };
        }

        static CheckBox Check(string text)
        {
            return new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 4, 14, 6) };
        }

        // Keeps a label and its control together when the toolbar wraps.
        static Control Pair(string text, Control control)
        {
            var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                                          Margin = Padding.Empty, Padding = Padding.Empty };
            p.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 5, 0, 0) });
            p.Controls.Add(control);
            return p;
        }

        static Control Spacer() { return new Panel { Width = 12, Height = 1, Margin = Padding.Empty }; }

        // Only the project's own https links are ever opened.
        static void OpenUrl(string url)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { System.Diagnostics.Process.Start(url); } catch (System.ComponentModel.Win32Exception) { }
        }

        static void Select(ComboBox box, int value)
        {
            for (int i = 0; i < box.Items.Count; i++)
                if (((Choice)box.Items[i]).Value == value) { box.SelectedIndex = i; return; }
        }

        static void SelectOrClear(ComboBox box, int value)
        {
            for (int i = 0; i < box.Items.Count; i++)
                if (((Choice)box.Items[i]).Value == value) { if (box.SelectedIndex != i) box.SelectedIndex = i; return; }
            box.SelectedIndex = -1; // camera is on a value the app does not list
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
