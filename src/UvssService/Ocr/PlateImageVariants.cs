using OpenCvSharp;

namespace UvssService.Ocr;

public record PlateCropMetrics(int Width, int Height, int Area, double AspectRatio, double Sharpness, bool IsDoubleLine, bool Usable);

/// <summary>Exact C# port of anpr-ai-service/main.py's image-enhancement and
/// crop-quality helpers (clahe_bgr, sharpen_bgr, upscale_for_ocr,
/// deskew_plate_image, plate_sharpness, plate_crop_metrics,
/// find_double_line_split, ocr_image_variants) -- same constants, same
/// formulas, so the C# multi-crop-variant search sees the same candidate
/// images the Python pipeline does.</summary>
public static class PlateImageVariants
{
    private const int MinPlateCropWidth = 48;
    private const int MinPlateCropHeight = 16;
    private const int MinPlateCropArea = 900;
    private const double MinPlateAspectRatio = 1.4;
    private const double MaxPlateAspectRatio = 8.5;
    private const double DoubleLineMinAspectRatio = 0.6;
    private const double DoubleLineMaxAspectRatio = 1.8;
    private const double MinPlateSharpness = 18.0;
    public const int OcrTargetMinHeight = 72;
    public const int OcrMaxVariants = 6;

    public static double PlateSharpness(Mat image)
    {
        if (image.Empty()) return 0.0;
        using var gray = new Mat();
        if (image.Channels() == 3) Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        else image.CopyTo(gray);
        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out var stddev);
        var std = stddev.Val0;
        return std * std;
    }

    public static PlateCropMetrics GetPlateCropMetrics(Mat image)
    {
        if (image.Empty())
        {
            return new PlateCropMetrics(0, 0, 0, 0.0, 0.0, false, false);
        }

        var height = image.Rows;
        var width = image.Cols;
        var area = width * height;
        var aspectRatio = width / (double)Math.Max(height, 1);
        var sharpness = PlateSharpness(image);
        var isDoubleLine = aspectRatio >= DoubleLineMinAspectRatio && aspectRatio <= DoubleLineMaxAspectRatio;
        var aspectOk = (aspectRatio >= MinPlateAspectRatio && aspectRatio <= MaxPlateAspectRatio) || isDoubleLine;
        var usable = width >= MinPlateCropWidth && height >= MinPlateCropHeight && area >= MinPlateCropArea
            && aspectOk && sharpness >= MinPlateSharpness;
        return new PlateCropMetrics(width, height, area, aspectRatio, sharpness, isDoubleLine, usable);
    }

    public static double PlateCropQualityScore(Mat image, double detectionConfidence = 0.0)
    {
        var metrics = GetPlateCropMetrics(image);
        if (!metrics.Usable) return 0.0;

        var areaScore = Math.Min(metrics.Area / 12000.0, 1.0);
        var sharpnessScore = Math.Min(metrics.Sharpness / 180.0, 1.0);
        var aspect = metrics.AspectRatio;
        var aspectScore = metrics.IsDoubleLine
            ? Math.Max(0.0, 1.0 - Math.Abs(aspect - 1.0) / 1.2)
            : Math.Max(0.0, 1.0 - Math.Abs(aspect - 4.5) / 4.0);
        return detectionConfidence * 0.45 + areaScore * 0.20 + sharpnessScore * 0.25 + aspectScore * 0.10;
    }

    public static Mat ClaheBgr(Mat image)
    {
        using var lab = new Mat();
        Cv2.CvtColor(image, lab, ColorConversionCodes.BGR2Lab);
        var channels = Cv2.Split(lab);
        using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));
        using var enhancedL = new Mat();
        clahe.Apply(channels[0], enhancedL);
        using var merged = new Mat();
        Cv2.Merge(new[] { enhancedL, channels[1], channels[2] }, merged);
        var result = new Mat();
        Cv2.CvtColor(merged, result, ColorConversionCodes.Lab2BGR);
        foreach (var c in channels) c.Dispose();
        return result;
    }

    public static Mat SharpenBgr(Mat image)
    {
        using var blurred = new Mat();
        Cv2.GaussianBlur(image, blurred, new Size(0, 0), 1.0);
        var result = new Mat();
        Cv2.AddWeighted(image, 1.6, blurred, -0.6, 0, result);
        return result;
    }

    public static Mat UpscaleForOcr(Mat image, int minHeight = OcrTargetMinHeight)
    {
        var height = image.Rows;
        var width = image.Cols;
        if (height <= 0 || width <= 0) return image;
        var scale = Math.Max(1.0, minHeight / (double)height);
        if (scale <= 1.05) return image;
        var result = new Mat();
        Cv2.Resize(image, result, new Size(0, 0), scale, scale, InterpolationFlags.Cubic);
        return result;
    }

    public static Mat DeskewPlateImage(Mat image)
    {
        if (image.Empty()) return image;

        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);
        using var thresh = new Mat();
        Cv2.Threshold(blurred, thresh, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        using var coordsMat = new Mat();
        Cv2.FindNonZero(thresh, coordsMat);
        if (coordsMat.Empty() || coordsMat.Rows < 20)
        {
            return image;
        }
        var coords = new Point[coordsMat.Rows];
        for (var i = 0; i < coordsMat.Rows; i++)
        {
            coords[i] = coordsMat.Get<Point>(i, 0);
        }

        var rect = Cv2.MinAreaRect(coords);
        var angle = rect.Angle;
        if (angle < -45) angle += 90;
        if (Math.Abs(angle) < 1.0 || Math.Abs(angle) > 18)
        {
            return image;
        }

        var height = image.Rows;
        var width = image.Cols;
        using var matrix = Cv2.GetRotationMatrix2D(new Point2f(width / 2.0f, height / 2.0f), angle, 1.0);
        var result = new Mat();
        Cv2.WarpAffine(image, result, matrix, new Size(width, height), InterpolationFlags.Cubic, BorderTypes.Replicate);
        return result;
    }

    public static int FindDoubleLineSplit(Mat image)
    {
        var height = image.Rows;
        using var gray = new Mat();
        if (image.Channels() == 3) Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        else image.CopyTo(gray);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);
        using var thresh = new Mat();
        Cv2.Threshold(blurred, thresh, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        var rowInk = new double[height];
        for (var y = 0; y < height; y++)
        {
            rowInk[y] = Cv2.Sum(thresh[y, y + 1, 0, thresh.Cols]).Val0;
        }

        var bandStart = Math.Max((int)(height * 0.25), 1);
        var bandEnd = Math.Min((int)(height * 0.75), height - 1);
        if (bandEnd <= bandStart)
        {
            return height / 2;
        }

        var bandLen = bandEnd - bandStart;
        var valleyOffset = 0;
        var minVal = double.MaxValue;
        var sum = 0.0;
        for (var i = 0; i < bandLen; i++)
        {
            var v = rowInk[bandStart + i];
            sum += v;
            if (v < minVal) { minVal = v; valleyOffset = i; }
        }
        var bandMean = bandLen > 0 ? sum / bandLen : 0.0;

        if (bandMean <= 0 || minVal > bandMean * 0.5)
        {
            return height / 2;
        }
        return bandStart + valleyOffset;
    }

    /// <summary>Up to 4 image-enhancement variants (original/clahe/sharp/deskew/deskew_clahe,
    /// deduped, capped at 4), each already upscaled via UpscaleForOcr.</summary>
    public static List<(string Name, Mat Image)> OcrImageVariants(Mat image)
    {
        var variants = new List<(string, Mat)>();
        var seen = new HashSet<(int, int, int, int)>();

        void Add(string name, Mat candidate)
        {
            if (candidate.Empty()) return;
            var upscaled = UpscaleForOcr(candidate);
            var key = (upscaled.Rows, upscaled.Cols, (int)Cv2.Mean(upscaled).Val0, (int)PlateSharpness(upscaled));
            if (!seen.Add(key)) return;
            variants.Add((name, upscaled));
        }

        Add("original", image);
        using (var clahe = ClaheBgr(image)) Add("clahe", clahe);
        using (var sharp = SharpenBgr(image)) Add("sharp", sharp);
        var deskewed = DeskewPlateImage(image);
        if (!ReferenceEquals(deskewed, image))
        {
            Add("deskew", deskewed);
            using var deskewedClahe = ClaheBgr(deskewed);
            Add("deskew_clahe", deskewedClahe);
        }

        return variants.Take(4).ToList();
    }
}
