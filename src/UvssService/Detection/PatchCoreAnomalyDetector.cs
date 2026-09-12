using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace UvssService.Detection;

/// <summary>Anomaly-based foreign-object detector -- the alternative to
/// OnnxForeignObjectDetector's supervised approach (which needs real
/// photos of actual threat objects to train on, genuinely hard/dangerous
/// to collect at scale). This is a PatchCore-style detector instead: a
/// PRETRAINED (ImageNet, not custom-trained) ResNet18 backbone
/// (model_conversion/export_patchcore_backbone.py) extracts a grid of
/// local "patch feature vectors" from the chassis image, compared against
/// a "memory bank" of patch vectors collected from known-CLEAN chassis
/// scans only -- BuildMemoryBank never needs to see a single photo of an
/// actual threat object. A patch whose nearest neighbour in that bank is
/// far away (in feature space) is flagged as anomalous -- "doesn't look
/// like anything we've seen on a clean vehicle", not "matches a known
/// threat class". Same technique real industrial visual-defect-inspection
/// systems use (PatchCore/PaDiM family).
///
/// Trade-off versus the supervised YOLO detector: this can flag things
/// that were never explicitly labelled as threats (any genuine deviation
/// from what a clean chassis normally looks like), but it also can't tell
/// you WHAT the anomaly is, and will flag benign but unusual things (mud,
/// an aftermarket part, an unusually-lit shadow) just as readily as an
/// actual threat -- it's a "flag this pass for a human to look closer at"
/// signal, not a final verdict.
///
/// Build the memory bank once via:
///   dotnet run -- --build-anomaly-bank &lt;cleanImagesDir&gt; &lt;outputBankPath&gt;
/// then point ForeignObjectDetector.PatchCoreMemoryBankPath at the result.</summary>
public class PatchCoreAnomalyDetector : IForeignObjectDetector, IDisposable
{
    private const int InputSize = 224;
    private const int FeatureDim = 384;

    private readonly InferenceSession? _session;
    private readonly float[]? _memoryBank;
    private readonly int _memoryBankCount;
    private readonly float _anomalyThreshold;

    public PatchCoreAnomalyDetector(string backbonePath, string memoryBankPath, float anomalyThreshold)
    {
        _anomalyThreshold = anomalyThreshold;

        if (File.Exists(backbonePath))
        {
            try
            {
                _session = new InferenceSession(backbonePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PatchCoreAnomalyDetector: failed to load backbone '{backbonePath}': {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"PatchCoreAnomalyDetector: backbone not found: {backbonePath}.");
        }

        if (File.Exists(memoryBankPath))
        {
            try
            {
                (_memoryBank, _memoryBankCount) = LoadMemoryBank(memoryBankPath);
                Console.WriteLine($"PatchCoreAnomalyDetector: loaded memory bank with {_memoryBankCount} vectors from '{memoryBankPath}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PatchCoreAnomalyDetector: failed to load memory bank '{memoryBankPath}': {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine(
                $"PatchCoreAnomalyDetector: memory bank not found: {memoryBankPath} -- build one first via "
                + "`dotnet run -- --build-anomaly-bank <cleanImagesDir> <outputPath>` (see this class's own remarks).");
        }
    }

    public bool Available => _session != null && _memoryBank != null && _memoryBankCount > 0;

    public List<ForeignObjectDetection> Detect(Mat image)
    {
        if (!Available || image.Empty())
        {
            return new List<ForeignObjectDetection>();
        }

        var (distances, gridH, gridW) = ComputeAnomalyScores(image);
        return ExtractAnomalyRegions(distances, gridH, gridW, image.Cols, image.Rows);
    }

    /// <summary>Per-patch nearest-neighbour distance grid, before
    /// thresholding -- exposed so PatchCoreAnomalyThreshold can be tuned
    /// empirically against real clean/dirty scans from your own site
    /// (there's no universally-correct value, see that option's own
    /// remarks) instead of guessed blind. Not used by Detect() internally
    /// beyond sharing this same computation.</summary>
    public (float[] Distances, int GridH, int GridW) ComputeAnomalyScores(Mat image)
    {
        if (!Available || image.Empty())
        {
            return (Array.Empty<float>(), 0, 0);
        }

        var (features, gridH, gridW) = ExtractFeatures(_session!, image);
        var distances = new float[gridH * gridW];
        var bank = _memoryBank!;
        var bankCount = _memoryBankCount;

        Parallel.For(0, gridH * gridW, i =>
        {
            var offset = i * FeatureDim;
            var minDistSq = float.MaxValue;
            for (var b = 0; b < bankCount; b++)
            {
                var bankOffset = b * FeatureDim;
                var sum = 0f;
                for (var d = 0; d < FeatureDim; d++)
                {
                    var diff = features[offset + d] - bank[bankOffset + d];
                    sum += diff * diff;
                }
                if (sum < minDistSq)
                {
                    minDistSq = sum;
                }
            }
            distances[i] = MathF.Sqrt(minDistSq);
        });

        return (distances, gridH, gridW);
    }

    /// <summary>Runs the backbone and rearranges its [1, FeatureDim, gridH,
    /// gridW] channel-major output into one contiguous FeatureDim-length
    /// vector per spatial position (gridH*gridW of them) -- the shape the
    /// nearest-neighbour search above and BuildMemoryBank below both
    /// need.</summary>
    private static (float[] Features, int GridH, int GridW) ExtractFeatures(InferenceSession session, Mat image)
    {
        using var resized = new Mat();
        Cv2.Resize(image, resized, new Size(InputSize, InputSize));
        var chw = YoloDecoder.MatToChwFloatArray(resized);
        var inputTensor = new DenseTensor<float>(chw, new[] { 1, 3, InputSize, InputSize });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("image", inputTensor) };
        using var results = session.Run(inputs);
        var outTensor = results.First(r => r.Name == "features").AsTensor<float>();
        var gridH = outTensor.Dimensions[2];
        var gridW = outTensor.Dimensions[3];
        var chwData = outTensor.ToArray();

        var hwc = new float[gridH * gridW * FeatureDim];
        for (var c = 0; c < FeatureDim; c++)
        {
            var chwChannelOffset = c * gridH * gridW;
            for (var y = 0; y < gridH; y++)
            {
                var rowOffset = chwChannelOffset + y * gridW;
                for (var x = 0; x < gridW; x++)
                {
                    hwc[(y * gridW + x) * FeatureDim + c] = chwData[rowOffset + x];
                }
            }
        }
        return (hwc, gridH, gridW);
    }

    /// <summary>Thresholds the per-patch anomaly-distance grid, groups
    /// connected above-threshold patches into regions via
    /// FindContours (same "highlight the flagged area" contract
    /// DrawDetectionHighlights in LaneWorkerHostedService already expects
    /// from IForeignObjectDetector, regardless of which implementation
    /// produced it), and maps each region's coordinates from the
    /// low-resolution feature grid back to the original image's own
    /// pixel space.</summary>
    private List<ForeignObjectDetection> ExtractAnomalyRegions(float[] distances, int gridH, int gridW, int origWidth, int origHeight)
    {
        using var heatmap = new Mat(gridH, gridW, MatType.CV_8UC1);
        for (var y = 0; y < gridH; y++)
        {
            for (var x = 0; x < gridW; x++)
            {
                heatmap.Set(y, x, (byte)(distances[y * gridW + x] > _anomalyThreshold ? 255 : 0));
            }
        }

        Cv2.FindContours(heatmap, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var scaleX = origWidth / (float)gridW;
        var scaleY = origHeight / (float)gridH;
        var detections = new List<ForeignObjectDetection>();
        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            if (rect.Width * rect.Height < 2)
            {
                continue; // a single isolated flagged cell is more likely noise than a real region
            }
            var maxDistInRegion = 0f;
            for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            {
                for (var x = rect.X; x < rect.X + rect.Width; x++)
                {
                    maxDistInRegion = Math.Max(maxDistInRegion, distances[y * gridW + x]);
                }
            }
            var mapped = new Rect(
                (int)(rect.X * scaleX), (int)(rect.Y * scaleY),
                Math.Max(1, (int)(rect.Width * scaleX)), Math.Max(1, (int)(rect.Height * scaleY)));
            detections.Add(new ForeignObjectDetection("anomaly", maxDistInRegion, mapped));
        }
        return detections;
    }

    private static (float[] Bank, int Count) LoadMemoryBank(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var dim = reader.ReadInt32();
        var count = reader.ReadInt32();
        if (dim != FeatureDim)
        {
            throw new InvalidOperationException($"Memory bank feature dim {dim} doesn't match expected {FeatureDim} -- rebuild it.");
        }
        var data = new float[count * dim];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = reader.ReadSingle();
        }
        return (data, count);
    }

    /// <summary>Builds a memory bank from a folder of known-CLEAN chassis
    /// images -- no threat examples needed, ever, only normal ones. Randomly
    /// subsamples down to maxVectors total patch vectors (a lightweight
    /// stand-in for the PatchCore paper's greedy coreset subsampling) so
    /// Detect()'s nearest-neighbour search stays fast regardless of how many
    /// clean images get added -- keeping every single patch from every image
    /// would make the bank (and every future Detect() call) grow unbounded
    /// for rapidly diminishing accuracy benefit past a few thousand
    /// representative vectors.</summary>
    public static void BuildMemoryBank(string backbonePath, IEnumerable<string> cleanImagePaths, string outputPath, int maxVectors = 4000)
    {
        using var session = new InferenceSession(backbonePath);
        var allVectors = new List<float[]>();
        var paths = cleanImagePaths.ToList();
        Console.WriteLine($"Building memory bank from {paths.Count} clean image(s)...");
        foreach (var path in paths)
        {
            using var image = Cv2.ImRead(path);
            if (image.Empty())
            {
                Console.WriteLine($"  skipping unreadable image: {path}");
                continue;
            }
            var (features, gridH, gridW) = ExtractFeatures(session, image);
            for (var i = 0; i < gridH * gridW; i++)
            {
                var vec = new float[FeatureDim];
                Array.Copy(features, i * FeatureDim, vec, 0, FeatureDim);
                allVectors.Add(vec);
            }
            Console.WriteLine($"  {Path.GetFileName(path)}: +{gridH * gridW} patch vectors (total so far: {allVectors.Count})");
        }

        if (allVectors.Count == 0)
        {
            throw new InvalidOperationException("No readable clean images found -- memory bank would be empty.");
        }

        var rng = new Random(42);
        var selected = allVectors.Count <= maxVectors
            ? allVectors
            : allVectors.OrderBy(_ => rng.Next()).Take(maxVectors).ToList();

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);
        writer.Write(FeatureDim);
        writer.Write(selected.Count);
        foreach (var vec in selected)
        {
            foreach (var v in vec)
            {
                writer.Write(v);
            }
        }
        Console.WriteLine($"Memory bank saved: {selected.Count} vectors ({FeatureDim} dims each) -> {outputPath}");
    }

    public void Dispose() => _session?.Dispose();
}
