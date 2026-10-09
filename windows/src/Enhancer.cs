// Live-view cleanup, run once per camera frame at the camera's native size (960x640):
//  1. Motion-adaptive temporal noise reduction. Each pixel is blended with its running
//     average. Motion is detected from the brightness change averaged over a 3x3
//     neighbourhood: random sensor noise (std ~9 per channel on this camera in low light)
//     cancels out in that average, but real movement doesn't. Static areas are averaged
//     over several frames; moving areas take the new frame directly, so motion doesn't smear.
//  2. Unsharp mask (3x3) with a small dead zone, so leftover noise isn't sharpened.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DslrWebcamStudio
{
    public sealed class Enhancer : IDisposable
    {
        int w, h;
        int[] cur, acc, outPx;     // packed 0xAARRGGBB
        int[] diff;                // signed brightness change, cur - acc
        bool haveAcc;
        Bitmap output;

        //                         Off   Low  Medium  High
        static readonly int[] NrBase = { 256, 96, 64, 40 };    // weight (of 256) given to the new frame when nothing moved
        static readonly int[] NrThreshold = { 1, 6, 9, 12 };   // 3x3-averaged brightness change treated as real motion
        static readonly int[] SharpAmount = { 0, 128, 256, 420 }; // unsharp strength (256 = 1.0)

        // Returns src unchanged when both are off; otherwise an internal bitmap (valid until the next call).
        public Bitmap Process(Bitmap src, int noiseLevel, int sharpLevel)
        {
            if (noiseLevel <= 0 && sharpLevel <= 0) { haveAcc = false; return src; }
            Ensure(src.Width, src.Height);
            Read(src, cur);

            int[] img = cur;
            if (noiseLevel > 0)
            {
                Temporal(NrBase[noiseLevel], NrThreshold[noiseLevel]);
                img = acc;
            }
            else haveAcc = false;

            if (sharpLevel > 0)
            {
                Sharpen(img, outPx, SharpAmount[sharpLevel]);
                img = outPx;
            }
            Write(img, output);
            return output;
        }

        void Ensure(int width, int height)
        {
            if (width == w && height == h && output != null) return;
            w = width; h = height;
            cur = new int[w * h]; acc = new int[w * h]; outPx = new int[w * h]; diff = new int[w * h];
            haveAcc = false;
            if (output != null) output.Dispose();
            output = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        }

        static int Luma(int p) { return (2 * ((p >> 16) & 255) + 5 * ((p >> 8) & 255) + (p & 255)) >> 3; }

        void Temporal(int baseW, int thr)
        {
            if (!haveAcc) { Array.Copy(cur, acc, cur.Length); haveAcc = true; return; }
            int[] c = cur, a = acc, df = diff;
            int width = w, height = h;
            Parallel.For(0, height, y =>
            {
                for (int i = y * width, end = i + width; i < end; i++) df[i] = Luma(c[i]) - Luma(a[i]);
            });
            Parallel.For(0, height, y =>
            {
                int ym = Math.Max(0, y - 1) * width, y0 = y * width, yp = Math.Min(height - 1, y + 1) * width;
                for (int x = 0; x < width; x++)
                {
                    int xm = Math.Max(0, x - 1), xp = Math.Min(width - 1, x + 1);
                    int m = Math.Abs(df[ym + xm] + df[ym + x] + df[ym + xp] + df[y0 + xm] + df[y0 + x] + df[y0 + xp]
                                     + df[yp + xm] + df[yp + x] + df[yp + xp]) / 9;
                    int k = m >= thr ? 256 : baseW + (256 - baseW) * m / thr;
                    int i = y0 + x, p = a[i], n = c[i];
                    int pr = (p >> 16) & 255, pg = (p >> 8) & 255, pb = p & 255;
                    pr += ((((n >> 16) & 255) - pr) * k + 128) >> 8;
                    pg += ((((n >> 8) & 255) - pg) * k + 128) >> 8;
                    pb += (((n & 255) - pb) * k + 128) >> 8;
                    a[i] = unchecked((int)0xFF000000) | (pr << 16) | (pg << 8) | pb;
                }
            });
        }

        void Sharpen(int[] src, int[] dst, int amount)
        {
            int width = w, height = h;
            Parallel.For(0, height, y =>
            {
                int ym = Math.Max(0, y - 1) * width, y0 = y * width, yp = Math.Min(height - 1, y + 1) * width;
                for (int x = 0; x < width; x++)
                {
                    int xm = Math.Max(0, x - 1), xp = Math.Min(width - 1, x + 1);
                    int c = src[y0 + x];
                    int result = unchecked((int)0xFF000000);
                    for (int sh = 0; sh <= 16; sh += 8)
                    {
                        // 3x3 Gaussian blur [1 2 1; 2 4 2; 1 2 1] / 16
                        int blur = (Ch(src[ym + xm], sh) + 2 * Ch(src[ym + x], sh) + Ch(src[ym + xp], sh)
                                  + 2 * Ch(src[y0 + xm], sh) + 4 * Ch(c, sh) + 2 * Ch(src[y0 + xp], sh)
                                  + Ch(src[yp + xm], sh) + 2 * Ch(src[yp + x], sh) + Ch(src[yp + xp], sh) + 8) >> 4;
                        int v = Ch(c, sh), diff = v - blur;
                        if (diff > 2) diff -= 2; else if (diff < -2) diff += 2; else diff = 0; // dead zone
                        v += (diff * amount) >> 8;
                        result |= (v < 0 ? 0 : v > 255 ? 255 : v) << sh;
                    }
                    dst[y0 + x] = result;
                }
            });
        }

        static int Ch(int p, int shift) { return (p >> shift) & 255; }

        void Read(Bitmap bmp, int[] px)
        {
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w, w); }
            finally { bmp.UnlockBits(d); }
        }

        void Write(int[] px, Bitmap bmp)
        {
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, d.Scan0 + y * d.Stride, w); }
            finally { bmp.UnlockBits(d); }
        }

        public void Dispose()
        {
            if (output != null) { output.Dispose(); output = null; }
        }
    }
}
