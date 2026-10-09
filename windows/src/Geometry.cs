// Placement of a live-view frame inside the fixed output frame.
// Canon live view is usually 3:2 (960x640 on the 700D) and stream sizes are 16:9, so the
// frame is either pillarboxed (Fit) or center-cropped (Fill).
using System;
using System.Drawing;

namespace DslrWebcamStudio
{
    public static class Geometry
    {
        // Largest rect with the source aspect that fits inside dst, centered.
        public static Rectangle FitRect(int srcW, int srcH, int dstW, int dstH)
        {
            if (srcW <= 0 || srcH <= 0) throw new ArgumentException("source size must be positive");
            double scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
            int w = Math.Min(dstW, (int)Math.Round(srcW * scale, MidpointRounding.AwayFromZero));
            int h = Math.Min(dstH, (int)Math.Round(srcH * scale, MidpointRounding.AwayFromZero));
            return new Rectangle((dstW - w) / 2, (dstH - h) / 2, w, h);
        }

        // Centered region of the source with the dst aspect.
        public static Rectangle CropRect(int srcW, int srcH, int dstW, int dstH)
        {
            if (srcW <= 0 || srcH <= 0) throw new ArgumentException("source size must be positive");
            if ((long)srcW * dstH > (long)dstW * srcH) // source wider: trim sides
            {
                int w = (int)Math.Round((double)srcH * dstW / dstH, MidpointRounding.AwayFromZero);
                return new Rectangle((srcW - w) / 2, 0, w, srcH);
            }
            int h = (int)Math.Round((double)srcW * dstH / dstW, MidpointRounding.AwayFromZero);
            return new Rectangle(0, (srcH - h) / 2, srcW, h);
        }
    }
}
