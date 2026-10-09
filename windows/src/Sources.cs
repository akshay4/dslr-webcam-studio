// Frame sources. Each runs on its own thread and keeps only the newest frame,
// so the paced output loop never waits on the camera.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace DslrWebcamStudio
{
    public abstract class LiveSource
    {
        readonly object gate = new object();
        Bitmap latest;
        int seq;
        Thread thread;
        protected volatile bool Stopping;
        public readonly RateMeter Meter = new RateMeter(SystemClock.Instance);

        public volatile string Model = "";
        public volatile string State = "Stopped";   // user-facing status line
        public volatile bool Faulted;

        public abstract string Name { get; }

        public void Start()
        {
            Stopping = false;
            thread = new Thread(Run) { IsBackground = true, Name = Name };
            thread.Start();
        }

        public void Stop()
        {
            Stopping = true;
            if (thread != null) { thread.Join(4000); thread = null; }
            lock (gate) { if (latest != null) { latest.Dispose(); latest = null; } }
            State = "Stopped";
        }

        public int Seq { get { lock (gate) return seq; } }

        // Runs draw() on the newest frame under the source lock; returns that frame's sequence number (0 = none yet).
        public int WithLatest(Action<Bitmap> draw)
        {
            lock (gate)
            {
                if (latest == null) return 0;
                draw(latest);
                return seq;
            }
        }

        protected void Publish(Bitmap frame)
        {
            Bitmap old;
            lock (gate) { old = latest; latest = frame; seq++; }
            if (old != null) old.Dispose();
            Meter.Tick();
        }

        protected abstract void Run();

        // Sleep in small steps so Stop() returns quickly.
        protected void Nap(int ms)
        {
            for (int t = 0; t < ms && !Stopping; t += 50) Thread.Sleep(Math.Min(50, ms - t));
        }
    }

    // Snapshot of the camera's exposure settings, for the UI.
    public sealed class CameraExposure
    {
        public uint? Mode, Iso, Shutter, Aperture;
        public override string ToString()
        {
            return CameraValues.ModeName(Mode) + "  |  ISO " + CameraValues.IsoName(Iso) + "  |  " +
                   CameraValues.ShutterName(Shutter) + "  |  " + CameraValues.ApertureName(Aperture);
        }
    }

    // Canon EOS live view over USB using our PTP implementation (CanonEos.cs).
    public sealed class CanonSource : LiveSource
    {
        public override string Name { get { return "Canon EOS (USB)"; } }

        readonly ConcurrentQueue<KeyValuePair<uint, uint>> requests = new ConcurrentQueue<KeyValuePair<uint, uint>>();
        public volatile CameraExposure Exposure;     // null until connected
        public volatile string CommandError;         // last refused setting, for the UI

        // Queues a property change; PTP calls must happen on the camera thread.
        public void RequestProperty(uint prop, uint value)
        {
            requests.Enqueue(new KeyValuePair<uint, uint>(prop, value));
        }

        void ApplyRequests(CanonLiveView lv)
        {
            KeyValuePair<uint, uint> r;
            while (requests.TryDequeue(out r))
            {
                var resp = lv.SetProperty(r.Key, r.Value);
                CommandError = resp.Ok ? null
                    : "Camera refused the change (" + Ptp.RcName(resp.Code) + "). Set the mode dial to M.";
            }
        }

        void UpdateExposure(CanonLiveView lv)
        {
            Exposure = new CameraExposure
            {
                Mode = lv.Prop(Ptp.DPC_EOS_AutoExposureMode), Iso = lv.Prop(Ptp.DPC_EOS_ISOSpeed),
                Shutter = lv.Prop(Ptp.DPC_EOS_ShutterSpeed), Aperture = lv.Prop(Ptp.DPC_EOS_Aperture),
            };
        }

        protected override void Run()
        {
            while (!Stopping)
            {
                CanonLiveView lv = null;
                try
                {
                    State = "Connecting to camera...";
                    lv = CanonLiveView.Open(null);
                    Model = lv.Model;
                    Faulted = false;
                    State = "Live";
                    UpdateExposure(lv);
                    var lastExposure = DateTime.UtcNow;
                    while (!Stopping)
                    {
                        if (!requests.IsEmpty) { ApplyRequests(lv); UpdateExposure(lv); }
                        if ((DateTime.UtcNow - lastExposure).TotalMilliseconds > 500)
                        {
                            UpdateExposure(lv); // dial or settings changed on the camera itself
                            lastExposure = DateTime.UtcNow;
                        }
                        var jpeg = lv.ReadJpeg();
                        if (jpeg == null) { Thread.Sleep(5); continue; }
                        Publish(Decode(jpeg));
                    }
                }
                catch (Exception e)
                {
                    Faulted = true;
                    State = "Camera: " + e.Message + " Retrying...";
                }
                finally
                {
                    Exposure = null;
                    if (lv != null) lv.Dispose();
                }
                if (!Stopping) Nap(2000); // camera unplugged, asleep, or busy: try again
            }
        }

        static Bitmap Decode(byte[] jpeg)
        {
            using (var ms = new MemoryStream(jpeg))
            using (var img = Image.FromStream(ms, false, false))
            {
                var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(img, 0, 0);
                return bmp;
            }
        }
    }

    // 3:2 moving test pattern at ~29.97 fps, sized like Canon live view (960x640).
    public sealed class TestPatternSource : LiveSource
    {
        public override string Name { get { return "Test pattern"; } }

        protected override void Run()
        {
            Model = "Test pattern 960x640";
            State = "Live";
            const int W = 960, H = 640;
            var colors = new[] { Color.FromArgb(192, 192, 192), Color.FromArgb(192, 192, 0), Color.FromArgb(0, 192, 192),
                                 Color.FromArgb(0, 192, 0), Color.FromArgb(192, 0, 192), Color.FromArgb(192, 0, 0), Color.FromArgb(0, 0, 192) };
            var clock = SystemClock.Instance;
            double next = clock.Now;
            using (var font = new Font("Segoe UI", 40, FontStyle.Bold))
            {
                for (int n = 0; !Stopping; n++)
                {
                    var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        int bw = W / colors.Length;
                        for (int i = 0; i < colors.Length; i++)
                            using (var b = new SolidBrush(colors[i])) g.FillRectangle(b, i * bw, 0, bw + 1, H);
                        g.FillRectangle(Brushes.White, (n * 8) % W, 0, 24, H);
                        g.DrawString("frame " + n, font, Brushes.Black, 40, H - 110);
                    }
                    Publish(bmp);
                    next += 1 / 29.97;
                    double wait = next - clock.Now;
                    if (wait > 0) clock.Sleep(wait); else next = clock.Now;
                }
            }
        }
    }
}
