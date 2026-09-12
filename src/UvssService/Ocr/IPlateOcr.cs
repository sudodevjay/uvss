namespace UvssService.Ocr;

public interface IPlateOcr
{
    bool Available { get; }

    /// <summary>Reads the plate text out of a cropped plate image (BGR,
    /// already cropped to roughly the plate's bounding box -- see
    /// Detection.IPlateDetector). Returns ("", 0) if unavailable or the
    /// image is unreadable.</summary>
    (string Text, float Confidence) Recognize(OpenCvSharp.Mat plateCrop);

    /// <summary>Two-line plate variant (bike/truck: state+RTO on top row,
    /// series+number on bottom row) -- reads each row separately and
    /// concatenates, mirroring anpr-ai-service's
    /// SARRefiner.recognize_double_line.</summary>
    (string Text, float Confidence) RecognizeDoubleLine(OpenCvSharp.Mat topRow, OpenCvSharp.Mat bottomRow);
}
