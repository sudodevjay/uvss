using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace UvssService.Detection;

/// <summary>ONNX export of a YOLOv8n foreign-object detector trained via
/// model_conversion/foreign_object_training (ultralytics, same recipe as
/// anpr-ai-service's own detectors) -- single class "foreign_object",
/// input "images" [1,3,640,640], output "output0" [1,5,8400] (4 box coords
/// + 1 class score). Same letterbox/decode machinery as OnnxVehicleDetector
/// and OnnxPlateDetector (YoloDecoder), just with numClasses=1 and no class
/// filter (there's only the one class to keep).
///
/// IMPORTANT: the currently-deployed weights/foreign_object_detector.onnx
/// (if present) may be the SYNTHETIC pipeline-verification model from
/// foreign_object_training's generate_synthetic_dataset.py -- it proves
/// this train -> export -> decode path works end-to-end, but it was never
/// shown a real foreign object and has no real-world detection ability.
/// Replace it with a model trained on genuine labeled UVSS imagery before
/// relying on this for actual screening.</summary>
public class OnnxForeignObjectDetector : IForeignObjectDetector, IDisposable
{
    private const int InputSize = 640;
    private const float IouThreshold = 0.45f;

    private readonly InferenceSession? _session;
    private readonly float _confidence;

    public OnnxForeignObjectDetector(string modelPath, float confidence)
    {
        _confidence = confidence;
        if (File.Exists(modelPath))
        {
            try
            {
                _session = new InferenceSession(modelPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Foreign-object detector failed to load '{modelPath}': {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine(
                $"Foreign-object detector model not found: {modelPath} "
                + "(no model trained yet -- detection will report unavailable)."
            );
        }
    }

    public bool Available => _session != null;

    public List<ForeignObjectDetection> Detect(Mat image)
    {
        if (_session == null || image.Empty())
        {
            return new List<ForeignObjectDetection>();
        }

        var letterbox = YoloDecoder.ResizeLetterbox(image, InputSize);
        var chw = YoloDecoder.MatToChwFloatArray(letterbox.Image);
        var inputTensor = new DenseTensor<float>(chw, new[] { 1, 3, InputSize, InputSize });

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", inputTensor) };
        using var results = _session.Run(inputs);
        var output = results.First(r => r.Name == "output0").AsTensor<float>().ToArray();

        var boxes = YoloDecoder.Decode(output, numClasses: 1, numBoxes: 8400, _confidence, IouThreshold, letterbox, InputSize);
        return boxes.Select(b => new ForeignObjectDetection("foreign_object", b.Confidence, b.BBox)).ToList();
    }

    public void Dispose() => _session?.Dispose();
}
