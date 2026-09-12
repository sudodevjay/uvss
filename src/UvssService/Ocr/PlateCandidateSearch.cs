using OpenCvSharp;

namespace UvssService.Ocr;

public class PlateCandidate
{
    public required string Text;
    public required string RawText;
    public required double Confidence;
    public required string Variant;
    public required double FormatScore;
    public required bool Valid;
    public required double Score;
    /// <summary>Single-rectangle crop (non-double-line candidates only) --
    /// kept only so RecognizeBest can hand the SAR refiner the exact crop
    /// CTC's fast search already picked as best.</summary>
    public Mat? Crop;
    public (Mat Top, Mat Bottom)? DoubleLineCrop;
}

/// <summary>Exact C# port of anpr-ai-service/main.py's PlateOCR.recognize_candidates
/// / recognize_best -- the multi-crop-variant fast (CTC) search over up to
/// OCR_MAX_VARIANTS (6) image-enhancement x crop-trim combinations, reranked
/// by cross-crop-framing agreement, with SAR refinement of only the single
/// winning crop. Same score weights/formulas as the Python original so this
/// produces the same candidate rankings on the same input.</summary>
public class PlateCandidateSearch
{
    private static readonly (string Tag, double TopRatio, double BottomRatio)[] CropVariants =
    {
        ("original", 0.00, 1.00),
        ("trim10", 0.10, 0.90),
        ("trim18", 0.18, 0.88),
        ("trim25", 0.25, 0.86),
        ("center60", 0.20, 0.80),
        ("center50", 0.25, 0.75),
    };

    private readonly ICtcOcr _ctcOcr;
    private readonly IPlateOcr _plateOcr;

    public PlateCandidateSearch(ICtcOcr ctcOcr, IPlateOcr plateOcr)
    {
        _ctcOcr = ctcOcr;
        _plateOcr = plateOcr;
    }

    public (string RawText, double Confidence, Mat? Top, Mat? Bottom) RecognizeDoubleLine(Mat img)
    {
        if (img.Empty()) return ("", 0.0, null, null);
        var height = img.Rows;
        if (height < 20) return ("", 0.0, null, null);

        var mid = PlateImageVariants.FindDoubleLineSplit(img);
        var overlap = Math.Max((int)(height * 0.04), 2);
        var topEnd = Math.Min(mid + overlap, height);
        var bottomStart = Math.Max(mid - overlap, 0);
        var topRow = new Mat(img, new Rect(0, 0, img.Cols, topEnd));
        var bottomRow = new Mat(img, new Rect(0, bottomStart, img.Cols, height - bottomStart));

        var topUp = PlateImageVariants.UpscaleForOcr(topRow);
        var bottomUp = PlateImageVariants.UpscaleForOcr(bottomRow);
        var (topText, topConfidence) = _ctcOcr.Recognize(topUp);
        var (bottomText, bottomConfidence) = _ctcOcr.Recognize(bottomUp);
        topText = PlateGrammar.NormalizePlateText(topText);
        bottomText = PlateGrammar.NormalizePlateText(bottomText);
        if (topText.Length == 0 && bottomText.Length == 0)
        {
            return ("", 0.0, topRow, bottomRow);
        }

        var confidences = new List<double>();
        if (topConfidence > 0) confidences.Add(topConfidence);
        if (bottomConfidence > 0) confidences.Add(bottomConfidence);
        var confidence = confidences.Count > 0 ? confidences.Average() : 0.0;
        return (topText + bottomText, confidence, topRow, bottomRow);
    }

    public List<PlateCandidate> RecognizeCandidates(Mat img)
    {
        if (img.Empty()) return new List<PlateCandidate>();

        var candidates = new List<PlateCandidate>();
        var seen = new HashSet<(string, string, string)>();
        var attempts = 0;
        var cropMetrics = PlateImageVariants.GetPlateCropMetrics(img);
        var tryDoubleLine = cropMetrics.IsDoubleLine;

        void AddCandidate(string rawText, double confidence, string variantTag, Mat? crop, (Mat, Mat)? doubleCrop)
        {
            var text = PlateGrammar.NormalizePlateText(rawText);
            if (text.Length == 0) return;
            foreach (var (correctedText, candidateFormatScore, correctionTag) in PlateGrammar.CorrectedPlateCandidates(text))
            {
                var key = (correctedText, variantTag, correctionTag);
                if (!seen.Add(key)) continue;
                var validityScore = PlateGrammar.IsValidPlateFormat(correctedText) ? 1.0 : 0.0;
                var score = confidence * 0.68 + candidateFormatScore * 0.22 + validityScore * 0.10
                    - PlateGrammar.PlateLengthPenalty(correctedText);
                candidates.Add(new PlateCandidate
                {
                    Text = correctedText,
                    RawText = text,
                    Confidence = confidence,
                    Variant = $"{variantTag}:{correctionTag}",
                    FormatScore = candidateFormatScore,
                    Valid = validityScore > 0,
                    Score = score,
                    Crop = crop,
                    DoubleLineCrop = doubleCrop,
                });
            }
        }

        foreach (var (imageVariant, variantImage) in PlateImageVariants.OcrImageVariants(img))
        {
            if (tryDoubleLine)
            {
                attempts++;
                var (rawText, confidence, topRow, bottomRow) = RecognizeDoubleLine(variantImage);
                if (topRow != null && bottomRow != null)
                {
                    AddCandidate(rawText, confidence, $"{imageVariant}:double_line", null, (topRow, bottomRow));
                }
            }

            foreach (var (cropTag, topRatio, bottomRatio) in CropVariants)
            {
                if (attempts >= PlateImageVariants.OcrMaxVariants) break;

                var h = variantImage.Rows;
                var top = (int)(h * topRatio);
                var bottom = (int)(h * bottomRatio);
                if (bottom <= top) continue;
                var crop = new Mat(variantImage, new Rect(0, top, variantImage.Cols, bottom - top));
                if (crop.Empty()) continue;

                attempts++;
                var (rawText, confidence) = _ctcOcr.Recognize(crop);
                AddCandidate(rawText, confidence, $"{imageVariant}:{cropTag}", crop, null);
            }
            if (attempts >= PlateImageVariants.OcrMaxVariants) break;
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        return RerankByCropAgreement(candidates);
    }

    /// <summary>Rewards a plate text that multiple *different crop framings*
    /// (not just color/contrast variants of the same framing) independently
    /// converge on, so a single high-confidence misread crop can't beat a
    /// reading several differently-framed crops agree on. See
    /// main.py's _rerank_by_crop_agreement for the exact reasoning.</summary>
    private static List<PlateCandidate> RerankByCropAgreement(List<PlateCandidate> candidates)
    {
        if (candidates.Count == 0) return candidates;

        var groups = new List<(string Representative, List<PlateCandidate> Members, HashSet<string> CropVariants)>();
        foreach (var candidate in candidates)
        {
            var parts = candidate.Variant.Split(':', 3);
            var cropVariant = parts.Length > 1 ? parts[1] : candidate.Variant;

            var matched = false;
            foreach (var group in groups)
            {
                if (PlateGrammar.SimilarPlateText(candidate.Text, group.Representative))
                {
                    group.Members.Add(candidate);
                    group.CropVariants.Add(cropVariant);
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                groups.Add((candidate.Text, new List<PlateCandidate> { candidate }, new HashSet<string> { cropVariant }));
            }
        }

        double GroupRank((string Representative, List<PlateCandidate> Members, HashSet<string> CropVariants) group)
        {
            var best = group.Members.OrderByDescending(c => c.Score).First();
            var supportBonus = Math.Min(group.CropVariants.Count - 1, 4) * 0.03;
            return best.Score + supportBonus;
        }

        var bestGroup = groups.OrderByDescending(GroupRank).First();
        var originalWinner = bestGroup.Members.OrderByDescending(c => c.Score).First();
        var winner = originalWinner;

        var memberLengths = bestGroup.Members.Select(m => m.Text.Length).Distinct().Count();
        var swapped = false;
        if (memberLengths > 1)
        {
            var consensusText = PlateGrammar.ConsensusPlateText(bestGroup.Members.Select(m => m.Text).ToList());
            if (consensusText.Length > 0 && consensusText.Length >= winner.Text.Length && consensusText != winner.Text)
            {
                winner = new PlateCandidate
                {
                    Text = consensusText,
                    RawText = originalWinner.RawText,
                    Confidence = originalWinner.Confidence,
                    Variant = originalWinner.Variant,
                    FormatScore = originalWinner.FormatScore,
                    Valid = originalWinner.Valid,
                    Score = originalWinner.Score,
                    Crop = originalWinner.Crop,
                    DoubleLineCrop = originalWinner.DoubleLineCrop,
                };
                swapped = true;
            }
        }

        var rest = swapped ? candidates : candidates.Where(c => !ReferenceEquals(c, originalWinner)).ToList();
        var result = new List<PlateCandidate> { winner };
        result.AddRange(rest);
        return result;
    }

    /// <summary>CTC's fast multi-crop-variant search finds the best-framed
    /// crop; the SAR head then re-reads just that one (much slower, run
    /// once, not per-variant). SAR doesn't have CTC's blank-collapse
    /// failure mode -- a 253-image held-out comparison found SAR
    /// exact-matched 98.81% vs this CTC pipeline's 87.75% on single-line
    /// crops, and never lost to CTC on a single image.</summary>
    public (string Text, double Confidence, string Variant) RecognizeBest(Mat img)
    {
        var candidates = RecognizeCandidates(img);
        if (candidates.Count == 0) return ("", 0.0, "");
        var best = candidates[0];

        var isDoubleLineCrop = best.DoubleLineCrop.HasValue;
        var cropUsable = best.Crop != null || isDoubleLineCrop;

        if (_plateOcr.Available && cropUsable)
        {
            string sarText;
            float sarConfidence;
            try
            {
                if (isDoubleLineCrop)
                {
                    var (top, bottom) = best.DoubleLineCrop!.Value;
                    (sarText, sarConfidence) = _plateOcr.RecognizeDoubleLine(top, bottom);
                }
                else
                {
                    (sarText, sarConfidence) = _plateOcr.Recognize(best.Crop!);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SAR refine error (falling back to CTC reading): {ex.Message}");
                sarText = "";
                sarConfidence = 0f;
            }

            sarText = PlateGrammar.NormalizePlateText(sarText);
            if (sarText.Length > 0)
            {
                // Grammar-clean only the double-line reading (plain
                // concatenation never gets a correction pass otherwise) --
                // NOT the single-line one, which measured worse on the
                // held-out eval when applied unconditionally (a correct
                // raw SAR reading occasionally got "corrected" wrong).
                if (isDoubleLineCrop)
                {
                    (sarText, _) = PlateGrammar.ChooseCorrectedPlate(sarText);
                }
                return (sarText, sarConfidence, $"{best.Variant}+sar");
            }
        }

        return (best.Text, best.Confidence, best.Variant);
    }
}
