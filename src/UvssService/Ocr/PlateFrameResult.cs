using OpenCvSharp;

namespace UvssService.Ocr;

/// <summary>One frame's plate-OCR result -- mirrors the per-frame result
/// dict fields anpr-ai-service's process_frame/select_aggregated_result
/// read (main.py:1806-1896), trimmed to what UVSS actually needs (no
/// vehicle_type/vehicle_color classification here).</summary>
public class PlateFrameResult
{
    public Rect? PlateBBox;
    public double PlateDetectionConfidence;
    public string PlateText = "";
    public double PlateConfidence;
    public double PlateCropQuality;
    public double PlateCropSharpness;
    public string PlateOcrVariant = "";
    /// <summary>JPEG bytes of the actual cropped plate image OCR read --
    /// carried through so the dashboard can show what the OCR "saw", not
    /// just the decoded text.</summary>
    public byte[]? PlateCropJpeg;
}
