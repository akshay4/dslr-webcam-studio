// Fixed-rate output pacing ("Target Streaming Framerate").
// Output ticks follow a fixed timeline. Each tick sends the newest camera frame,
// so a faster source drops frames and a slower source repeats the last one.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DslrWebcamStudio
{
    public interface IClock
    {
        double Now { get; }
        void Sleep(double seconds);
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        readonly Stopwatch sw = Stopwatch.StartNew();

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);

        SystemClock() { timeBeginPeriod(1); } // default Windows sleep granularity is ~15.6 ms

        public double Now { get { return sw.Elapsed.TotalSeconds; } }

        public void Sleep(double seconds)
        {
            // Coarse sleep, then a short spin for the last ~1.5 ms.
            double end = Now + seconds;
            double coarse = seconds - 0.0015;
            if (coarse > 0) Thread.Sleep(TimeSpan.FromSeconds(coarse));
            while (Now < end) Thread.SpinWait(50);
        }
    }

    public sealed class FramePacer
    {
        readonly IClock clock;
        double next = double.NaN;
        public readonly double Interval;
        public int LateTicks;

        public FramePacer(int fps, IClock clock)
        {
            if (fps <= 0) throw new ArgumentException("fps must be positive");
            Interval = 1.0 / fps;
            this.clock = clock;
        }

        public void Wait()
        {
            double now = clock.Now;
            if (double.IsNaN(next)) { next = now + Interval; return; }
            if (now >= next)
            {
                // Fell behind (slow frame or a stall). Skip the missed slots and wait for the
                // next slot on the same timeline, so two frames never go out back to back.
                LateTicks++;
                next += (Math.Floor((now - next) / Interval) + 1) * Interval;
            }
            clock.Sleep(next - now);
            next += Interval;
        }
    }

    // Events per second over the last second, for the status bar. Counting events in a window
    // (instead of averaging 1/interval) doesn't spike when two events land close together.
    public sealed class RateMeter
    {
        const double Window = 1.0;
        readonly IClock clock;
        readonly Queue<double> stamps = new Queue<double>();
        readonly object gate = new object();
        double last;

        public RateMeter(IClock clock) { this.clock = clock; }

        public void Tick()
        {
            lock (gate)
            {
                double now = clock.Now;
                last = now;
                stamps.Enqueue(now);
                Trim(now);
            }
        }

        public double Current
        {
            get
            {
                lock (gate)
                {
                    double now = clock.Now;
                    Trim(now);
                    if (stamps.Count < 2) return 0;
                    return (stamps.Count - 1) / (last - stamps.Peek());
                }
            }
        }

        void Trim(double now)
        {
            while (stamps.Count > 0 && now - stamps.Peek() > Window) stamps.Dequeue();
        }
    }
}
