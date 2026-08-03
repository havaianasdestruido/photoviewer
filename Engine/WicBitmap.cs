using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Photon.Engine;

// Windows Imaging Component (WIC) loading/decoding. This is Photon's REAL
// imaging engine: every thumbnail, full-size view, zoom and rotate runs
// through these BitmapSource pipelines. The WMMR DLLs are only the optional
// "engine layer" on top.
internal static class WicBitmap
{
    // Decode a file to a frozen BitmapSource, optionally downsampled.
    // EXIF orientation (photos taken with phones/cameras) is applied so the
    // displayed image is upright.
    public static BitmapSource LoadSource(string path, int? decodePixelWidth = null)
    {
        using var fs = File.OpenRead(path);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (decodePixelWidth is int w)
            bi.DecodePixelWidth = w;
        bi.StreamSource = fs;
        bi.EndInit();
        bi.Freeze();

        var oriented = ApplyExifOrientation(bi);
        oriented.Freeze();
        return oriented;
    }

    // Copy a BitmapSource into a GDI+ System.Drawing.Bitmap for the WinForms
    // PictureBox. Pixel format is normalized to Bgra32 via WIC (FormatConvertedBitmap).
    public static Bitmap ToGdi(BitmapSource src)
    {
        BitmapSource b = src;
        if (b.Format != PixelFormats.Bgra32 && b.Format != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            b = converted;
        }

        int width = b.PixelWidth;
        int height = b.PixelHeight;
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            b.CopyPixels(new Int32Rect(0, 0, width, height), bd.Scan0, bd.Stride * height, bd.Stride);
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
        return bmp;
    }

    // Returns a TransformedBitmap for the given rotation (0/90/180/270).
    public static BitmapSource Rotate(BitmapSource src, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees == 0)
            return src;
        var t = new RotateTransform(degrees);
        t.Freeze();
        var tb = new TransformedBitmap(src, t);
        tb.Freeze();
        return tb;
    }

    // EXIF orientation readout keyed by the /app1/ifd/{ushort=274} query.
    public static int ReadExifOrientation(BitmapSource src)
    {
        try
        {
            var frame = src as BitmapFrame;
            if (frame?.Metadata is BitmapMetadata md)
            {
                object? v = md.GetQuery("/app1/ifd/{ushort=274}");
                if (v != null && ushort.TryParse(v.ToString(), out ushort o))
                    return o;
            }
        }
        catch { }
        return 1;
    }

    private static BitmapSource ApplyExifOrientation(BitmapSource src)
    {
        try
        {
            int orientation = ReadExifOrientation(src);
            switch (orientation)
            {
                case 3: return Transformed(src, new RotateTransform(180), 180);
                case 6: return Transformed(src, new RotateTransform(90), 90);
                case 8: return Transformed(src, new RotateTransform(270), 270);
                case 2: return Transformed(src, Scale(-1, 1), 0);
                case 4: return Transformed(src, Scale(1, -1), 0);
                case 5:
                    return Transformed(src, Group(Scale(-1, 1), new RotateTransform(270)), 270);
                case 7:
                    return Transformed(src, Group(Scale(-1, 1), new RotateTransform(90)), 90);
                default:
                    return src;
            }
        }
        catch
        {
            return src; // orientation hints are best-effort only
        }
    }

    private static ScaleTransform Scale(double sx, double sy)
    {
        var s = new ScaleTransform(sx, sy);
        s.Freeze();
        return s;
    }

    private static TransformGroup Group(Transform a, Transform b)
    {
        var g = new TransformGroup();
        g.Children.Add(a);
        g.Children.Add(b);
        g.Freeze();
        return g;
    }

    private static BitmapSource Transformed(BitmapSource src, Transform transform, int _)
    {
        var tb = new TransformedBitmap(src, transform);
        tb.Freeze();
        return tb;
    }
}
