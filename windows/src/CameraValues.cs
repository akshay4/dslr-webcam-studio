// Canon EOS property value codes (same encoding over PTP as in Canon's SDK headers).
using System.Collections.Generic;

namespace DslrWebcamStudio
{
    public static class CameraValues
    {
        public const uint IsoAuto = 0;

        // ISO codes the app offers; 0x48 = ISO 100, +8 per stop.
        public static readonly KeyValuePair<uint, string>[] IsoChoices = {
            Kv(0x00, "Auto"), Kv(0x48, "100"), Kv(0x50, "200"), Kv(0x58, "400"), Kv(0x60, "800"),
            Kv(0x68, "1600"), Kv(0x70, "3200"), Kv(0x78, "6400"),
        };

        // Shutter speeds useful for video in live view (1/30 s gathers 4x the light of 1/125 s).
        public static readonly KeyValuePair<uint, string>[] ShutterChoices = {
            Kv(0x60, "1/30"), Kv(0x63, "1/40"), Kv(0x65, "1/50"), Kv(0x68, "1/60"),
            Kv(0x6B, "1/80"), Kv(0x6D, "1/100"), Kv(0x70, "1/125"),
        };

        static readonly Dictionary<uint, string> Iso = new Dictionary<uint, string> {
            { 0x00, "Auto" }, { 0x48, "100" }, { 0x4B, "125" }, { 0x4D, "160" }, { 0x50, "200" }, { 0x53, "250" },
            { 0x55, "320" }, { 0x58, "400" }, { 0x5B, "500" }, { 0x5D, "640" }, { 0x60, "800" }, { 0x63, "1000" },
            { 0x65, "1250" }, { 0x68, "1600" }, { 0x6B, "2000" }, { 0x6D, "2500" }, { 0x70, "3200" }, { 0x73, "4000" },
            { 0x75, "5000" }, { 0x78, "6400" }, { 0x7B, "8000" }, { 0x7D, "10000" }, { 0x80, "12800" },
        };

        static readonly Dictionary<uint, string> Shutter = new Dictionary<uint, string> {
            { 0x0C, "bulb" }, { 0x48, "1/4" }, { 0x4B, "1/5" }, { 0x4D, "1/6" }, { 0x50, "1/8" }, { 0x53, "1/10" },
            { 0x55, "1/13" }, { 0x58, "1/15" }, { 0x5B, "1/20" }, { 0x5D, "1/25" }, { 0x60, "1/30" }, { 0x63, "1/40" },
            { 0x65, "1/50" }, { 0x68, "1/60" }, { 0x6B, "1/80" }, { 0x6D, "1/100" }, { 0x70, "1/125" }, { 0x73, "1/160" },
            { 0x75, "1/200" }, { 0x78, "1/250" }, { 0x7B, "1/320" }, { 0x7D, "1/400" }, { 0x80, "1/500" }, { 0x83, "1/640" },
            { 0x85, "1/800" }, { 0x88, "1/1000" }, { 0x8B, "1/1250" }, { 0x8D, "1/1600" }, { 0x90, "1/2000" }, { 0x93, "1/2500" },
            { 0x95, "1/3200" }, { 0x98, "1/4000" },
        };

        static readonly Dictionary<uint, string> Aperture = new Dictionary<uint, string> {
            { 0x18, "2.0" }, { 0x1B, "2.2" }, { 0x1C, "2.5" }, { 0x1D, "2.5" }, { 0x20, "2.8" }, { 0x23, "3.2" },
            { 0x24, "3.5" }, { 0x25, "3.5" }, { 0x28, "4.0" }, { 0x2B, "4.5" }, { 0x2C, "4.5" }, { 0x2D, "5.0" },
            { 0x30, "5.6" }, { 0x33, "6.3" }, { 0x34, "6.7" }, { 0x35, "7.1" }, { 0x38, "8.0" }, { 0x3B, "9.0" },
            { 0x3C, "9.5" }, { 0x3D, "10" }, { 0x40, "11" }, { 0x43, "13" }, { 0x45, "14" }, { 0x48, "16" },
            { 0x4B, "18" }, { 0x4D, "20" }, { 0x50, "22" },
        };

        static readonly Dictionary<uint, string> Modes = new Dictionary<uint, string> {
            { 0, "P" }, { 1, "Tv" }, { 2, "Av" }, { 3, "M" }, { 4, "Bulb" }, { 5, "A-DEP" }, { 9, "Auto" },
            { 10, "Night portrait" }, { 11, "Sports" }, { 12, "Portrait" }, { 13, "Landscape" }, { 14, "Close-up" },
            { 15, "Flash off" }, { 19, "Creative Auto" }, { 20, "Movie" }, { 22, "A+ (Scene Intelligent Auto)" },
        };

        static KeyValuePair<uint, string> Kv(uint k, string v) { return new KeyValuePair<uint, string>(k, v); }

        static string Name(Dictionary<uint, string> d, uint? v, string prefix)
        {
            if (!v.HasValue) return "?";
            string s;
            return d.TryGetValue(v.Value, out s) ? prefix + s : "0x" + v.Value.ToString("X");
        }

        public static string IsoName(uint? v) { return Name(Iso, v, ""); }
        public static string ShutterName(uint? v) { return Name(Shutter, v, ""); }
        public static string ApertureName(uint? v) { return Name(Aperture, v, "f/"); }
        public static string ModeName(uint? v) { return Name(Modes, v, ""); }

        // ISO can be set in P/Tv/Av/M (and movie mode with manual exposure); shutter only in Tv/M.
        public static bool IsoSettable(uint? mode) { return mode.HasValue && (mode.Value <= 3 || mode.Value == 20); }
        public static bool ShutterSettable(uint? mode) { return mode.HasValue && (mode.Value == 1 || mode.Value == 3 || mode.Value == 20); }
    }
}
