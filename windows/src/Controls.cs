// Small owner-drawn controls for the dark, Windows 11 style window: rounded cards, pill buttons,
// segmented pickers and toggle switches. Plain WinForms, no extra libraries.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DslrWebcamStudio
{
    static class Draw
    {
        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var path = RoundRect(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, path);
        }

        public static void StrokeRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var path = RoundRect(r, radius))
            using (var pen = new Pen(c))
                g.DrawPath(pen, path);
        }

        public static Color Lighten(Color c, int amount)
        {
            return Color.FromArgb(c.A, Math.Min(255, c.R + amount), Math.Min(255, c.G + amount), Math.Min(255, c.B + amount));
        }

        // Fills the control with its parent's color, so rounded corners blend in.
        public static void ParentBackground(Control c, Graphics g)
        {
            using (var b = new SolidBrush(c.Parent != null ? c.Parent.BackColor : Theme.Background))
                g.FillRectangle(b, c.ClientRectangle);
        }
    }

    // A rounded card that stacks its children top to bottom.
    sealed class Card : FlowLayoutPanel
    {
        public Color Border = Theme.CardBorder;
        public float Radius;
        // The card's color; children (and their rounded corners) blend with it.
        public Color Fill { get { return BackColor; } set { BackColor = value; } }

        public Card(float radius, Padding padding)
        {
            Radius = radius;
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = padding;
            BackColor = Theme.Card;
            DoubleBuffered = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Draw.ParentBackground(this, e.Graphics);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Draw.FillRound(e.Graphics, Fill, r, Radius);
            Draw.StrokeRound(e.Graphics, Border, r, Radius);
        }
    }

    // Rounded button with an optional icon glyph (Segoe MDL2 Assets) before the text.
    sealed class PillButton : Button
    {
        public string Glyph;
        public Color Fill = Theme.Button, TextColor = Theme.Text;
        public float Radius;
        bool hover, down;

        public PillButton(string text, string glyph, float radius)
        {
            Text = text;
            Glyph = glyph;
            Radius = radius;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Draw.ParentBackground(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = !Enabled ? Color.FromArgb(120, Fill) : down ? Draw.Lighten(Fill, 30) : hover ? Draw.Lighten(Fill, 16) : Fill;
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Draw.FillRound(g, fill, r, Radius);
            if (Fill == Theme.Button) Draw.StrokeRound(g, Theme.ButtonBorder, r, Radius);
            if (Focused && ShowFocusCues) Draw.StrokeRound(g, Theme.Text, new RectangleF(2, 2, Width - 5, Height - 5), Radius - 2);

            var color = Enabled ? TextColor : Theme.Muted;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            Size textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
            int glyphW = 0;
            Font icon = null;
            if (!string.IsNullOrEmpty(Glyph))
            {
                icon = new Font(Theme.IconFontName, Font.Size);
                glyphW = TextRenderer.MeasureText(g, Glyph, icon, Size.Empty, flags).Width + (Text.Length > 0 ? (int)(Font.Height * 0.5f) : 0);
            }
            int x = (Width - glyphW - textSize.Width) / 2;
            if (icon != null)
            {
                TextRenderer.DrawText(g, Glyph, icon, new Rectangle(x, 0, glyphW, Height), color, flags);
                icon.Dispose();
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + glyphW, 0, textSize.Width + 2, Height), color, flags);
        }
    }

    // A row of mutually exclusive choices, like a segmented control or radio button group.
    sealed class Segmented : Control
    {
        readonly string[] items;
        public readonly int[] Values;
        int selected, hover = -1;
        public event EventHandler SelectedIndexChanged;

        public Segmented(string[] items, int[] values)
        {
            this.items = items;
            Values = values;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value < 0 || value >= items.Length || value == selected) return;
                selected = value;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public int Value { get { return Values[selected]; } }

        // Selects the segment holding this value (no event when it is already selected).
        public void SelectValue(int value)
        {
            int i = Array.IndexOf(Values, value);
            if (i >= 0) SelectedIndex = i;
        }

        Rectangle Segment(int i)
        {
            int w = Width / items.Length;
            return new Rectangle(i * w, 0, i == items.Length - 1 ? Width - i * w : w, Height);
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < items.Length; i++) if (Segment(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Enabled && e.Button == MouseButtons.Left)
            {
                Focus();
                int i = HitTest(e.Location);
                if (i >= 0) SelectedIndex = i;
            }
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left && selected > 0) SelectedIndex = selected - 1;
            if (e.KeyCode == Keys.Right && selected < items.Length - 1) SelectedIndex = selected + 1;
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Draw.ParentBackground(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float radius = Height / 5f;
            Draw.FillRound(g, Theme.Input, new RectangleF(0, 0, Width - 1, Height - 1), radius);
            for (int i = 0; i < items.Length; i++)
            {
                var r = Segment(i);
                if (i == selected)
                    Draw.FillRound(g, Enabled ? Theme.Selected : Theme.InputHover, new RectangleF(r.X + 2, r.Y + 2, r.Width - 5, r.Height - 5), radius - 1);
                else if (i == hover && Enabled)
                    Draw.FillRound(g, Theme.InputHover, new RectangleF(r.X + 2, r.Y + 2, r.Width - 5, r.Height - 5), radius - 1);
                var color = !Enabled ? Theme.Muted : i == selected ? Theme.Text : Color.FromArgb(200, 200, 200);
                TextRenderer.DrawText(g, items[i], Font, r, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            if (Focused && ShowFocusCues) Draw.StrokeRound(g, Theme.Accent, new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), radius);
        }
    }

    // An on/off switch with its label on the right (a restyled CheckBox: keyboard and events as usual).
    sealed class ToggleSwitch : CheckBox
    {
        public ToggleSwitch(string text)
        {
            Text = text;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Draw.ParentBackground(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = Font.Height * 1.15f, w = h * 2f, y = (Height - h) / 2f;
            var track = new RectangleF(1, y, w, h);
            Draw.FillRound(g, Checked ? Theme.Accent : Theme.Input, track, h / 2);
            if (!Checked) Draw.StrokeRound(g, Theme.ButtonBorder, track, h / 2);
            float knob = h - 6;
            float kx = Checked ? track.Right - knob - 3 : track.X + 3;
            using (var b = new SolidBrush(Checked ? Color.White : Theme.Muted))
                g.FillEllipse(b, kx, y + 3, knob, knob);
            if (Focused && ShowFocusCues) Draw.StrokeRound(g, Theme.Text, new RectangleF(track.X - 1, track.Y - 1, track.Width + 2, track.Height + 2), h / 2 + 1);
            var tr = new Rectangle((int)(w + Font.Height * 0.6f), 0, Width - (int)(w + Font.Height * 0.6f), Height);
            TextRenderer.DrawText(g, Text, Font, tr, Enabled ? Theme.Text : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }
}
