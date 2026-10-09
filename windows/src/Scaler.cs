// Fast parallel bicubic (Catmull-Rom) resize for 32bpp bitmaps, with optional mirroring.
// Separable: one horizontal pass into a float buffer, then one vertical pass.
// Catmull-Rom keeps edges noticeably crisper than bilinear when upscaling the
// 960x640 live view; for downscaling the kernel widens so it doesn't alias.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;

namespace DslrWebcamStudio
{
    public sealed unsafe class Scaler
    {
        float[] temp = new float[0];

        // Scales srcRect of src into dstRect of dst, optionally mirrored left-right (flipH) and/or
        // upside-down (flipV). Pixels of dst outside dstRect are set to black.
        public void Draw(Bitmap src, Rectangle srcRect, Bitmap dst, Rectangle dstRect, bool flipH = false, bool flipV = false)
        {
            var sd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var dd = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { Resize(sd, srcRect, dd, dstRect, flipH, flipV); }
            finally { dst.UnlockBits(dd); src.UnlockBits(sd); }
        }

        // Filter taps for one axis: dst position i reads src indices idx[i*taps + t] with weight w[i*taps + t].
        sealed class Axis
        {
            public int Taps;
            public int[] Idx;
            public float[] W;
        }

        static float CatmullRom(double x)
        {
            x = Math.Abs(x);
            if (x < 1) return (float)((1.5 * x - 2.5) * x * x + 1);
            if (x < 2) return (float)(((-0.5 * x + 2.5) * x - 4) * x + 2);
            return 0;
        }

        static Axis BuildAxis(int srcStart, int srcLen, int dstLen, bool flip)
        {
            double scale = (double)srcLen / dstLen;
            double fscale = Math.Max(1.0, scale);       // widen the kernel when shrinking
            double support = 2 * fscale;
            int taps = (int)Math.Ceiling(support * 2) + 1;
            var a = new Axis { Taps = taps, Idx = new int[dstLen * taps], W = new float[dstLen * taps] };
            for (int i = 0; i < dstLen; i++)
            {
                int o = (flip ? dstLen - 1 - i : i) * taps;
                double center = (i + 0.5) * scale - 0.5;
                int first = (int)Math.Floor(center - support) + 1;
                float sum = 0;
                for (int t = 0; t < taps; t++)
                {
                    int s = first + t;
                    float w = CatmullRom((s - center) / fscale);
                    a.Idx[o + t] = srcStart + Math.Max(0, Math.Min(srcLen - 1, s));
                    a.W[o + t] = w;
                    sum += w;
                }
                if (sum != 0) for (int t = 0; t < taps; t++) a.W[o + t] /= sum;
            }
            return a;
        }

        void Resize(BitmapData sd, Rectangle sr, BitmapData dd, Rectangle dr, bool flipH, bool flipV)
        {
            int dw = dr.Width, dh = dr.Height;
            byte* sBase = (byte*)sd.Scan0, dBase = (byte*)dd.Scan0;
            int sStride = sd.Stride, dStride = dd.Stride, dstW = dd.Width;
            var ax = BuildAxis(sr.X, sr.Width, dw, flipH);
            var ay = BuildAxis(0, sr.Height, dh, flipV);  // rows index into the temp buffer

            int need = sr.Height * dw * 3;
            if (temp.Length < need) temp = new float[need];
            var tmp = temp;

            // Horizontal pass: each source row -> dw columns (B, G, R as floats).
            Parallel.For(0, sr.Height, row =>
            {
                byte* s = sBase + (long)(sr.Y + row) * sStride;
                int to = row * dw * 3;
                int taps = ax.Taps;
                for (int x = 0; x < dw; x++)
                {
                    float b = 0, g = 0, r = 0;
                    int o = x * taps;
                    for (int t = 0; t < taps; t++)
                    {
                        byte* p = s + ax.Idx[o + t] * 4;
                        float w = ax.W[o + t];
                        b += p[0] * w; g += p[1] * w; r += p[2] * w;
                    }
                    tmp[to++] = b; tmp[to++] = g; tmp[to++] = r;
                }
            });

            // Vertical pass into the destination rectangle; bars outside it are black.
            Parallel.For(0, dd.Height, y =>
            {
                uint* drow = (uint*)(dBase + (long)y * dStride);
                if (y < dr.Y || y >= dr.Bottom)
                {
                    for (int x = 0; x < dstW; x++) drow[x] = 0xFF000000;
                    return;
                }
                for (int x = 0; x < dr.X; x++) drow[x] = 0xFF000000;
                for (int x = dr.Right; x < dstW; x++) drow[x] = 0xFF000000;

                int taps = ay.Taps, o = (y - dr.Y) * taps;
                uint* d = drow + dr.X;
                for (int x = 0; x < dw; x++)
                {
                    float b = 0, g = 0, r = 0;
                    int c = x * 3;
                    for (int t = 0; t < taps; t++)
                    {
                        int k = ay.Idx[o + t] * dw * 3 + c;
                        float w = ay.W[o + t];
                        b += tmp[k] * w; g += tmp[k + 1] * w; r += tmp[k + 2] * w;
                    }
                    d[x] = 0xFF000000 | ((uint)Clamp(r) << 16) | ((uint)Clamp(g) << 8) | (uint)Clamp(b);
                }
            });
        }

        static int Clamp(float v)
        {
            int i = (int)(v + 0.5f);
            return i < 0 ? 0 : i > 255 ? 255 : i;
        }
    }
}
