using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(15, 11, 24);
    public static readonly Color Surface = Color.FromArgb(25, 19, 38);
    public static readonly Color Field = Color.FromArgb(19, 14, 29);
    public static readonly Color Border = Color.FromArgb(58, 44, 78);
    public static readonly Color Text = Color.FromArgb(247, 242, 255);
    public static readonly Color Muted = Color.FromArgb(179, 163, 198);
    public static readonly Color Accent = Color.FromArgb(204, 175, 255);
    public static readonly Color Primary = Color.FromArgb(108, 54, 206);
    public static readonly Color Success = Color.FromArgb(140, 224, 182);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr parameter, string text);

    public static void DarkTitleBar(Form form)
    {
        int enabled = 1;
        if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        ApplyWindowColor(form, 34, Border);
        ApplyWindowColor(form, 35, Background);
        ApplyWindowColor(form, 36, Text);
    }

    private static void ApplyWindowColor(Form form, int attribute, Color color)
    {
        int colorRef = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(form.Handle, attribute, ref colorRef, sizeof(int));
    }

    public static void SearchHint(TextBox box, string hint)
    {
        SendMessage(box.Handle, 0x1501, new IntPtr(1), hint);
    }

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Mix(Color from, Color to, float amount)
    {
        return Color.FromArgb((int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));
    }

    public static void MemoryCard(Graphics graphics, float x, float y, float scale, Color tint)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(x, y);
        graphics.ScaleTransform(scale, scale);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var shell = new GraphicsPath())
        using (var enamel = new LinearGradientBrush(new Rectangle(0, 0, 60, 70),
            Color.FromArgb(190, 151, 255), Color.FromArgb(88, 42, 170), 65f))
        using (var depth = new SolidBrush(Color.FromArgb(56, 29, 106)))
        using (var rim = new Pen(Color.FromArgb(219, 195, 255), 1.2f))
        using (var face = new SolidBrush(Color.FromArgb(35, 17, 68)))
        using (var monogram = new SolidBrush(Mix(tint, Color.White, .78f)))
        using (var contacts = new SolidBrush(Color.FromArgb(255, 205, 144)))
        {
            shell.AddLine(9, 2, 43, 2);
            shell.AddLine(43, 2, 56, 15);
            shell.AddLine(56, 15, 56, 57);
            shell.AddArc(44, 51, 12, 12, 0, 90);
            shell.AddLine(50, 63, 9, 63);
            shell.AddArc(3, 51, 12, 12, 90, 90);
            shell.AddLine(3, 57, 3, 8);
            shell.AddArc(3, 2, 12, 12, 180, 90);
            shell.CloseFigure();
            graphics.TranslateTransform(2, 5);
            graphics.FillPath(depth, shell);
            graphics.TranslateTransform(-2, -5);
            graphics.FillPath(enamel, shell);
            graphics.DrawPath(rim, shell);
            using (var label = Rounded(new RectangleF(8, 17, 43, 32), 4))
                graphics.FillPath(face, label);
            // A continuous angular M leaves the same silhouette at taskbar and banner sizes.
            graphics.FillPolygon(monogram, new[] { new Point(13, 42), new Point(17, 24), new Point(23, 24),
                new Point(29, 33), new Point(38, 24), new Point(46, 24), new Point(42, 42),
                new Point(35, 42), new Point(37, 32), new Point(28, 40), new Point(22, 31), new Point(20, 42) });
            graphics.FillRectangle(face, 11, 8, 16, 3);
            for (int i = 0; i < 4; i++) graphics.FillRectangle(contacts, 14 + i * 9, 53, 5, 7);
        }
        graphics.Restore(state);
    }
}

internal class SurfacePanel : Panel
{
    public SurfacePanel()
    {
        BackColor = Theme.Surface;
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent == null ? Theme.Background : Parent.BackColor);
        if (Width < 25 || Height < 25) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var shape = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 16))
        using (var fill = new SolidBrush(BackColor))
        using (var stroke = new Pen(Theme.Border))
        {
            e.Graphics.FillPath(fill, shape);
            e.Graphics.DrawPath(stroke, shape);
        }
    }
}

internal sealed class BrandBanner : Control
{
    private readonly Font title = new Font("Segoe UI", 28f, FontStyle.Bold);
    private readonly Font subtitle = new Font("Segoe UI", 10.5f);
    private readonly Timer animation;
    private readonly System.Diagnostics.Stopwatch elapsed = new System.Diagnostics.Stopwatch();
    private float reveal;

    public BrandBanner()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        AccessibleName = "MemoryCard — Backup manual de saves";
        animation = new Timer { Interval = 16 };
        animation.Tick += (sender, args) =>
        {
            float t = Math.Min(1f, elapsed.ElapsedMilliseconds / 680f);
            reveal = 1f - (1f - t) * (1f - t) * (1f - t);
            Invalidate();
            if (t >= 1f) animation.Stop();
        };
    }

    public void Reveal()
    {
        elapsed.Restart();
        animation.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);
        if (Width < 30 || Height < 30) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var frame = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 18f))
        using (var gradient = new LinearGradientBrush(ClientRectangle, Color.FromArgb(32, 20, 56),
            Color.FromArgb(65, 34, 111), LinearGradientMode.Horizontal))
        using (var edge = new Pen(Color.FromArgb(100, 76, 148)))
        {
            g.FillPath(gradient, frame);
            g.DrawPath(edge, frame);
        }

        Theme.MemoryCard(g, 30, (Height - 52) / 2f, 0.75f, Theme.Accent);
        TextRenderer.DrawText(g, "MemoryCard", title, new Rectangle(92, 19, Width - 150, 49),
            Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Backup manual de saves", subtitle, new Rectangle(95, 68, Width - 155, 25),
            Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        float right = Width - 152 + (1f - reveal) * 20;
        float top = (Height - 74) / 2f;
        var baseState = g.Save();
        g.TranslateTransform(right, top);
        using (var rings = new Pen(Color.FromArgb((int)(46 * reveal), 221, 195, 255)))
        {
            g.DrawEllipse(rings, -82, -9, 182, 91);
            g.DrawEllipse(rings, -71, -1, 160, 74);
            g.DrawLine(rings, -140, 37, 110, 37);
        }
        g.TranslateTransform(34, 36);
        g.RotateTransform(-15f);
        Theme.MemoryCard(g, -32, -38, 1.1f, Theme.Mix(Color.FromArgb(82, 54, 125), Theme.Accent, reveal));
        g.Restore(baseState);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animation.Dispose();
            title.Dispose();
            subtitle.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class AnimatedButton : Button
{
    private readonly bool primary;
    private readonly Timer animation;
    private float glow;
    private bool hover;
    private bool pressed;

    public AnimatedButton(string text, bool primary)
    {
        this.primary = primary;
        Text = text;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw, true);
        animation = new Timer { Interval = 16 };
        animation.Tick += (sender, args) =>
        {
            glow = hover ? Math.Min(1f, glow + 0.12f) : Math.Max(0f, glow - 0.12f);
            Invalidate();
            if (glow == 0f || glow == 1f) animation.Stop();
        };
        MouseEnter += (sender, args) => { hover = true; animation.Start(); };
        MouseLeave += (sender, args) => { hover = false; pressed = false; animation.Start(); };
        MouseDown += (sender, args) => { pressed = args.Button == MouseButtons.Left; Invalidate(); };
        MouseUp += (sender, args) => { pressed = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent == null ? Theme.Surface : Parent.BackColor);
        if (Width < 20 || Height < 20) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color normal = primary ? Theme.Primary : Color.FromArgb(37, 28, 53);
        Color hovered = primary ? Color.FromArgb(130, 72, 232) : Color.FromArgb(53, 36, 77);
        Color fill = !Enabled ? Color.FromArgb(34, 27, 47)
            : pressed ? Theme.Mix(normal, Theme.Background, .2f) : Theme.Mix(normal, hovered, glow);
        Color outline = Focused && Enabled ? Theme.Accent
            : primary && Enabled ? Theme.Mix(Color.FromArgb(149, 98, 239), Theme.Accent, glow)
            : Theme.Mix(Theme.Border, Color.FromArgb(122, 84, 170), Enabled ? glow : 0);
        using (var shape = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 9))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(outline))
        {
            g.FillPath(brush, shape);
            g.DrawPath(pen, shape);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? Theme.Text : Color.FromArgb(133, 116, 152),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) animation.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class GameList : ListBox
{
    private int hovered = -1;

    public GameList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 48;
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        AccessibleName = "Jogos com saves";
        MouseMove += (sender, args) => SetHover(IndexFromPoint(args.Location));
        MouseLeave += (sender, args) => SetHover(-1);
    }

    private void SetHover(int index)
    {
        if (hovered == index) return;
        int previous = hovered;
        hovered = index;
        if (previous >= 0 && previous < Items.Count) Invalidate(GetItemRectangle(previous));
        if (hovered >= 0 && hovered < Items.Count) Invalidate(GetItemRectangle(hovered));
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var g = e.Graphics;
        using (var background = new SolidBrush(Theme.Field)) g.FillRectangle(background, e.Bounds);
        bool selected = (e.State & DrawItemState.Selected) != 0;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var row = Rectangle.Inflate(e.Bounds, -5, -3);
        if (selected || hovered == e.Index)
        {
            using (var shape = Theme.Rounded(row, 8))
            using (var fill = new SolidBrush(selected ? Color.FromArgb(54, 34, 88) : Theme.Surface))
            using (var stroke = new Pen(selected ? Color.FromArgb(123, 84, 178) : Theme.Border))
            {
                g.FillPath(fill, shape);
                g.DrawPath(stroke, shape);
            }
        }

        var tint = selected ? Theme.Accent : Theme.Muted;
        Theme.MemoryCard(g, e.Bounds.X + 17, e.Bounds.Y + (e.Bounds.Height - 22) / 2f, 0.32f, tint);
        var title = new Rectangle(row.X + 42, row.Y, Math.Max(1, row.Width - 65), row.Height);
        TextRenderer.DrawText(g, Items[e.Index].ToString(), Font, title, Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (selected)
        {
            using (var marker = new SolidBrush(Theme.Accent))
                g.FillEllipse(marker, row.Right - 16, row.Top + (row.Height - 6) / 2, 6, 6);
        }
    }
}

internal sealed class ActivityTrack : Control
{
    private readonly Timer timer;
    private readonly System.Diagnostics.Stopwatch elapsed = new System.Diagnostics.Stopwatch();

    public ActivityTrack()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        timer = new Timer { Interval = 25 };
        timer.Tick += (sender, args) => Invalidate();
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
    }

    public void SetActive(bool active)
    {
        if (active && !timer.Enabled)
        {
            elapsed.Restart();
            timer.Start();
        }
        else if (!active)
        {
            timer.Stop();
            elapsed.Reset();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);
        if (!timer.Enabled || Width < 10 || Height < 4) return;
        int segment = Math.Max(24, Width / 5);
        int left = (int)(elapsed.ElapsedMilliseconds * (Width + segment) / 1300 % (Width + segment)) - segment;
        var bounds = new Rectangle(0, (Height - 3) / 2, Width, 3);
        using (var rail = new SolidBrush(Theme.Border)) g.FillRectangle(rail, bounds);
        using (var glow = new LinearGradientBrush(new Rectangle(left, bounds.Y, segment, bounds.Height),
            Theme.Border, Theme.Accent, LinearGradientMode.Horizontal))
            g.FillRectangle(glow, left, bounds.Y, segment, bounds.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}

