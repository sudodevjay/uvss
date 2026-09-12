using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace UvssService.Ocr;

/// <summary>Runs the trained PP-OCRv3 SAR plate-recognition model natively
/// in-process via ONNX Runtime -- no HTTP call to anpr-ai-service, no
/// separate Python process. The two ONNX graphs this loads were hand-built
/// (paddle2onnx itself is a confirmed dead end on Windows for this model --
/// see uvss-service/model_conversion/SAR_ONNX_NOTES.md) from the exact
/// weights already trained and deployed in anpr-ai-service
/// (weights/indian_plate_rec_v2_sar_best_accuracy.pdparams), and validated
/// to reproduce that model's output to within float32 rounding noise
/// (~1e-6) across 15 diverse test crops.
///
/// This is the SAR path specifically (not CTC) -- SAR is what actually
/// produces the accurate reading in production (98.81% exact-match vs
/// CTC's 87.75%, per anpr-ai-service/sar_refiner.py's own docstring). See
/// OnnxCtcOcr for the fast CTC path used by PlateCandidateSearch's
/// multi-crop-variant search -- both share the same backbone ONNX graph
/// (ctc_backbone_and_head.onnx), so the backbone only runs once per crop
/// even though this class only reads its SAR-relevant outputs.
///
/// The decode loop below runs the decoder graph incrementally, one
/// timestep at a time, carrying LSTM state forward -- NOT the reference
/// Python implementation's more expensive "recompute the whole padded
/// sequence through the LSTM at every step" form. These are mathematically
/// equivalent for this unidirectional/causal LSTM, and that equivalence is
/// now empirically confirmed (not just assumed) by the validation in
/// SAR_ONNX_NOTES.md -- do not "fix" this back to the expensive form.</summary>
public class OnnxPlateOcr : IPlateOcr, IDisposable
{
    private const int MaxDecodeSteps = 25;
    private const int StartIdx = 97; // shared BOS/EOS token
    private const int EndIdx = 97;
    private const int PaddingIdx = 98;
    private const int EmbeddingDim = 512;
    private const int VocabSize = 99;

    private readonly InferenceSession? _backboneSession;
    private readonly InferenceSession? _decoderSession;
    private readonly float[,]? _embedding; // [99, 512]
    private readonly string[] _characterList = Array.Empty<string>();

    public OnnxPlateOcr(string backboneModelPath, string decoderModelPath, string embeddingPath, string charDictPath)
    {
        if (File.Exists(backboneModelPath) && File.Exists(decoderModelPath) && File.Exists(embeddingPath) && File.Exists(charDictPath))
        {
            try
            {
                _backboneSession = new InferenceSession(backboneModelPath);
                _decoderSession = new InferenceSession(decoderModelPath);
                _embedding = LoadEmbedding(embeddingPath);
                _characterList = BuildCharacterList(charDictPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Plate OCR failed to load models: {ex.Message}");
                _backboneSession?.Dispose();
                _decoderSession?.Dispose();
                _backboneSession = null;
                _decoderSession = null;
                _embedding = null;
            }
        }
        else
        {
            Console.WriteLine(
                "Plate OCR model files not found (expected: "
                + $"{backboneModelPath}, {decoderModelPath}, {embeddingPath}, {charDictPath}) "
                + "-- OCR will report unavailable."
            );
        }
    }

    public bool Available => _backboneSession != null && _decoderSession != null && _embedding != null;

    public (string Text, float Confidence) RecognizeDoubleLine(Mat topRow, Mat bottomRow)
    {
        var (topText, topConfidence) = Recognize(topRow);
        var (bottomText, bottomConfidence) = Recognize(bottomRow);
        if (topText.Length == 0 && bottomText.Length == 0)
        {
            return ("", 0f);
        }
        var confidences = new List<float>();
        if (topConfidence > 0) confidences.Add(topConfidence);
        if (bottomConfidence > 0) confidences.Add(bottomConfidence);
        var confidence = confidences.Count > 0 ? confidences.Average() : 0f;
        return (topText + bottomText, confidence);
    }

    public (string Text, float Confidence) Recognize(Mat plateCrop)
    {
        if (!Available || plateCrop.Empty())
        {
            return ("", 0f);
        }

        var (inputTensor, imgW, validRatio) = OcrPreprocessing.ResizeNorm(plateCrop);

        var backboneInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("valid_ratio", new DenseTensor<float>(new[] { validRatio }, new[] { 1 })),
        };
        using var backboneResults = _backboneSession!.Run(backboneInputs);
        var feat = backboneResults.First(r => r.Name == "feat").AsTensor<float>();
        var attnKey = backboneResults.First(r => r.Name == "attn_key").AsTensor<float>();
        var holisticFeat = backboneResults.First(r => r.Name == "holistic_feat").AsTensor<float>();

        var holisticArr = new float[EmbeddingDim];
        for (var i = 0; i < EmbeddingDim; i++) holisticArr[i] = holisticFeat[0, i];

        var h0 = new float[EmbeddingDim];
        var c0 = new float[EmbeddingDim];
        var h1 = new float[EmbeddingDim];
        var c1 = new float[EmbeddingDim];

        // Priming call: seed the decoder LSTM state with the encoder's own
        // output vector (not a token embedding) -- its logits are discarded.
        var (_, h0p, c0p, h1p, c1p) = RunDecoderStep(holisticArr, h0, c0, h1, c1, feat, attnKey, holisticFeat, validRatio);
        h0 = h0p; c0 = c0p; h1 = h1p; c1 = c1p;

        var prevToken = GetEmbeddingRow(StartIdx);
        var sb = new System.Text.StringBuilder();
        var confidences = new List<float>();

        for (var step = 0; step < MaxDecodeSteps; step++)
        {
            var (logits, h0n, c0n, h1n, c1n) = RunDecoderStep(prevToken, h0, c0, h1, c1, feat, attnKey, holisticFeat, validRatio);
            h0 = h0n; c0 = c0n; h1 = h1n; c1 = c1n;

            var pred = ArgMax(logits);
            if (pred == EndIdx)
            {
                break;
            }
            if (pred != PaddingIdx)
            {
                if (pred >= 0 && pred < _characterList.Length)
                {
                    sb.Append(_characterList[pred]);
                }
                confidences.Add(Softmax(logits)[pred]);
            }
            prevToken = GetEmbeddingRow(pred);
        }

        var confidence = confidences.Count > 0 ? confidences.Average() : 0f;
        return (sb.ToString(), confidence);
    }

    private (float[] Logits, float[] H0, float[] C0, float[] H1, float[] C1) RunDecoderStep(
        float[] prevEmbedding, float[] h0, float[] c0, float[] h1, float[] c1,
        Tensor<float> feat, Tensor<float> attnKey, Tensor<float> holisticFeat, float validRatio)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("prev_embedding", new DenseTensor<float>(prevEmbedding, new[] { 1, EmbeddingDim })),
            NamedOnnxValue.CreateFromTensor("h0_in", new DenseTensor<float>(h0, new[] { 1, EmbeddingDim })),
            NamedOnnxValue.CreateFromTensor("c0_in", new DenseTensor<float>(c0, new[] { 1, EmbeddingDim })),
            NamedOnnxValue.CreateFromTensor("h1_in", new DenseTensor<float>(h1, new[] { 1, EmbeddingDim })),
            NamedOnnxValue.CreateFromTensor("c1_in", new DenseTensor<float>(c1, new[] { 1, EmbeddingDim })),
            NamedOnnxValue.CreateFromTensor("feat", feat.ToDenseTensor()),
            NamedOnnxValue.CreateFromTensor("attn_key", attnKey.ToDenseTensor()),
            NamedOnnxValue.CreateFromTensor("holistic_feat", holisticFeat.ToDenseTensor()),
            NamedOnnxValue.CreateFromTensor("valid_ratio", new DenseTensor<float>(new[] { validRatio }, new[] { 1 })),
        };
        using var results = _decoderSession!.Run(inputs);
        var logits = results.First(r => r.Name == "logits").AsTensor<float>().ToArray();
        var h0Out = results.First(r => r.Name == "h0_out").AsTensor<float>().ToArray();
        var c0Out = results.First(r => r.Name == "c0_out").AsTensor<float>().ToArray();
        var h1Out = results.First(r => r.Name == "h1_out").AsTensor<float>().ToArray();
        var c1Out = results.First(r => r.Name == "c1_out").AsTensor<float>().ToArray();
        return (logits, h0Out, c0Out, h1Out, c1Out);
    }

    private float[] GetEmbeddingRow(int index)
    {
        var row = new float[EmbeddingDim];
        for (var i = 0; i < EmbeddingDim; i++) row[i] = _embedding![index, i];
        return row;
    }

    private static float[,] LoadEmbedding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        var table = new float[VocabSize, EmbeddingDim];
        for (var r = 0; r < VocabSize; r++)
        {
            for (var c = 0; c < EmbeddingDim; c++)
            {
                table[r, c] = floats[r * EmbeddingDim + c];
            }
        }
        return table;
    }

    /// <summary>Mirrors CTCLabelDecode/SARLabelDecode's shared base
    /// character-list construction: en_dict.txt lines + space, plus SAR's
    /// three extra tokens (unknown, shared BOS/EOS, padding) appended in
    /// that fixed order to reach the 99-entry vocab the embedding table
    /// and decoder were trained against.</summary>
    private static string[] BuildCharacterList(string charDictPath)
    {
        var dictLines = File.ReadAllLines(charDictPath);
        var list = new List<string>(dictLines) { " ", "<UKN>", "<BOS/EOS>", "<PAD>" };
        return list.ToArray();
    }

    private static int ArgMax(float[] values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best]) best = i;
        }
        return best;
    }

    private static float[] Softmax(float[] logits)
    {
        var max = logits.Max();
        var exps = logits.Select(v => (float)Math.Exp(v - max)).ToArray();
        var sum = exps.Sum();
        return exps.Select(v => v / sum).ToArray();
    }

    public void Dispose()
    {
        _backboneSession?.Dispose();
        _decoderSession?.Dispose();
    }
}
