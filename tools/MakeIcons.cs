// Draws the app icon (same design as assets/icon.svg) and writes:
//   assets/icon_<size>.png for 16..1024 and assets/icon.ico (16-256, PNG-compressed entries).
// Build and run: csc MakeIcons.cs -r:System.Drawing.dll && MakeIcons.exe <assetsDir>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcons
{
    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        var icoSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var pngs = new Dictionary<int, byte[]>();
        foreach (int s in new[] { 16, 24, 32, 48, 64, 128, 256, 512, 1024 })
        {
            using (var bmp = Draw(s))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs[s] = ms.ToArray();
                File.WriteAllBytes(Path.Combine(dir, "icon_" + s + ".png"), pngs[s]);
            }
        }
        using (var f = new BinaryWriter(File.Create(Path.Combine(dir, "icon.ico"))))
        {
            f.Write((short)0); f.Write((short)1); f.Write((short)icoSizes.Length);
            int offset = 6 + 16 * icoSizes.Length;
            foreach (int s in icoSizes)
            {
                f.Write((byte)(s >= 256 ? 0 : s)); f.Write((byte)(s >= 256 ? 0 : s));
                f.Write((byte)0); f.Write((byte)0); f.Write((short)1); f.Write((short)32);
                f.Write(pngs[s].Length); f.Write(offset);
                offset += pngs[s].Length;
            }
            foreach (int s in icoSizes) f.Write(pngs[s]);
        }
    }

    // Design on a 512x512 canvas, scaled to the requested size.
    static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(size / 512f, size / 512f);

            using (var bg = new LinearGradientBrush(new PointF(0, 16), new PointF(0, 496), Color.FromArgb(0x2b, 0x31, 0x40), Color.FromArgb(0x15, 0x18, 0x20)))
            using (var p = RoundRect(16, 16, 480, 480, 108)) g.FillPath(bg, p);

            // Camera body with the viewfinder hump.
            using (var body = new GraphicsPath())
            {
                body.AddLine(176, 148, 336, 148);
                body.AddLine(336, 148, 358, 188);
                body.AddLine(358, 188, 416, 188);
                body.AddArc(386, 188, 60, 60, 270, 90);
                body.AddArc(386, 358, 60, 60, 0, 90);
                body.AddArc(66, 358, 60, 60, 90, 90);
                body.AddArc(66, 188, 60, 60, 180, 90);
                body.AddLine(96, 188, 154, 188);
                body.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(0xe9, 0xed, 0xf5))) g.FillPath(b, body);
            }
            using (var b = new SolidBrush(Color.FromArgb(0x15, 0x18, 0x20))) g.FillEllipse(b, 256 - 92, 300 - 92, 184, 184);
            using (var lens = new LinearGradientBrush(new PointF(184, 228), new PointF(328, 372), Color.FromArgb(0x5a, 0xa9, 0xff), Color.FromArgb(0x1c, 0x4f, 0x9c)))
                g.FillEllipse(lens, 256 - 72, 300 - 72, 144, 144);
            using (var b = new SolidBrush(Color.FromArgb(140, 255, 255, 255))) g.FillEllipse(b, 230 - 18, 274 - 18, 36, 36);
            using (var b = new SolidBrush(Color.FromArgb(0xff, 0x3b, 0x3b))) g.FillEllipse(b, 392 - 16, 226 - 16, 32, 32);
        }
        return bmp;
    }

    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
