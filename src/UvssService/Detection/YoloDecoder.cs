using OpenCvSharp;

namespace UvssService.Detection;

public record YoloBox(int ClassId, float Confidence, Rect BBox);

/// <summary>Shared YOLOv8-style ONNX decode: letterbox preprocessing (resize
/// preserving aspect ratio + pad, matching ultralytics' own default
/// inference preprocessing so exported-model coordinates line up), output
/// tensor layout [1, 4+numClasses, numBoxes] (box in cx,cy,w,h, model-input
/// pixel units), confidence filtering, and greedy NMS. Used by both
/// OnnxPlateDetector and OnnxVehicleDetector -- same export format
/// (`model.export(format="onnx")` via ultralytics), different trained
/// weights/class counts.</summary>
public static class YoloDecoder
{
    public record Letterbox(Mat Image, float Scale, int PadX, int PadY);

    public static Letterbox ResizeLetterbox(Mat src, int targetSize)
    {
        var scale = Math.Min(targetSize / (float)src.Rows, targetSize / (float)src.Cols);
        var newW = (int)Math.Round(src.Cols * scale);
        var newH = (int)Math.Round(src.Rows * scale);
        using var resized = new Mat();
        Cv2.Resize(src, resized, new Size(newW, newH));

        var padX = (targetSize - newW) / 2;
        var padY = (targetSize - newH) / 2;
        var canvas = new Mat(targetSize, targetSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
        resized.CopyTo(canvas[new Rect(padX, padY, newW, newH)]);
        return new Letterbox(canvas, scale, padX, padY);
    }

    public static float[] MatToChwFloatArray(Mat bgrImage)
    {
        using var rgb = new Mat();
        Cv2.CvtColor(bgrImage, rgb, ColorConversionCodes.BGR2RGB);
        using var f = new Mat();
        rgb.ConvertTo(f, MatType.CV_32FC3, 1.0 / 255.0);

        var h = f.Rows;
        var w = f.Cols;
        var chw = new float[3 * h * w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var px = f.At<Vec3f>(y, x);
                chw[0 * h * w + y * w + x] = px.Item0; // R
                chw[1 * h * w + y * w + x] = px.Item1; // G
                chw[2 * h * w + y * w + x] = px.Item2; // B
            }
        }
        return chw;
    }

    /// <param name="output">Flat [4+numClasses, numBoxes] tensor data (batch dim already stripped).</param>
    public static List<YoloBox> Decode(
        float[] output, int numClasses, int numBoxes,
        float confidenceThreshold, float iouThreshold,
        Letterbox letterbox, int targetSize, IReadOnlyCollection<int>? classFilter = null)
    {
        var stride = numBoxes;
        var candidates = new List<YoloBox>();

        for (var i = 0; i < numBoxes; i++)
        {
            var bestClass = -1;
            var bestScore = 0f;
            for (var c = 0; c < numClasses; c++)
            {
                var score = output[(4 + c) * stride + i];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }
            if (bestScore < confidenceThreshold || bestClass < 0) continue;
            if (classFilter != null && !classFilter.Contains(bestClass)) continue;

            var cx = output[0 * stride + i];
            var cy = output[1 * stride + i];
            var w = output[2 * stride + i];
            var h = output[3 * stride + i];

            // Undo letterbox: model-input pixel coords -> original image coords.
            var x1 = (cx - w / 2 - letterbox.PadX) / letterbox.Scale;
            var y1 = (cy - h / 2 - letterbox.PadY) / letterbox.Scale;
            var x2 = (cx + w / 2 - letterbox.PadX) / letterbox.Scale;
            var y2 = (cy + h / 2 - letterbox.PadY) / letterbox.Scale;

            candidates.Add(new YoloBox(bestClass, bestScore, new Rect(
                (int)Math.Round(x1), (int)Math.Round(y1),
                (int)Math.Round(x2 - x1), (int)Math.Round(y2 - y1))));
        }

        return Nms(candidates, iouThreshold);
    }

    private static List<YoloBox> Nms(List<YoloBox> boxes, float iouThreshold)
    {
        var sorted = boxes.OrderByDescending(b => b.Confidence).ToList();
        var kept = new List<YoloBox>();
        while (sorted.Count > 0)
        {
            var best = sorted[0];
            kept.Add(best);
            sorted.RemoveAt(0);
            sorted.RemoveAll(b => Iou(b.BBox, best.BBox) > iouThreshold);
        }
        return kept;
    }

    private static float Iou(Rect a, Rect b)
    {
        var inter = a.Intersect(b);
        var interArea = inter.Width > 0 && inter.Height > 0 ? inter.Width * inter.Height : 0;
        var unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
        return unionArea <= 0 ? 0 : interArea / (float)unionArea;
    }
}
