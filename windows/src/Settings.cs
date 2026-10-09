// Streaming output settings: allow-lists, resolution mapping, ini persistence.
// Same model as EOS Webcam Utility Pro (QH=[360,720,1080] + mapResolutionToSize,
// config.ini [Global] StreamWidth/StreamHeight/StreamFps).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DslrWebcamStudio
{
    public enum FitMode { Fit, Fill }

    public sealed class StreamSettings
    {
        public static readonly int[] Resolutions = { 360, 720, 1080 };
        // Canon DSLR live view tops out around 17-30 fps over USB 2.0, so 60 would only repeat frames.
        public static readonly int[] FpsChoices = { 15, 24, 25, 30 };

        public const int DefaultResolution = 720;
        public const int DefaultFps = 30;

        public static readonly string[] Levels = { "Off", "Low", "Medium", "High" };
        public const int DefaultNoiseReduction = 2, DefaultSharpness = 1;

        public readonly int Width;
        public readonly int Height;
        public readonly int Fps;
        public readonly FitMode Fit;
        public readonly bool FlipHorizontal; // mirror left-right
        public readonly bool FlipVertical;   // upside-down
        public readonly int NoiseReduction;  // 0..3 (Off..High)
        public readonly int Sharpness;       // 0..3 (Off..High)

        public StreamSettings() : this(1280, 720, DefaultFps, FitMode.Fit) { }

        public StreamSettings(int width, int height, int fps, FitMode fit, bool flipH = false, bool flipV = false,
                              int noiseReduction = DefaultNoiseReduction, int sharpness = DefaultSharpness)
        {
            Width = width; Height = height; Fps = fps; Fit = fit;
            FlipHorizontal = flipH; FlipVertical = flipV;
            NoiseReduction = Clamp(noiseReduction); Sharpness = Clamp(sharpness);
        }

        static int Clamp(int level) { return Math.Max(0, Math.Min(Levels.Length - 1, level)); }

        public int Resolution { get { return SizeToResolution(Width, Height); } }

        public static void ResolutionToSize(int resolution, out int w, out int h)
        {
            switch (resolution)
            {
                case 360: w = 640; h = 360; break;
                case 720: w = 1280; h = 720; break;
                default: w = 1920; h = 1080; break; // Canon's default branch
            }
        }

        public static int SizeToResolution(int w, int h)
        {
            if (w == 640 && h == 360) return 360;
            if (w == 1280 && h == 720) return 720;
            return 1080;
        }

        static bool IsAllowedSize(int w, int h)
        {
            foreach (int r in Resolutions)
            {
                int rw, rh;
                ResolutionToSize(r, out rw, out rh);
                if (rw == w && rh == h) return true;
            }
            return false;
        }

        public StreamSettings WithResolution(int resolution)
        {
            if (Array.IndexOf(Resolutions, resolution) < 0)
                throw new ArgumentException("unsupported resolution " + resolution);
            int w, h;
            ResolutionToSize(resolution, out w, out h);
            return new StreamSettings(w, h, Fps, Fit, FlipHorizontal, FlipVertical, NoiseReduction, Sharpness);
        }

        public StreamSettings WithFps(int fps)
        {
            if (Array.IndexOf(FpsChoices, fps) < 0)
                throw new ArgumentException("unsupported fps " + fps);
            return new StreamSettings(Width, Height, fps, Fit, FlipHorizontal, FlipVertical, NoiseReduction, Sharpness);
        }

        public StreamSettings WithFit(FitMode fit)
        {
            return new StreamSettings(Width, Height, Fps, fit, FlipHorizontal, FlipVertical, NoiseReduction, Sharpness);
        }

        public StreamSettings WithFlip(bool horizontal, bool vertical)
        {
            return new StreamSettings(Width, Height, Fps, Fit, horizontal, vertical, NoiseReduction, Sharpness);
        }

        public StreamSettings WithEnhancement(int noiseReduction, int sharpness)
        {
            return new StreamSettings(Width, Height, Fps, Fit, FlipHorizontal, FlipVertical, noiseReduction, sharpness);
        }

        // True when only options that the running output can change in place differ (flip, enhancement).
        public bool SameFormat(StreamSettings o)
        {
            return o.Width == Width && o.Height == Height && o.Fps == Fps && o.Fit == Fit;
        }

        public override bool Equals(object obj)
        {
            var o = obj as StreamSettings;
            return o != null && o.Width == Width && o.Height == Height && o.Fps == Fps && o.Fit == Fit
                && o.FlipHorizontal == FlipHorizontal && o.FlipVertical == FlipVertical
                && o.NoiseReduction == NoiseReduction && o.Sharpness == Sharpness;
        }

        public override int GetHashCode() { return Width ^ (Height << 12) ^ (Fps << 24) ^ (int)Fit ^ (FlipHorizontal ? 1 << 28 : 0) ^ (FlipVertical ? 1 << 29 : 0) ^ (NoiseReduction << 20) ^ (Sharpness << 22); }

        public override string ToString() { return Width + "x" + Height + " @ " + Fps + " fps (" + Fit + (FlipHorizontal ? ", mirrored" : "") + (FlipVertical ? ", flipped" : "") + ")"; }

        // ---- persistence -------------------------------------------------

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "DSLR Webcam Studio", "config.ini");
            }
        }

        // Missing or invalid values fall back to their defaults.
        public static StreamSettings Load(string path)
        {
            var g = Ini.Read(path, "Global");
            int w = GetInt(g, "StreamWidth", 0), h = GetInt(g, "StreamHeight", 0);
            if (!IsAllowedSize(w, h)) ResolutionToSize(DefaultResolution, out w, out h);
            int fps = GetInt(g, "StreamFps", DefaultFps);
            if (Array.IndexOf(FpsChoices, fps) < 0) fps = DefaultFps;
            string fitText;
            FitMode fit = FitMode.Fit;
            if (g.TryGetValue("FitMode", out fitText) && fitText.Trim().ToLowerInvariant() == "fill") fit = FitMode.Fill;
            return new StreamSettings(w, h, fps, fit, GetInt(g, "FlipHorizontal", 0) == 1, GetInt(g, "FlipVertical", 0) == 1,
                                      GetInt(g, "NoiseReduction", DefaultNoiseReduction), GetInt(g, "Sharpness", DefaultSharpness));
        }

        public void Save(string path)
        {
            Ini.Write(path, "Global", new Dictionary<string, string> {
                { "StreamWidth", Width.ToString() },
                { "StreamHeight", Height.ToString() },
                { "StreamFps", Fps.ToString() },
                { "FitMode", Fit == FitMode.Fill ? "fill" : "fit" },
                { "FlipHorizontal", FlipHorizontal ? "1" : "0" },
                { "FlipVertical", FlipVertical ? "1" : "0" },
                { "NoiseReduction", NoiseReduction.ToString() },
                { "Sharpness", Sharpness.ToString() },
            });
        }

        static int GetInt(Dictionary<string, string> d, string key, int def)
        {
            string s;
            int v;
            return d.TryGetValue(key, out s) && int.TryParse(s.Trim(), out v) ? v : def;
        }
    }

    // Minimal ini reader/writer that keeps unrelated sections and keys intact.
    static class Ini
    {
        public static Dictionary<string, string> Read(string path, string section)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return result;
            bool inSection = false;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inSection = string.Equals(line.Substring(1, line.Length - 2).Trim(), section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (inSection && eq > 0 && !line.StartsWith(";") && !line.StartsWith("#"))
                    result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return result;
        }

        public static void Write(string path, string section, Dictionary<string, string> values)
        {
            var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
            var pending = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            int sectionEnd = -1;
            bool inSection = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    if (inSection) { sectionEnd = i; inSection = false; }
                    if (string.Equals(line.Substring(1, line.Length - 2).Trim(), section, StringComparison.OrdinalIgnoreCase))
                    { inSection = true; sectionEnd = i + 1; }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (inSection && eq > 0)
                {
                    var key = line.Substring(0, eq).Trim();
                    string v;
                    if (pending.TryGetValue(key, out v)) { lines[i] = key + "=" + v; pending.Remove(key); }
                    sectionEnd = i + 1;
                }
            }
            if (sectionEnd < 0)
            {
                lines.Add("[" + section + "]");
                sectionEnd = lines.Count;
            }
            foreach (var kv in values)
                if (pending.ContainsKey(kv.Key)) lines.Insert(sectionEnd++, kv.Key + "=" + kv.Value);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines.ToArray(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
    }
}
