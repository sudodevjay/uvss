using OpenCvSharp;

namespace UvssService.Ocr;

public class AggregatedPlateResult
{
    public string PlateText = "";
    public double PlateConfidence;
    public double PlateFrameConfidence;
    public bool PlateTextValid;
    public string PlateStateCode = "";
    public string PlateStateName = "";
    public Rect? PlateBBox;
    public double PlateDetectionConfidence;
    public double PlateCropQuality;
    public double PlateCropSharpness;
    public string PlateOcrVariant = "";
    public byte[]? PlateCropJpeg;

    public bool AggregationEnabled;
    public int AggregationWindowSize;
    public int AggregationCandidateCount;
    public int AggregationClusterCount;
    public int AggregationTextVotes;
    public bool AggregationMinCandidatesMet;
    public double AggregationScore;
}

/// <summary>Exact C# port of anpr-ai-service/main.py's select_aggregated_result
/// (main.py:1806-1896) -- combines several frames' independent
/// recognize_best reads (one per selected track frame) into one final
/// decided plate text via per-character consensus voting across
/// same-cluster reads, same score weights as the Python original.</summary>
public static class PlateAggregation
{
    public static AggregatedPlateResult SelectAggregatedResult(IReadOnlyList<PlateFrameResult> results, int minCandidates)
    {
        var candidates = results
            .Where(r => r.PlateBBox.HasValue && PlateGrammar.NormalizePlateText(r.PlateText).Length > 0)
            .ToList();

        if (candidates.Count == 0)
        {
            var detected = results.Where(r => r.PlateBBox.HasValue).ToList();
            PlateFrameResult? selectedFallback = detected.Count > 0
                ? detected
                    .OrderByDescending(r => r.PlateCropQuality)
                    .ThenByDescending(r => r.PlateDetectionConfidence)
                    .ThenByDescending(r => r.PlateCropSharpness)
                    .First()
                : results.FirstOrDefault();

            var fallback = new AggregatedPlateResult
            {
                PlateBBox = selectedFallback?.PlateBBox,
                PlateDetectionConfidence = selectedFallback?.PlateDetectionConfidence ?? 0.0,
                PlateText = selectedFallback?.PlateText ?? "",
                PlateConfidence = selectedFallback?.PlateConfidence ?? 0.0,
                PlateCropQuality = selectedFallback?.PlateCropQuality ?? 0.0,
                PlateCropSharpness = selectedFallback?.PlateCropSharpness ?? 0.0,
                PlateOcrVariant = selectedFallback?.PlateOcrVariant ?? "",
                PlateCropJpeg = selectedFallback?.PlateCropJpeg,
                AggregationEnabled = true,
                AggregationWindowSize = results.Count,
                AggregationCandidateCount = 0,
                AggregationTextVotes = 0,
                AggregationMinCandidatesMet = false,
                AggregationScore = 0.0,
            };
            return fallback;
        }

        var groups = new List<(string Representative, List<PlateFrameResult> Results, List<string> Texts)>();
        foreach (var result in candidates)
        {
            var text = PlateGrammar.NormalizePlateText(result.PlateText);
            var matched = false;
            foreach (var group in groups)
            {
                if (PlateGrammar.SimilarPlateText(text, group.Representative))
                {
                    group.Results.Add(result);
                    group.Texts.Add(text);
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                groups.Add((text, new List<PlateFrameResult> { result }, new List<string> { text }));
            }
        }

        (double Confidence, double Quality, double Sharpness, double Format) ResultScore(PlateFrameResult r) => (
            r.PlateConfidence,
            r.PlateCropQuality,
            Math.Min(r.PlateCropSharpness / 200.0, 1.0),
            PlateGrammar.FormatScore(r.PlateText)
        );

        var scoredGroups = new List<(double Score, string Text, PlateFrameResult Best, double AvgConfidence, int Count)>();
        foreach (var group in groups)
        {
            var groupText = PlateGrammar.ConsensusPlateText(group.Texts);
            var groupCount = group.Results.Count;
            var avgConfidence = group.Results.Average(r => r.PlateConfidence);
            var avgQuality = group.Results.Average(r => r.PlateCropQuality);
            var bestResult = group.Results
                .OrderByDescending(ResultScore)
                .First();
            var groupScore = groupCount * 1.25
                + avgConfidence * 0.75
                + avgQuality * 0.45
                + PlateGrammar.FormatScore(groupText) * 0.85
                + Math.Min(bestResult.PlateCropSharpness / 200.0, 1.0) * 0.30;
            scoredGroups.Add((groupScore, groupText, bestResult, avgConfidence, groupCount));
        }

        var winner = scoredGroups.OrderByDescending(g => g.Score).First();
        var stateCode = PlateGrammar.PlateStateCode(winner.Text);
        var minCandidatesMet = winner.Count >= minCandidates;

        return new AggregatedPlateResult
        {
            PlateText = winner.Text,
            PlateFrameConfidence = winner.Best.PlateConfidence,
            PlateConfidence = winner.AvgConfidence,
            PlateTextValid = PlateGrammar.IsValidPlateFormat(winner.Text) && minCandidatesMet,
            PlateStateCode = stateCode,
            PlateStateName = PlateGrammar.IndianStateCodes.GetValueOrDefault(stateCode, stateCode == "BH" ? "Bharat Series" : ""),
            PlateBBox = winner.Best.PlateBBox,
            PlateDetectionConfidence = winner.Best.PlateDetectionConfidence,
            PlateCropQuality = winner.Best.PlateCropQuality,
            PlateCropSharpness = winner.Best.PlateCropSharpness,
            PlateOcrVariant = winner.Best.PlateOcrVariant,
            PlateCropJpeg = winner.Best.PlateCropJpeg,
            AggregationEnabled = true,
            AggregationWindowSize = results.Count,
            AggregationCandidateCount = candidates.Count,
            AggregationClusterCount = groups.Count,
            AggregationTextVotes = winner.Count,
            AggregationMinCandidatesMet = minCandidatesMet,
            AggregationScore = winner.Score,
        };
    }
}
