using OpenCvSharp;

namespace UvssService.Detection;

/// <summary>Classical Viola-Jones Haar-cascade face detector (OpenCV's own
/// bundled haarcascade_frontalface_default.xml -- BSD licensed, ships with
/// every OpenCV install, no training/download needed). Less accurate than a
/// modern deep-learning face detector, but real, immediately usable, and a
/// perfectly reasonable first cut for "is there a clear frontal face here"
/// -- unlike the foreign-object detector, there's no missing-model problem
/// to work around here.</summary>
public class CascadeFaceDetector : IFaceDetector
{
    private readonly CascadeClassifier? _classifier;

    public CascadeFaceDetector(string cascadePath)
    {
        if (File.Exists(cascadePath))
        {
            try
            {
                _classifier = new CascadeClassifier(cascadePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Face detector failed to load '{cascadePath}': {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Face detector cascade not found: {cascadePath} -- driver best-shot selection will report unavailable.");
        }
    }

    public bool Available => _classifier != null;

    public List<Rect> Detect(Mat image)
    {
        if (_classifier == null || image.Empty())
        {
            return new List<Rect>();
        }
        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);
        return _classifier.DetectMultiScale(
            gray, scaleFactor: 1.1, minNeighbors: 5, minSize: new Size(60, 60)).ToList();
    }
}
