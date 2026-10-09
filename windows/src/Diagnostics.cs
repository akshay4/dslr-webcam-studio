// Camera diagnostics report: what Windows sees, what the camera supports,
// and a short live-view test. Saved as a text file the user can send back.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;

namespace DslrWebcamStudio
{
    public static class Diagnostics
    {
        public static string Run()
        {
            var sb = new StringBuilder();
            Action<string> log = s => sb.AppendLine(s);
            log("DSLR Webcam Studio diagnostics  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log("App " + typeof(Diagnostics).Assembly.GetName().Version + "  OS " + Environment.OSVersion + "  64-bit " + Environment.Is64BitProcess);

            string canonProcs = "";
            foreach (var p in Process.GetProcesses())
                if (p.ProcessName.StartsWith("EWC") || p.ProcessName.IndexOf("EOS", StringComparison.OrdinalIgnoreCase) >= 0)
                    canonProcs += p.ProcessName + " ";
            log("Canon processes running: " + (canonProcs == "" ? "none" : canonProcs));

            log("");
            log("Portable devices:");
            try
            {
                var devs = MtpDevice.Enumerate();
                if (devs.Count == 0) log("  (none)");
                foreach (var d in devs) log("  " + d);
            }
            catch (Exception e) { log("  enumerate failed: " + e.Message); }

            log("");
            log("Live view test:");
            try
            {
                using (var lv = CanonLiveView.Open(s => log("  " + s)))
                {
                    if (lv.DeviceInfo != null)
                    {
                        var ops = new StringBuilder();
                        foreach (var op in lv.DeviceInfo.Operations) if (op >= 0x9100) ops.Append(op.ToString("X4")).Append(' ');
                        log("  Canon ops: " + ops);
                    }
                    var sw = Stopwatch.StartNew();
                    int frames = 0, notReady = 0;
                    double firstMs = -1;
                    string size = "?";
                    long bytes = 0;
                    while (sw.Elapsed.TotalSeconds < 6)
                    {
                        var j = lv.ReadJpeg();
                        if (j == null) { notReady++; System.Threading.Thread.Sleep(5); continue; }
                        if (firstMs < 0)
                        {
                            firstMs = sw.Elapsed.TotalMilliseconds;
                            using (var ms = new MemoryStream(j)) using (var img = Image.FromStream(ms)) size = img.Width + "x" + img.Height;
                        }
                        frames++;
                        bytes += j.Length;
                    }
                    double active = sw.Elapsed.TotalSeconds - Math.Max(0, firstMs) / 1000;
                    log(string.Format("  first frame after {0:F0} ms, frame size {1}", firstMs, size));
                    log(string.Format("  {0} frames, {1:F1} fps, {2} not-ready polls, avg JPEG {3} KB",
                        frames, frames / Math.Max(0.001, active), notReady, frames == 0 ? 0 : bytes / frames / 1024));
                }
                log("  result: OK");
            }
            catch (Exception e)
            {
                log("  result: FAILED - " + e.GetType().Name + ": " + e.Message);
            }
            return sb.ToString();
        }

        public static string SaveReport(string report)
        {
            string dir = Path.GetDirectoryName(StreamSettings.DefaultPath);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "diagnostics_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            File.WriteAllText(path, report);
            return path;
        }
    }
}
