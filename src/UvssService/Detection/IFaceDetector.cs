using OpenCvSharp;

namespace UvssService.Detection;

public interface IFaceDetector
{
    bool Available { get; }
    List<Rect> Detect(Mat image);
}
