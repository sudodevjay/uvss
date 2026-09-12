using OpenCvSharp;

namespace UvssService.Storage;

/// <summary>Saving captured/stitched images to disk. Mirrors the Python
/// version's save_capture_image() exactly (date-bucketed folders, one file
/// per capture).</summary>
public static class ImageStorage
{
    public static string? Save(string rootDir, string name, Mat? image, string suffix = "")
    {
        if (image == null || image.Empty())
        {
            return null;
        }

        var now = DateTime.Now;
        var dateDir = Path.Combine(rootDir, now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateDir);

        var timestamp = now.ToString("yyyyMMdd_HHmmss_fff");
        var nameParts = string.IsNullOrEmpty(suffix) ? new[] { name, timestamp } : new[] { name, timestamp, suffix };
        var filePath = Path.Combine(dateDir, string.Join("_", nameParts) + ".jpg");

        return Cv2.ImWrite(filePath, image) ? filePath : null;
    }

    /// <summary>Same date-bucketed convention, for callers that already
    /// have JPEG-encoded bytes on hand (e.g. a frame already encoded for
    /// the live UI) rather than a Mat.</summary>
    public static string? SaveBytes(string rootDir, string name, byte[]? jpegBytes, string suffix = "")
    {
        if (jpegBytes == null || jpegBytes.Length == 0)
        {
            return null;
        }

        var now = DateTime.Now;
        var dateDir = Path.Combine(rootDir, now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateDir);

        var timestamp = now.ToString("yyyyMMdd_HHmmss_fff");
        var nameParts = string.IsNullOrEmpty(suffix) ? new[] { name, timestamp } : new[] { name, timestamp, suffix };
        var filePath = Path.Combine(dateDir, string.Join("_", nameParts) + ".jpg");

        File.WriteAllBytes(filePath, jpegBytes);
        return filePath;
    }
}
