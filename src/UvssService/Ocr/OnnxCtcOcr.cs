using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace UvssService.Ocr;

/// <summary>Fast CTC plate-recognition path -- used by PlateCandidateSearch
/// to cheaply score ~6 crop/enhancement variants per frame before SAR
/// (OnnxPlateOcr) refines only the single winning crop. Loads the same
/// combined backbone graph as OnnxPlateOcr (ctc_backbone_and_head.onnx) but
/// reads its `ctc_logits` output instead of feat/attn_key/holistic_feat --
/// exact C# port of anpr-ai-service's PlateOCR.recognize/_decode_ctc,
/// including the repeated-character-recovery step plain greedy CTC decode
/// would otherwise miss (e.g. the "99" in "9924" collapsing to one "9").</summary>
public class OnnxCtcOcr : ICtcOcr, IDisposable
{
    private readonly InferenceSession? _session;
    private readonly string[] _characterList = Array.Empty<string>();

    public OnnxCtcOcr(string backboneModelPath, string charDictPath)
    {
        if (File.Exists(backboneModelPath) && File.Exists(charDictPath))
        {
            try
            {
                _session = new InferenceSession(backboneModelPath);
                var dictLines = File.ReadAllLines(charDictPath);
                var chars = new List<string> { "blank" };
                chars.AddRange(dictLines);
                chars.Add(" ");
                _characterList = chars.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CTC OCR failed to load: {ex.Message}");
                _session?.Dispose();
                _session = null;
            }
        }
        else
        {
            Console.WriteLine(
                $"CTC OCR model files not found (expected: {backboneModelPath}, {charDictPath}) "
                + "-- fast CTC search will report unavailable."
            );
        }
    }

    public bool Available => _session != null;

    public (string Text, float Confidence) Recognize(Mat img)
    {
        if (!Available || img.Empty())
        {
            return ("", 0f);
        }

        var (inputTensor, _, validRatio) = OcrPreprocessing.ResizeNorm(img);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("valid_ratio", new DenseTensor<float>(new[] { validRatio }, new[] { 1 })),
        };
        using var results = _session!.Run(inputs);
        var logits = results.First(r => r.Name == "ctc_logits").AsTensor<float>();

        var timesteps = logits.Dimensions[1];
        var numClasses = logits.Dimensions[2];
        var predsIdx = new int[timesteps];
        var predsProb = new float[timesteps];
        for (var t = 0; t < timesteps; t++)
        {
            var best = 0;
            var bestVal = float.MinValue;
            for (var c = 0; c < numClasses; c++)
            {
                var v = logits[0, t, c];
                if (v > bestVal) { bestVal = v; best = c; }
            }
            predsIdx[t] = best;
            predsProb[t] = bestVal;
        }

        var (chars, confs) = DecodeCtc(predsIdx, predsProb);
        var text = string.Concat(chars);
        var confidence = confs.Count > 0 ? confs.Average() : 0f;
        return (text, confidence);
    }

    /// <summary>Exact port of PlateOCR._decode_ctc: groups consecutive
    /// identical predicted classes into runs, drops blank (class 0) runs,
    /// then recovers genuinely-doubled characters (a run >= a
    /// this-sequence's-own median-scaled threshold counts as two repeated
    /// characters instead of the usual one) -- see the Python docstring for
    /// why a fixed threshold doesn't work across differently-sized crops.</summary>
    private (List<string> Chars, List<float> Confs) DecodeCtc(int[] predsIdx, float[] predsProb)
    {
        var runs = new List<(int ClassIdx, int Length, float Prob)>();
        var n = predsIdx.Length;
        var i = 0;
        while (i < n)
        {
            var j = i;
            while (j < n && predsIdx[j] == predsIdx[i]) j++;
            if (predsIdx[i] != 0)
            {
                runs.Add((predsIdx[i], j - i, predsProb[i]));
            }
            i = j;
        }

        var nonBlankLengths = runs.Select(r => r.Length).OrderBy(l => l).ToList();
        int repeatThreshold;
        if (nonBlankLengths.Count > 0)
        {
            var medianLength = nonBlankLengths[nonBlankLengths.Count / 2];
            repeatThreshold = Math.Max(3, (int)Math.Round(medianLength * 1.7));
        }
        else
        {
            repeatThreshold = 3;
        }

        var chars = new List<string>();
        var confs = new List<float>();
        foreach (var (classIdx, length, prob) in runs)
        {
            var repeats = length >= repeatThreshold ? 2 : 1;
            var ch = classIdx >= 0 && classIdx < _characterList.Length ? _characterList[classIdx] : "";
            for (var r = 0; r < repeats; r++)
            {
                chars.Add(ch);
                confs.Add(prob);
            }
        }
        return (chars, confs);
    }

    public void Dispose() => _session?.Dispose();
}
