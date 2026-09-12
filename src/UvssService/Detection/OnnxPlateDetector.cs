using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace UvssService.Detection;

/// <summary>ONNX export of anpr-ai-service's weights/best.pt (ultralytics
/// YOLO, single class "License_Plate"). Input "images" [1,3,640,640],
/// output "output0" [1,5,8400] (4 box coords + 1 class score) -- confirmed
/// via onnxruntime inspection of the exported model.</summary>
public class OnnxPlateDetector : IPlateDetector, IDisposable
{
    private const int InputSize = 640;
    private readonly InferenceSession? _session;
    private readonly float _confidence;
    private readonly float _iou;

    public OnnxPlateDetector(string modelPath, float confidence = 0.4f, float iou = 0.45f)
    {
        _confidence = confidence;
        _iou = iou;
        if (File.Exists(modelPath))
        {
            try
            {
                _session = new InferenceSession(modelPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Plate detector failed to load '{modelPath}': {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Plate detector model not found: {modelPath} -- detection will report unavailable.");
        }
    }

    public bool Available => _session != null;

    public List<YoloBox> Detect(Mat frame)
    {
        if (_session == null || frame.Empty())
        {
            return new List<YoloBox>();
        }

        var letterbox = YoloDecoder.ResizeLetterbox(frame, InputSize);
        var chw = YoloDecoder.MatToChwFloatArray(letterbox.Image);
        var inputTensor = new DenseTensor<float>(chw, new[] { 1, 3, InputSize, InputSize });

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", inputTensor) };
        using var results = _session.Run(inputs);
        var output = results.First(r => r.Name == "output0").AsTensor<float>().ToArray();

        return YoloDecoder.Decode(output, numClasses: 1, numBoxes: 8400, _confidence, _iou, letterbox, InputSize);
    }

    public void Dispose() => _session?.Dispose();
}
