using OpenCvSharp;

namespace UvssService.Ocr;

/// <summary>Fast (CTC-based) single-line plate OCR -- used to cheaply score
/// ~6 crop/enhancement variants per frame before the much slower SAR head
/// (IPlateOcr) refines only the single winning crop. Mirrors
/// anpr-ai-service's PlateOCR.recognize (CTC path).</summary>
public interface ICtcOcr
{
    bool Available { get; }
    (string Text, float Confidence) Recognize(Mat img);
}
