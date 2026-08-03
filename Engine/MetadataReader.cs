using System.IO;
using System.Windows.Media.Imaging;

namespace Photon.Engine;

// EXIF / basic metadata readout. Primary path is WIC (BitmapMetadata queries),
// which is honest and always available. MetadataSys + WLMFReadWrite are probed
// separately by PhotoEngine and their stub results are reported in the UI.
internal static class MetadataReader
{
    private static readonly (string Query, string Label)[] ExifFields =
    {
        ("/app1/ifd/{ushort=271}", "Make"),
        ("/app1/ifd/{ushort=272}", "Model"),
        ("/app1/ifd/{ushort=305}", "Software"),
        ("/app1/ifd/{ushort=274}", "Orientation"),
        ("/app1/ifd/exif/{ushort=36867}", "DateTaken"),
        ("/app1/ifd/exif/{ushort=36868}", "DateOriginal"),
        ("/app1/ifd/exif/{ushort=33434}", "ExposureTime"),
        ("/app1/ifd/exif/{ushort=33437}", "FNumber"),
        ("/app1/ifd/exif/{ushort=34855}", "ISO"),
        ("/app1/ifd/exif/{ushort=37377}", "ShutterSpeed"),
        ("/app1/ifd/exif/{ushort=37386}", "FocalLength"),
        ("/app1/ifd/exif/{ushort=41987}", "WhiteBalance"),
    };

    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        var dict = new Dictionary<string, string>();

        try
        {
            using var fs = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                dict["DecodeError"] = "no frame";
                return dict;
            }
            var frame = decoder.Frames[0];
            dict["Format"] = frame.Format.ToString();
            dict["Width"] = frame.PixelWidth.ToString();
            dict["Height"] = frame.PixelHeight.ToString();
            dict["DPI"] = $"{frame.DpiX:0.##} x {frame.DpiY:0.##}";

            if (frame.Metadata is BitmapMetadata md)
            {
                foreach (var (query, label) in ExifFields)
                {
                    try
                    {
                        object? v = md.GetQuery(query);
                        string? s = FormatValue(v);
                        if (!string.IsNullOrEmpty(s))
                            dict[label] = s;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            dict["DecodeError"] = $"{ex.GetType().Name}: {ex.Message}";
        }

        var fi = new FileInfo(path);
        dict["FileSize"] = FormatBytes(fi.Length);
        dict["Modified"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
        return dict;
    }

    private static string? FormatValue(object? v)
    {
        if (v == null)
            return null;
        string s = v.ToString() ?? "";
        s = s.Trim();
        if (s.Length == 0)
            return null;
        // EXIF rationals come back as "N/D" strings; render as decimal.
        if (s.Contains('/'))
        {
            var parts = s.Split('/');
            if (parts.Length == 2 &&
                long.TryParse(parts[0].Trim(), out long n) &&
                long.TryParse(parts[1].Trim(), out long d) && d != 0)
                return ((double)n / d).ToString("0.####");
        }
        return s;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }
}
