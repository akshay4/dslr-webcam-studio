// Camera diagnostics report: what Windows sees, what the camera supports,
// and a short live-view test. Saved as a text file the user can send back.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;

namespace DslrWebcamStudio
{
    public static class Diagnostics
    {
        // Report text (English, for bug reports) and limits.
        const string Header = "{0} diagnostics  {1:yyyy-MM-dd HH:mm:ss}";
        const string AppLine = "App {0}  OS {1}  64-bit {2}";
        const string CanonProcesses = "Canon processes running: {0}";
        const string PortableDevices = "Portable devices:";
        const string LiveViewTest = "Live view test:";
        const string None = "none", NoneListed = "  (none)", Indent = "  ";
        const string EnumerateFailed = "  enumerate failed: {0}";
        const string CanonOps = "  Canon ops: {0}";
        const string FirstFrame = "  first frame after {0:F0} ms, frame size {1}";
        const string FrameStats = "  {0} frames, {1:F1} fps, {2} not-ready polls, avg JPEG {3} KB";
        const string ResultOk = "  result: OK";
        const string ResultFailed = "  result: FAILED - {0}: {1}";
        const string FilePrefix = "diagnostics_", FileTime = "yyyyMMdd_HHmmss", FileExt = ".txt";
        const ushort FirstCanonOp = 0x9100;
        const double TestSeconds = 6;

        public static string Run()
        {
            var sb = new StringBuilder();
            Action<string> log = s => sb.AppendLine(s);
            log(string.Format(Header, Strings.AppName, DateTime.Now));
            log(string.Format(AppLine, typeof(Diagnostics).Assembly.GetName().Version, Environment.OSVersion, Environment.Is64BitProcess));

            string canonProcs = string.Join(" ", CanonSoftware.Running());
            log(string.Format(CanonProcesses, canonProcs == "" ? None : canonProcs));

            log("");
            log(PortableDevices);
            try
            {
                var devs = MtpDevice.Enumerate();
                if (devs.Count == 0) log(NoneListed);
                foreach (var d in devs) log(Indent + d);
            }
            catch (Exception e) { log(string.Format(EnumerateFailed, e.Message)); }

            log("");
            log(LiveViewTest);
            try
            {
                using (var lv = CanonLiveView.Open(s => log(Indent + s)))
                {
                    if (lv.DeviceInfo != null)
                    {
                        var ops = new StringBuilder();
                        foreach (var op in lv.DeviceInfo.Operations) if (op >= FirstCanonOp) ops.Append(op.ToString("X4")).Append(' ');
                        log(string.Format(CanonOps, ops));
                    }
                    var sw = Stopwatch.StartNew();
                    int frames = 0, notReady = 0;
                    double firstMs = -1;
                    string size = "?";
                    long bytes = 0;
                    while (sw.Elapsed.TotalSeconds < TestSeconds)
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
                    log(string.Format(FirstFrame, firstMs, size));
                    log(string.Format(FrameStats,
                        frames, frames / Math.Max(0.001, active), notReady, frames == 0 ? 0 : bytes / frames / 1024));
                }
                log(ResultOk);
            }
            catch (Exception e)
            {
                log(string.Format(ResultFailed, e.GetType().Name, e.Message));
            }
            return sb.ToString();
        }

        public static string SaveReport(string report)
        {
            string dir = Path.GetDirectoryName(StreamSettings.DefaultPath);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, FilePrefix + DateTime.Now.ToString(FileTime) + FileExt);
            File.WriteAllText(path, report);
            return path;
        }
    }

    // Canon's own apps (EOS Utility, EOS Webcam Utility and its EWC services) talk to the camera
    // too and hold it while they run.
    public static class CanonSoftware
    {
        const string ServicePrefix = "EWC", CanonName = "EOS";

        public static List<string> Running()
        {
            var names = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName;
                    if ((n.StartsWith(ServicePrefix) || n.IndexOf(CanonName, StringComparison.OrdinalIgnoreCase) >= 0) && !names.Contains(n))
                        names.Add(n);
                }
                catch (InvalidOperationException) { } // exited meanwhile
                finally { p.Dispose(); }
            }
            names.Sort();
            return names;
        }

        // Appended to camera errors when Canon software may be the cause.
        public static string Hint()
        {
            var r = Running();
            return r.Count == 0 ? "" : string.Format(Strings.CanonSoftwareHint, string.Join(", ", r));
        }
    }
}
