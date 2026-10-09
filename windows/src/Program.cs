using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("DSLR Webcam Studio")]
[assembly: AssemblyProduct("DSLR Webcam Studio")]
[assembly: AssemblyDescription("Use a Canon EOS camera as a webcam: live preview, resolution, framerate and image controls.")]

namespace DslrWebcamStudio
{
    static class Program
    {
        // DSLRWebcamStudio.exe                       -> app
        // DSLRWebcamStudio.exe --diag                -> write diagnostics report, print its path
        // DSLRWebcamStudio.exe --selftest <dir> [camera]  -> run checks, write <dir>\selftest.txt + PNGs
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "--diag")
            {
                string path = Diagnostics.SaveReport(Diagnostics.Run());
                Console.WriteLine(path);
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--selftest")
                return SelfTest.Run(args[1], args.Length >= 3 && args[2] == "camera");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(StreamSettings.DefaultPath));
            return 0;
        }
    }

    static class SelfTest
    {
        static StringBuilder log;
        static int failures;

        static void Check(bool ok, string what)
        {
            log.AppendLine((ok ? "PASS " : "FAIL ") + what);
            if (!ok) failures++;
        }

        public static int Run(string dir, bool withCamera)
        {
            Directory.CreateDirectory(dir);
            log = new StringBuilder();
            failures = 0;
            try
            {
                UnitChecks(dir);
                PipelineCheck(dir, new TestPatternSource(), "test");
                if (withCamera) PipelineCheck(dir, new CanonSource(), "camera");
                UiCheck(dir);
            }
            catch (Exception e) { Check(false, "unhandled: " + e); }
            log.AppendLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            File.WriteAllText(Path.Combine(dir, "selftest.txt"), log.ToString());
            return failures == 0 ? 0 : 1;
        }

        static void UnitChecks(string dir)
        {
            int w, h;
            StreamSettings.ResolutionToSize(360, out w, out h); Check(w == 640 && h == 360, "360 -> 640x360");
            StreamSettings.ResolutionToSize(720, out w, out h); Check(w == 1280 && h == 720, "720 -> 1280x720");
            StreamSettings.ResolutionToSize(999, out w, out h); Check(w == 1920 && h == 1080, "unknown -> 1080p (Canon default)");

            Check(Throws(() => new StreamSettings().WithFps(60)), "fps 60 rejected");
            Check(Throws(() => new StreamSettings().WithResolution(480)), "480p rejected");

            string ini = Path.Combine(dir, "test_config.ini");
            File.WriteAllText(ini, "[Other]\nx=1\n[Global]\nLogLevel=3\nStreamFps=59\n");
            Check(StreamSettings.Load(ini).Equals(new StreamSettings()), "invalid ini values fall back to defaults");
            var s = new StreamSettings().WithResolution(1080).WithFps(25).WithFit(FitMode.Fill);
            s.Save(ini);
            string text = File.ReadAllText(ini);
            Check(StreamSettings.Load(ini).Equals(s), "ini round trip");
            Check(text.Contains("StreamWidth=1920") && text.Contains("StreamHeight=1080") && text.Contains("StreamFps=25"), "Canon-style keys written");
            Check(text.Contains("LogLevel=3") && text.Contains("[Other]") && text.Contains("x=1"), "other ini keys preserved");
            File.Delete(ini);

            // Canon live view is 960x640 (3:2) on the 700D
            Check(Geometry.FitRect(960, 640, 1280, 720) == new Rectangle(100, 0, 1080, 720), "fit 960x640 into 720p");
            Check(Geometry.FitRect(960, 640, 1920, 1080) == new Rectangle(150, 0, 1620, 1080), "fit into 1080p");
            Check(Geometry.FitRect(960, 640, 640, 360) == new Rectangle(50, 0, 540, 360), "fit into 360p");
            Check(Geometry.CropRect(960, 640, 1280, 720) == new Rectangle(0, 50, 960, 540), "fill crop 960x640 to 16:9");
            Check(Geometry.CropRect(2000, 720, 1280, 720) == new Rectangle(360, 0, 1280, 720), "fill crop wide source");

            var clk = new FakeClock();
            var p = new FramePacer(30, clk);
            p.Wait();
            double t0 = clk.Now;
            for (int i = 0; i < 300; i++) { clk.T += (i % 2 == 0) ? 0.013 : 0.001; p.Wait(); }
            Check(Math.Abs(clk.Now - t0 - 10.0) < 1e-6 && p.LateTicks == 0, "pacer: 300 ticks at 30 fps = 10 s, no drift");
            clk.T += 0.5; p.Wait();
            double before = clk.Now; p.Wait();
            Check(p.LateTicks == 1 && Math.Abs(clk.Now - before - 1 / 30.0) < 1e-9, "pacer: resync after stall without burst");

            var mclk = new FakeClock();
            var meter = new RateMeter(mclk);
            for (int i = 0; i < 75; i++) { meter.Tick(); mclk.T += 1 / 25.0; }
            mclk.T -= 1 / 25.0;
            Check(Math.Abs(meter.Current - 25) < 0.01, "rate meter: 25 fps reads " + meter.Current.ToString("F2"));
            mclk.T += 2; Check(meter.Current == 0, "rate meter: 0 after ticks stop");

            using (var src = new Bitmap(4, 4, PixelFormat.Format32bppPArgb))
            using (var dst = new Bitmap(16, 9, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(src)) g.Clear(Color.FromArgb(255, 200, 100, 50));
                var r = Geometry.FitRect(4, 4, 16, 9);
                new Scaler().Draw(src, new Rectangle(0, 0, 4, 4), dst, r);
                var inside = dst.GetPixel(r.X + r.Width / 2, r.Y + r.Height / 2);
                var bar = dst.GetPixel(0, 4);
                Check(inside.R == 200 && inside.G == 100 && inside.B == 50 && inside.A == 255, "scaler keeps color (" + inside + ")");
                Check(bar.R == 0 && bar.G == 0 && bar.B == 0 && bar.A == 255, "scaler paints side bars black");
            }

            // Mirror / flip: left half red, right half blue; top half white, bottom half black (in green channel).
            using (var src = new Bitmap(8, 8, PixelFormat.Format32bppPArgb))
            using (var dst = new Bitmap(8, 8, PixelFormat.Format32bppPArgb))
            {
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        src.SetPixel(x, y, Color.FromArgb(255, x < 4 ? 255 : 0, y < 4 ? 255 : 0, x < 4 ? 0 : 255));
                var sc = new Scaler();
                sc.Draw(src, new Rectangle(0, 0, 8, 8), dst, new Rectangle(0, 0, 8, 8), true, false);
                Check(dst.GetPixel(0, 0).B == 255 && dst.GetPixel(7, 0).R == 255, "mirror swaps left and right");
                sc.Draw(src, new Rectangle(0, 0, 8, 8), dst, new Rectangle(0, 0, 8, 8), false, true);
                Check(dst.GetPixel(0, 0).G == 0 && dst.GetPixel(0, 7).G == 255, "flip swaps top and bottom");
                sc.Draw(src, new Rectangle(0, 0, 8, 8), dst, new Rectangle(0, 0, 8, 8), false, false);
                Check(dst.GetPixel(0, 0).R == 255 && dst.GetPixel(0, 0).G == 255 && dst.GetPixel(7, 7).B == 255, "no flip keeps orientation");
            }

            // Noise reduction: static noisy gray frames get cleaner; a sudden big change passes straight through.
            {
                var rnd = new Random(1);
                var en = new Enhancer();
                Bitmap outBmp = null;
                for (int f = 0; f < 20; f++)
                {
                    var frame = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
                    for (int y = 0; y < 64; y++)
                        for (int x = 0; x < 64; x++)
                        {
                            int v = Math.Max(0, Math.Min(255, 128 + (int)(rnd.NextDouble() * 30 - 15)));
                            frame.SetPixel(x, y, Color.FromArgb(255, v, v, v));
                        }
                    outBmp = en.Process(frame, 3, 0);
                }
                double inStd = 30 / Math.Sqrt(12), outStd = StdDevGreen(outBmp);
                Check(outStd < inStd * 0.6, "noise reduction: static noise std " + inStd.ToString("F1") + " -> " + outStd.ToString("F1"));
                using (var white = new Bitmap(64, 64, PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(white)) g.Clear(Color.White);
                    var o = en.Process(white, 3, 0);
                    Check(o.GetPixel(32, 32).G > 245, "noise reduction: motion passes through without smearing (" + o.GetPixel(32, 32).G + ")");
                }
                en.Dispose();
            }

            // Camera events: property-changed records update the property table.
            {
                var ev = new List<byte>();
                ev.AddRange(BitConverter.GetBytes(16)); ev.AddRange(BitConverter.GetBytes(0xC189)); ev.AddRange(BitConverter.GetBytes(0xD103)); ev.AddRange(BitConverter.GetBytes(0x60));
                ev.AddRange(BitConverter.GetBytes(12)); ev.AddRange(BitConverter.GetBytes(0xC18A)); ev.AddRange(BitConverter.GetBytes(0xD103));
                ev.AddRange(BitConverter.GetBytes(8)); ev.AddRange(BitConverter.GetBytes(0));
                var props = new Dictionary<uint, uint>();
                CanonLiveView.ParseEvents(ev.ToArray(), props);
                Check(props.Count == 1 && props[0xD103] == 0x60 && CameraValues.IsoName(0x60) == "800", "event parse: ISO 800");
            }

            var jpeg = new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 };
            var block = new List<byte>();
            block.AddRange(BitConverter.GetBytes(8 + 4)); block.AddRange(BitConverter.GetBytes(5)); block.AddRange(new byte[4]);
            block.AddRange(BitConverter.GetBytes(8 + jpeg.Length)); block.AddRange(BitConverter.GetBytes(1)); block.AddRange(jpeg);
            Check(Same(CanonLiveView.ExtractJpeg(block.ToArray()), jpeg), "viewfinder block parse (type 1 = JPEG)");
            Check(Same(CanonLiveView.ExtractJpeg(jpeg), jpeg), "viewfinder bare JPEG fallback");
        }

        static void PipelineCheck(string dir, LiveSource src, string tag)
        {
            src.Start();
            try
            {
                if (tag == "camera")
                {
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (src.Seq == 0 && DateTime.UtcNow < deadline) Thread.Sleep(50);
                    Check(src.Seq > 0, "camera delivered frames (" + src.State + ")");
                    if (src.Seq == 0) return;
                }
                foreach (var cfg in new[] {
                    new StreamSettings(1280, 720, 30, FitMode.Fit),
                    new StreamSettings(1920, 1080, 25, FitMode.Fill),
                    new StreamSettings(640, 360, 15, FitMode.Fit) })
                {
                    var eng = new OutputEngine(src, cfg);
                    eng.Start();
                    Thread.Sleep(3000);
                    double fps = eng.OutputFps, camFps = src.Meter.Current;
                    int fw = 0, fh = 0;
                    string png = Path.Combine(dir, tag + "_" + cfg.Width + "x" + cfg.Height + "_" + cfg.Fps + ".png");
                    eng.WithFrame(f => { fw = f.Width; fh = f.Height; f.Save(png, ImageFormat.Png); });
                    eng.Stop();
                    Check(fw == cfg.Width && fh == cfg.Height, tag + " " + cfg + ": output frame is " + fw + "x" + fh);
                    Check(Math.Abs(fps - cfg.Fps) < 1.0,
                          string.Format("{0} {1}: output {2:F2} fps (camera {3:F1} fps, repeated {4}, dropped {5}, late {6}, compose {7:F1} ms)", tag, cfg, fps, camFps, eng.Repeated, eng.Dropped, eng.LateTicks, eng.ComposeMs));
                }
            }
            finally { src.Stop(); }
        }

        static void UiCheck(string dir)
        {
            Application.EnableVisualStyles();
            using (var form = new MainForm(Path.Combine(dir, "ui_config.ini")))
            {
                form.SelectSource(1); // test pattern
                var t = new System.Windows.Forms.Timer { Interval = 2500 };
                t.Tick += delegate { t.Stop(); form.CaptureWindow(Path.Combine(dir, "ui.png")); form.Close(); };
                t.Start();
                Application.Run(form);
            }
            Check(File.Exists(Path.Combine(dir, "ui.png")), "UI renders (ui.png)");
        }

        static double StdDevGreen(Bitmap b)
        {
            double s = 0, s2 = 0; int n = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++) { int v = b.GetPixel(x, y).G; s += v; s2 += v * v; n++; }
            double m = s / n;
            return Math.Sqrt(Math.Max(0, s2 / n - m * m));
        }

        static bool Throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }

        static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        sealed class FakeClock : IClock
        {
            public double T;
            public double Now { get { return T; } }
            public void Sleep(double s) { T += s; }
        }
    }
}
