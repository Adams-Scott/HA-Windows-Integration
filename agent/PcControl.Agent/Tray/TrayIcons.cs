using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PcControl.Agent.Tray;

/// <summary>Draws the tray icon at runtime, with an optional red error badge.</summary>
internal static class TrayIcons
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(bool error)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width * 2);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;

            // Monitor
            using var body = new SolidBrush(Color.FromArgb(0x03, 0xA9, 0xF4));
            using var screen = new SolidBrush(Color.FromArgb(0x0D, 0x1B, 0x2A));
            g.FillRectangle(body, 2 * s, 4 * s, 28 * s, 19 * s);
            g.FillRectangle(screen, 4 * s, 6 * s, 24 * s, 15 * s);
            g.FillRectangle(body, 13 * s, 23 * s, 6 * s, 3 * s);
            g.FillRectangle(body, 8 * s, 26 * s, 16 * s, 3 * s);

            // Power glyph
            using var pen = new Pen(Color.White, 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 11 * s, 9 * s, 10 * s, 10 * s, -50, 280);
            g.DrawLine(pen, 16 * s, 8 * s, 16 * s, 13 * s);

            if (error)
            {
                using var red = new SolidBrush(Color.FromArgb(0xE5, 0x39, 0x35));
                using var ring = new Pen(Color.White, 1.5f * s);
                g.FillEllipse(red, 18 * s, 16 * s, 13 * s, 13 * s);
                g.DrawEllipse(ring, 18 * s, 16 * s, 13 * s, 13 * s);
                using var bang = new Pen(Color.White, 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(bang, 24.5f * s, 19 * s, 24.5f * s, 23.5f * s);
                g.DrawLine(bang, 24.5f * s, 26 * s, 24.5f * s, 26.2f * s);
            }
        }

        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }
}
