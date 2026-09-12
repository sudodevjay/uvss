using OpenCvSharp;

namespace UvssService.Imaging;

public static class FrameStitcher
{
    /// <summary>Vertically stack the grabbed frames into one under-vehicle image.</summary>
    public static Mat? Stitch(IReadOnlyList<Mat> frames)
    {
        var usable = frames.Where(frame => frame != null && !frame.Empty()).ToArray();
        if (usable.Length == 0)
        {
            return null;
        }
        var stitched = new Mat();
        Cv2.VConcat(usable, stitched);
        return stitched;
    }

    /// <summary>Rescale a stitched image's height to what it *should* be for
    /// the known physical distance the vehicle travelled during the pass
    /// (pairDistanceMetres * referenceRowsPerMetre rows) -- corrects a
    /// faster-than-reference pass (fewer rows than physically warranted,
    /// would look squashed) or a slower-than-reference pass (more rows
    /// than warranted, would look stretched). See the
    /// ReferenceRowsPerMetre calibration note in appsettings.json.</summary>
    public static Mat NormalizeHeight(Mat image, double pairDistanceMetres, int referenceRowsPerMetre)
    {
        var targetHeight = Math.Max((int)(pairDistanceMetres * referenceRowsPerMetre), 1);
        if (image.Rows == targetHeight)
        {
            return image;
        }
        var resized = new Mat();
        Cv2.Resize(image, resized, new Size(image.Cols, targetHeight), interpolation: InterpolationFlags.Linear);
        return resized;
    }
}
