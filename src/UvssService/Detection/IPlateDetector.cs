using OpenCvSharp;

namespace UvssService.Detection;

/// <summary>Finds the plate's bounding box within a full vehicle/ANPR-camera
/// frame -- this MUST run before OCR: OnnxPlateOcr reads a cropped plate
/// image, it doesn't locate the plate itself. Mirrors anpr-ai-service's
/// PlateDetector (weights/best.pt), exported to ONNX so it runs natively
/// in-process here, same as OnnxPlateOcr.</summary>
public interface IPlateDetector
{
    bool Available { get; }
    List<YoloBox> Detect(Mat frame);
}
