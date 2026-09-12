using OpenCvSharp;

namespace UvssService.Detection;

public record ForeignObjectDetection(string Label, float Confidence, Rect BBox);

/// <summary>Foreign-object detector for the stitched under-vehicle image.
/// No trained model exists yet -- same graceful-degradation pattern as
/// anpr-ai-service's PlateDetector: if the model file isn't present,
/// Available is false and Detect() returns an empty list instead of
/// throwing, so the rest of the pipeline keeps working while the model
/// itself is trained/exported later.</summary>
public interface IForeignObjectDetector
{
    bool Available { get; }
    List<ForeignObjectDetection> Detect(Mat image);
}
