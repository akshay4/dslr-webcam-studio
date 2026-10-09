// Source -> scale/letterbox -> fixed size and fixed rate.
// Produces output frames at exactly Settings.Width x Height and Settings.Fps,
// the format a webcam consumer negotiates. Frames go to the preview now and
// to a virtual-camera sink later.
//
// Each tick first sends the frame composed during the previous slot, then
// composes the newest camera frame for the next one. Send timing therefore
// doesn't depend on how long scaling takes (cost: one slot of latency).
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;

namespace DslrWebcamStudio
{
    public sealed class OutputEngine
    {
        readonly LiveSource source;
        public readonly StreamSettings Settings;
        readonly object frontGate = new object();
        Bitmap front, pending;
        bool pendingReady;
        Thread thread;
        volatile bool stopping;
        FramePacer pacer;
        readonly RateMeter meter = new RateMeter(SystemClock.Instance);
        readonly Scaler scaler = new Scaler();
        readonly Enhancer enhancer = new Enhancer();

        public int Repeated, Dropped, Frames;
        public double ComposeMs; // smoothed time to clean up and scale one frame
        public int LateTicks { get { return pacer == null ? 0 : pacer.LateTicks; } }
        public event Action FrameReady;
        // Receives every sent frame (the virtual camera); called on the output thread.
        public VirtualCamera.Writer Sink;
        // Read on every frame, so changing them takes effect without restarting the output.
        public volatile bool FlipHorizontal, FlipVertical;
        public volatile int NoiseReduction, Sharpness;

        public OutputEngine(LiveSource source, StreamSettings settings)
        {
            this.source = source;
            Settings = settings;
            FlipHorizontal = settings.FlipHorizontal;
            FlipVertical = settings.FlipVertical;
            NoiseReduction = settings.NoiseReduction;
            Sharpness = settings.Sharpness;
        }

        int LiveOptionsKey { get { return (FlipHorizontal ? 1 : 0) | (FlipVertical ? 2 : 0) | (NoiseReduction << 2) | (Sharpness << 4); } }

        public double OutputFps { get { return meter.Current; } }

        public void Start()
        {
            front = NewFrame();
            pending = NewFrame();
            stopping = false;
            thread = new Thread(Run) { IsBackground = true, Name = "output", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            if (thread != null) { thread.Join(3000); thread = null; }
            lock (frontGate)
            {
                if (front != null) front.Dispose();
                if (pending != null) pending.Dispose();
                front = pending = null;
            }
            enhancer.Dispose();
        }

        // Runs draw() on the frame most recently sent (under lock).
        public bool WithFrame(Action<Bitmap> draw)
        {
            lock (frontGate)
            {
                if (front == null || Frames == 0) return false;
                draw(front);
                return true;
            }
        }

        Bitmap NewFrame()
        {
            var b = new Bitmap(Settings.Width, Settings.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b)) g.Clear(Color.Black);
            return b;
        }

        void Run()
        {
            pacer = new FramePacer(Settings.Fps, SystemClock.Instance);
            int lastSeq = 0;
            bool started = false;
            int lastOptions = LiveOptionsKey;
            while (!stopping)
            {
                pacer.Wait();
                if (stopping) break;

                // 1. Send on the slot.
                if (pendingReady)
                {
                    lock (frontGate) { var t = front; front = pending; pending = t; }
                    pendingReady = false;
                    started = true;
                }
                else if (started)
                {
                    Repeated++; // camera slower than the target rate: resend the last frame
                }
                if (started)
                {
                    var sink = Sink;
                    if (sink != null) lock (frontGate) { if (front != null) sink.Write(front, Settings.Fps); }
                    Frames++;
                    meter.Tick();
                    var h = FrameReady;
                    if (h != null) h();
                }

                // 2. Prepare the next slot from the newest camera frame.
                // Changing flip or cleanup recomposes right away, even if the camera has no new frame yet.
                int options = LiveOptionsKey;
                if (source.Seq != lastSeq || options != lastOptions)
                {
                    lastOptions = options;
                    double t0 = SystemClock.Instance.Now;
                    int drawn = source.WithLatest(Compose);
                    double ms = (SystemClock.Instance.Now - t0) * 1000;
                    if (drawn != 0)
                    {
                        if (lastSeq != 0) Dropped += Math.Max(0, drawn - lastSeq - 1);
                        lastSeq = drawn;
                        pendingReady = true;
                        ComposeMs = ComposeMs == 0 ? ms : 0.9 * ComposeMs + 0.1 * ms;
                    }
                }
            }
        }

        void Compose(Bitmap src)
        {
            int W = Settings.Width, H = Settings.Height;
            bool fh = FlipHorizontal, fv = FlipVertical;
            src = enhancer.Process(src, NoiseReduction, Sharpness);
            if (Settings.Fit == FitMode.Fill)
                scaler.Draw(src, Geometry.CropRect(src.Width, src.Height, W, H), pending, new Rectangle(0, 0, W, H), fh, fv);
            else
                scaler.Draw(src, new Rectangle(0, 0, src.Width, src.Height), pending, Geometry.FitRect(src.Width, src.Height, W, H), fh, fv);
        }
    }
}
