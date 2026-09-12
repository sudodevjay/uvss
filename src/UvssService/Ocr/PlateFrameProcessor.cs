using OpenCvSharp;
using UvssService.Detection;

namespace UvssService.Ocr;

/// <summary>Turns one ANPR frame into a PlateFrameResult (detect -> crop ->
/// full recognize_best pipeline) -- mirrors anpr-ai-service's process_frame
/// for the single-plate-candidate case. process_frame_batch (main.py:1899)
/// is the loop over selected track frames calling this per frame, then
/// PlateAggregation.SelectAggregatedResult combines them.
///
/// Simplification vs. production: only the single highest-confidence plate
/// detection per frame is used (production scores up to 3 candidate
/// detections per frame and picks the best after OCR'ing each) -- that
/// extra per-frame layer only matters when one frame contains multiple
/// candidate plate regions (e.g. more than one vehicle in view), which
/// isn't the common UVSS single-lane case; the temporal multi-frame voting
/// this class feeds into is the piece that actually matters here and is
/// ported in full.</summary>
public static class PlateFrameProcessor
{
    public static PlateFrameResult ProcessFrame(Mat frame, IPlateDetector plateDetector, PlateCandidateSearch search)
    {
        var result = new PlateFrameResult();
        if (!plateDetector.Available || frame.Empty())
        {
            return result;
        }

        var boxes = plateDetector.Detect(frame);
        if (boxes.Count == 0)
        {
            return result;
        }

        var best = boxes.OrderByDescending(b => b.Confidence).First();
        var padded = PadAndClamp(best.BBox, frame.Cols, frame.Rows, paddingRatio: 0.1);
        using var crop = new Mat(frame, padded);
        var metrics = PlateImageVariants.GetPlateCropMetrics(crop);

        result.PlateBBox = best.BBox;
        result.PlateDetectionConfidence = best.Confidence;
        result.PlateCropQuality = PlateImageVariants.PlateCropQualityScore(crop, best.Confidence);
        result.PlateCropSharpness = metrics.Sharpness;
        Cv2.ImEncode(".jpg", crop, out var cropJpeg);
        result.PlateCropJpeg = cropJpeg;

        var (text, confidence, variant) = search.RecognizeBest(crop);
        result.PlateText = text;
        result.PlateConfidence = confidence;
        result.PlateOcrVariant = variant;
        return result;
    }

    public static List<PlateFrameResult> ProcessFrameBatch(IReadOnlyList<Mat> frames, IPlateDetector plateDetector, PlateCandidateSearch search)
    {
        var results = new List<PlateFrameResult>();
        foreach (var frame in frames)
        {
            if (frame.Empty()) continue;
            results.Add(ProcessFrame(frame, plateDetector, search));
        }
        return results;
    }

    private static Rect PadAndClamp(Rect box, int imageWidth, int imageHeight, double paddingRatio)
    {
        var padX = (int)(box.Width * paddingRatio);
        var padY = (int)(box.Height * paddingRatio);
        var x1 = Math.Max(0, box.X - padX);
        var y1 = Math.Max(0, box.Y - padY);
        var x2 = Math.Min(imageWidth, box.X + box.Width + padX);
        var y2 = Math.Min(imageHeight, box.Y + box.Height + padY);
        return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }
}
