using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Dictee;

internal static class Shapes
{
    public static GraphicsPath Capsule(RectangleF r) => RoundedRect(r, Math.Min(r.Width, r.Height) / 2);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class AppIcon
{
    public static readonly Icon Small = Create(SystemInformation.SmallIconSize.Width);

    static Icon Create(int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = size;

            using (var background = new SolidBrush(Color.FromArgb(28, 28, 30)))
            using (var tile = Shapes.RoundedRect(new RectangleF(0, 0, s, s), s * 0.26f))
                g.FillPath(background, tile);

            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, Math.Max(1.2f, s * 0.08f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using (var body = Shapes.Capsule(new RectangleF(s * 0.37f, s * 0.14f, s * 0.26f, s * 0.43f)))
                g.FillPath(white, body);
            g.DrawArc(pen, s * 0.27f, s * 0.30f, s * 0.46f, s * 0.38f, 0, 180);
            g.DrawLine(pen, s * 0.5f, s * 0.68f, s * 0.5f, s * 0.82f);
            g.DrawLine(pen, s * 0.38f, s * 0.82f, s * 0.62f, s * 0.82f);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}

/// <summary>Premultiplied ARGB surface pushed to a layered window with per-pixel alpha.</summary>
internal sealed class LayeredCanvas : IDisposable
{
    const uint ULW_ALPHA = 2;
    const byte AC_SRC_ALPHA = 1;

    readonly IntPtr screenDc, memoryDc, dib, previousBitmap;
    readonly Bitmap bitmap;

    public Graphics Graphics { get; }
    public int Width { get; }
    public int Height { get; }

    public LayeredCanvas(int width, int height)
    {
        Width = width;
        Height = height;
        screenDc = Native.GetDC(IntPtr.Zero);
        memoryDc = Native.CreateCompatibleDC(screenDc);
        var info = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        dib = Native.CreateDIBSection(memoryDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        previousBitmap = Native.SelectObject(memoryDc, dib);
        bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);

        Graphics = Graphics.FromImage(bitmap);
        Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
    }

    public void Present(IntPtr window, Point location, byte opacity)
    {
        Graphics.Flush(FlushIntention.Sync);
        var destination = new Native.POINT(location.X, location.Y);
        var size = new Native.SIZE(Width, Height);
        var source = new Native.POINT(0, 0);
        var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = opacity, AlphaFormat = AC_SRC_ALPHA };
        Native.UpdateLayeredWindow(window, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
    }

    public void Dispose()
    {
        Graphics.Dispose();
        bitmap.Dispose();
        Native.SelectObject(memoryDc, previousBitmap);
        Native.DeleteObject(dib);
        Native.DeleteDC(memoryDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
    }
}
