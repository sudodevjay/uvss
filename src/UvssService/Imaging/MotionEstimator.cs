using OpenCvSharp;

namespace UvssService.Imaging;

/// <summary>Image-based motion estimation between two overlapping
/// line-scan captures -- the same principle an optical mouse uses to track
/// movement from its own tiny image sensor instead of a mechanical wheel:
/// the visual texture (bolts, pipes, welds, grime on the chassis) that
/// appears in one capture reappears, shifted, in the next one, and the size
/// of that shift tells you how far the vehicle actually moved between the
/// two captures. This needs zero extra hardware -- no encoder, no loop, no
/// radar -- just the line-scan camera's own pixels. See US7164810B2
/// ("image-based velocity detection") for the real-world technique this is
/// based on.</summary>
public static class MotionEstimator
{
    /// <summary>Given a `template` patch and a taller `searchRegion` that
    /// should contain the template's content somewhere within it (shifted
    /// vertically -- i.e. along the direction of travel), finds the
    /// best-matching vertical offset via normalized cross-correlation.
    /// Returns null if no confident match is found (e.g. too little visual
    /// texture in this stretch of the vehicle to correlate against, such as
    /// a flat, featureless panel).</summary>
    public static int? FindBestVerticalOffset(Mat template, Mat searchRegion, double minConfidence = 0.5)
    {
        if (template.Empty() || searchRegion.Empty() || searchRegion.Rows < template.Rows
            || searchRegion.Cols != template.Cols)
        {
            return null;
        }

        using var grayTemplate = ToGray(template);
        using var graySearch = ToGray(searchRegion);
        using var result = new Mat();
        Cv2.MatchTemplate(graySearch, grayTemplate, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out var maxVal, out _, out var maxLoc);
        return maxVal < minConfidence ? null : maxLoc.Y;
    }

    private static Mat ToGray(Mat src)
    {
        if (src.Channels() == 1)
        {
            return src;
        }
        var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }
}
