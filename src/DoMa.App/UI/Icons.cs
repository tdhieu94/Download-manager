using System.Drawing.Drawing2D;

namespace DoMa.App.UI;

public enum IconKind
{
    None,
    Plus,
    Play,
    Pause,
    Trash,
    Settings,
    Folder,
    File,
    Link,
    Refresh,
    Download,
    Check,
    Clock,
    Document,
    Package,
    Archive,
    Shapes,
    Inbox,
    Search,
    Copy,
    Power,
}

/// <summary>Bộ icon dạng nét (line icon) vẽ bằng GDI+, sắc nét ở mọi mức DPI.</summary>
public static class Icons
{
    public static void Draw(Graphics g, IconKind kind, RectangleF bounds, Color color, float stroke = 2f)
    {
        if (kind == IconKind.None || bounds.Width <= 0)
            return;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(bounds.Width / 24f, bounds.Height / 24f);
        using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(color);
        Paint(g, kind, pen, fill);
        g.Restore(state);
    }

    public static Bitmap Bitmap(IconKind kind, int size, Color color, float stroke = 2f)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        Draw(g, kind, new RectangleF(0, 0, size, size), color, stroke);
        return bmp;
    }

    private static void Paint(Graphics g, IconKind kind, Pen p, Brush fill)
    {
        switch (kind)
        {
            case IconKind.Plus:
                g.DrawLine(p, 12, 5, 12, 19);
                g.DrawLine(p, 5, 12, 19, 12);
                break;
            case IconKind.Play:
                using (var path = Poly(new PointF(8, 5), new PointF(19, 12), new PointF(8, 19)))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(p, path);
                }
                break;
            case IconKind.Pause:
                Round(g, p, fill, new RectangleF(6.5f, 5, 3.5f, 14), 1, true);
                Round(g, p, fill, new RectangleF(14, 5, 3.5f, 14), 1, true);
                break;
            case IconKind.Trash:
                g.DrawLine(p, 4, 6.5f, 20, 6.5f);
                g.DrawLines(p, [new PointF(9, 6.5f), new PointF(9, 4), new PointF(15, 4), new PointF(15, 6.5f)]);
                using (var path = Poly(new PointF(6, 6.5f), new PointF(7, 20), new PointF(17, 20), new PointF(18, 6.5f)))
                    g.DrawPath(p, path);
                g.DrawLine(p, 10, 10.5f, 10, 16);
                g.DrawLine(p, 14, 10.5f, 14, 16);
                break;
            case IconKind.Settings:
                DrawGear(g, p);
                break;
            case IconKind.Folder:
                using (var path = Poly(
                           new PointF(3, 7), new PointF(3, 18.5f), new PointF(21, 18.5f), new PointF(21, 8.5f),
                           new PointF(12, 8.5f), new PointF(10, 5.5f), new PointF(3, 5.5f)))
                    g.DrawPath(p, path);
                break;
            case IconKind.File:
            case IconKind.Document:
                using (var path = Poly(
                           new PointF(6, 3), new PointF(14, 3), new PointF(19, 8), new PointF(19, 21), new PointF(6, 21)))
                    g.DrawPath(p, path);
                g.DrawLines(p, [new PointF(14, 3), new PointF(14, 8), new PointF(19, 8)]);
                if (kind == IconKind.Document)
                {
                    g.DrawLine(p, 9, 13, 16, 13);
                    g.DrawLine(p, 9, 17, 14, 17);
                }
                break;
            case IconKind.Link:
                g.DrawArc(p, 3, 9, 10, 6, 90, 180);
                g.DrawArc(p, 11, 9, 10, 6, 270, 180);
                g.DrawLine(p, 8, 9, 10.5f, 9);
                g.DrawLine(p, 8, 15, 10.5f, 15);
                g.DrawLine(p, 13.5f, 9, 16, 9);
                g.DrawLine(p, 13.5f, 15, 16, 15);
                g.DrawLine(p, 8.5f, 12, 15.5f, 12);
                break;
            case IconKind.Refresh:
                g.DrawArc(p, 3.5f, 3.5f, 17, 17, 0, 300);
                g.DrawLine(p, 16.25f, 4.64f, 20, 8);
                g.DrawLines(p, [new PointF(20, 3.5f), new PointF(20, 8), new PointF(15.5f, 8)]);
                break;
            case IconKind.Download:
                g.DrawLine(p, 12, 4, 12, 15);
                g.DrawLines(p, [new PointF(7, 10.5f), new PointF(12, 15.5f), new PointF(17, 10.5f)]);
                g.DrawLine(p, 5, 20, 19, 20);
                break;
            case IconKind.Check:
                g.DrawLines(p, [new PointF(5, 12.5f), new PointF(10, 17.5f), new PointF(19, 7)]);
                break;
            case IconKind.Clock:
                g.DrawEllipse(p, 3.5f, 3.5f, 17, 17);
                g.DrawLines(p, [new PointF(12, 7.5f), new PointF(12, 12), new PointF(15, 14)]);
                break;
            case IconKind.Package:
                using (var path = Poly(
                           new PointF(12, 3), new PointF(20, 7.5f), new PointF(20, 16.5f), new PointF(12, 21),
                           new PointF(4, 16.5f), new PointF(4, 7.5f)))
                    g.DrawPath(p, path);
                g.DrawLines(p, [new PointF(4, 7.5f), new PointF(12, 12), new PointF(20, 7.5f)]);
                g.DrawLine(p, 12, 12, 12, 21);
                break;
            case IconKind.Archive:
                Round(g, p, fill, new RectangleF(3, 4, 18, 5), 1, false);
                g.DrawLines(p, [new PointF(5, 9), new PointF(5, 20), new PointF(19, 20), new PointF(19, 9)]);
                g.DrawLine(p, 10, 13, 14, 13);
                break;
            case IconKind.Shapes:
                g.DrawEllipse(p, 3.5f, 3.5f, 7.5f, 7.5f);
                Round(g, p, fill, new RectangleF(13, 13, 7.5f, 7.5f), 1.5f, false);
                using (var path = Poly(new PointF(16.75f, 3.5f), new PointF(20.5f, 10), new PointF(13, 10)))
                    g.DrawPath(p, path);
                using (var path = Poly(new PointF(3.5f, 16.75f), new PointF(7.25f, 13), new PointF(11, 16.75f), new PointF(7.25f, 20.5f)))
                    g.DrawPath(p, path);
                break;
            case IconKind.Inbox:
                using (var path = Poly(
                           new PointF(3, 13), new PointF(6, 5), new PointF(18, 5), new PointF(21, 13),
                           new PointF(21, 19), new PointF(3, 19)))
                    g.DrawPath(p, path);
                g.DrawLines(p, [new PointF(3, 13), new PointF(8, 13), new PointF(9.5f, 15.5f), new PointF(14.5f, 15.5f), new PointF(16, 13), new PointF(21, 13)]);
                break;
            case IconKind.Search:
                g.DrawEllipse(p, 4, 4, 13, 13);
                g.DrawLine(p, 15, 15, 20, 20);
                break;
            case IconKind.Copy:
                Round(g, p, fill, new RectangleF(8.5f, 8.5f, 12, 12), 2, false);
                g.DrawLines(p, [new PointF(15.5f, 8.5f), new PointF(15.5f, 4), new PointF(4, 4), new PointF(4, 15.5f), new PointF(8.5f, 15.5f)]);
                break;
            case IconKind.Power:
                g.DrawArc(p, 4.5f, 5, 15, 15, -50, 280);
                g.DrawLine(p, 12, 3.5f, 12, 11);
                break;
        }
    }

    private static GraphicsPath Poly(params PointF[] points)
    {
        var path = new GraphicsPath();
        path.AddPolygon(points);
        return path;
    }

    private static void Round(Graphics g, Pen p, Brush fill, RectangleF r, float radius, bool filled)
    {
        using var path = Theme.RoundRect(r, radius);
        if (filled)
            g.FillPath(fill, path);
        g.DrawPath(p, path);
    }

    private static void DrawGear(Graphics g, Pen p)
    {
        const int teeth = 8;
        var points = new List<PointF>();
        for (var i = 0; i < teeth; i++)
        {
            var a = i * Math.PI * 2 / teeth;
            var step = Math.PI * 2 / teeth;
            foreach (var (offset, radius) in new[] { (-0.25, 7.0), (-0.12, 9.5), (0.12, 9.5), (0.25, 7.0) })
            {
                var t = a + offset * step * 1.6;
                points.Add(new PointF(12 + (float)(Math.Cos(t) * radius), 12 + (float)(Math.Sin(t) * radius)));
            }
        }
        using var path = new GraphicsPath();
        path.AddPolygon(points.ToArray());
        g.DrawPath(p, path);
        g.DrawEllipse(p, 9, 9, 6, 6);
    }
}
