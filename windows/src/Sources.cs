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
        const int NapStepMs = 50;

        readonly object gate = new object();
        Bitmap latest, spare; // spare: the previous frame, reused for the next one to avoid per-frame allocations
        int seq;
        Thread thread;
        protected volatile bool Stopping;
        public readonly RateMeter Meter = new RateMeter(SystemClock.Instance);

        public volatile string Model = "";
        public volatile string State = Strings.Stopped;   // user-facing status line
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
            if (thread != null) { thread.Join(Timing.ThreadJoinMs); thread = null; }
            lock (gate)
            {
                if (latest != null) { latest.Dispose(); latest = null; }
                if (spare != null) { spare.Dispose(); spare = null; }
            }
            State = Strings.Stopped;
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

        // A bitmap to draw the next frame into: the recycled previous frame when the size matches.
        protected Bitmap RentFrame(int width, int height)
        {
            Bitmap b;
            lock (gate) { b = spare; spare = null; }
            if (b != null && b.Width == width && b.Height == height) return b;
            if (b != null) b.Dispose();
            return new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        }

        protected void Publish(Bitmap frame)
        {
            lock (gate)
            {
                if (spare != null) spare.Dispose();
                spare = latest; // no longer visible to readers once replaced (readers hold the lock)
                latest = frame;
                seq++;
            }
            Meter.Tick();
        }

        protected abstract void Run();

        // Sleep in small steps so Stop() returns quickly.
        protected void Nap(int ms)
        {
            for (int t = 0; t < ms && !Stopping; t += NapStepMs) Thread.Sleep(Math.Min(NapStepMs, ms - t));
        }
    }

    // Snapshot of the camera's exposure settings, for the UI.
    public sealed class CameraExposure
    {
        public uint? Mode, Iso, Shutter, Aperture;
        public override string ToString()
        {
            return string.Format(Strings.ExposureFormat, CameraValues.ModeName(Mode), CameraValues.IsoName(Iso),
                                 CameraValues.ShutterName(Shutter), CameraValues.ApertureName(Aperture));
        }
    }

    // Canon EOS live view over USB using our PTP implementation (CanonEos.cs).
    public sealed class CanonSource : LiveSource
    {
        public override string Name { get { return Strings.SourceCanon; } }

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
                CommandError = resp.Ok ? null : string.Format(Strings.CameraRefused, Ptp.RcName(resp.Code));
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
                    State = Strings.Connecting;
                    lv = CanonLiveView.Open(null);
                    Model = lv.Model;
                    Faulted = false;
                    State = Strings.Live;
                    UpdateExposure(lv);
                    var lastExposure = DateTime.UtcNow;
                    while (!Stopping)
                    {
                        if (!requests.IsEmpty) { ApplyRequests(lv); UpdateExposure(lv); }
                        if ((DateTime.UtcNow - lastExposure).TotalMilliseconds > Timing.ExposureRefreshMs)
                        {
                            UpdateExposure(lv); // dial or settings changed on the camera itself
                            lastExposure = DateTime.UtcNow;
                        }
                        var jpeg = lv.ReadJpeg();
                        if (jpeg == null) { Thread.Sleep(Timing.CameraPollIdleMs); continue; }
                        var frame = Decode(jpeg);
                        if (frame != null) Publish(frame);
                    }
                }
                catch (Exception e) // any camera/USB failure: report it and reconnect
                {
                    Faulted = true;
                    State = string.Format(Strings.CameraErrorRetrying, e.Message);
                }
                finally
                {
                    Exposure = null;
                    if (lv != null) lv.Dispose();
                }
                if (!Stopping) Nap(Timing.CameraRetryMs); // camera unplugged, asleep, or busy: try again
            }
        }

        // Decodes into a recycled bitmap; returns null for a corrupt frame (skipped).
        Bitmap Decode(byte[] jpeg)
        {
            try
            {
                using (var ms = new MemoryStream(jpeg))
                using (var img = Image.FromStream(ms, false, false))
                {
                    var bmp = RentFrame(img.Width, img.Height);
                    using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(img, 0, 0);
                    return bmp;
                }
            }
            catch (ArgumentException) { return null; } // not a valid image
        }
    }

    // 3:2 moving test pattern at ~29.97 fps, sized like Canon live view (960x640).
    public sealed class TestPatternSource : LiveSource
    {
        const int W = 960, H = 640, BarWidth = 24, BarStep = 8, FontSize = 40;
        static readonly Color[] Bars = {
            Color.FromArgb(192, 192, 192), Color.FromArgb(192, 192, 0), Color.FromArgb(0, 192, 192), Color.FromArgb(0, 192, 0),
            Color.FromArgb(192, 0, 192), Color.FromArgb(192, 0, 0), Color.FromArgb(0, 0, 192),
        };

        public override string Name { get { return Strings.SourceTestPattern; } }

        protected override void Run()
        {
            Model = Strings.TestPatternModel;
            State = Strings.Live;
            var clock = SystemClock.Instance;
            double next = clock.Now;
            using (var font = new Font(Theme.FontName, FontSize, FontStyle.Bold))
            {
                for (int n = 0; !Stopping; n++)
                {
                    var bmp = RentFrame(W, H);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        int bw = W / Bars.Length;
                        for (int i = 0; i < Bars.Length; i++)
                            using (var b = new SolidBrush(Bars[i])) g.FillRectangle(b, i * bw, 0, bw + 1, H);
                        g.FillRectangle(Brushes.White, (n * BarStep) % W, 0, BarWidth, H);
                        g.DrawString(string.Format(Strings.TestPatternFrame, n), font, Brushes.Black, 40, H - 110);
                    }
                    Publish(bmp);
                    next += 1 / Timing.TestPatternFps;
                    double wait = next - clock.Now;
                    if (wait > 0) clock.Sleep(wait); else next = clock.Now;
                }
            }
        }
    }
}
