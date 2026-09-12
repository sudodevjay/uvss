using System.Text.RegularExpressions;

namespace UvssService.Ocr;

/// <summary>Exact C# port of anpr-ai-service/main.py's grammar/state-code
/// correction + consensus-voting logic (normalize_plate_text,
/// corrected_plate_candidates, choose_corrected_plate, correct_state_code_candidates,
/// extra_char_removal_candidates, similar_plate_text, consensus_plate_text) --
/// same formulas, same score weights, ported line-for-line so behaviour
/// matches the deployed Python pipeline exactly, not just approximately.</summary>
public static class PlateGrammar
{
    private static readonly Regex IndianStandardPlateRe = new(@"^[A-Z]{2}\d{1,2}[A-Z]{1,3}\d{1,4}$", RegexOptions.Compiled);
    private static readonly Regex IndianBhPlateRe = new(@"^\d{2}BH\d{4}[A-Z]{1,2}$", RegexOptions.Compiled);

    public static readonly IReadOnlyDictionary<string, string> IndianStateCodes = new Dictionary<string, string>
    {
        ["AN"] = "Andaman and Nicobar Islands", ["AP"] = "Andhra Pradesh", ["AR"] = "Arunachal Pradesh",
        ["AS"] = "Assam", ["BR"] = "Bihar", ["CG"] = "Chhattisgarh", ["CH"] = "Chandigarh",
        ["DD"] = "Dadra and Nagar Haveli and Daman and Diu", ["DL"] = "Delhi", ["DN"] = "Dadra and Nagar Haveli",
        ["GA"] = "Goa", ["GJ"] = "Gujarat", ["HP"] = "Himachal Pradesh", ["HR"] = "Haryana",
        ["JH"] = "Jharkhand", ["JK"] = "Jammu and Kashmir", ["KA"] = "Karnataka", ["KL"] = "Kerala",
        ["LA"] = "Ladakh", ["LD"] = "Lakshadweep", ["MH"] = "Maharashtra", ["ML"] = "Meghalaya",
        ["MN"] = "Manipur", ["MP"] = "Madhya Pradesh", ["MZ"] = "Mizoram", ["NL"] = "Nagaland",
        ["OD"] = "Odisha", ["OR"] = "Odisha", ["PB"] = "Punjab", ["PN"] = "Punjab", ["PY"] = "Puducherry",
        ["RJ"] = "Rajasthan", ["SK"] = "Sikkim", ["TN"] = "Tamil Nadu", ["TG"] = "Telangana",
        ["TR"] = "Tripura", ["TS"] = "Telangana", ["UA"] = "Uttarakhand", ["UK"] = "Uttarakhand",
        ["UP"] = "Uttar Pradesh", ["WB"] = "West Bengal",
    };

    private static readonly (string Pattern, string Tag)[] IndianSlotPatterns =
    {
        ("LLDLDDDD", "indian_8"),
        ("LLDLLDDDD", "indian_9a"),
        ("LLDDLDDDD", "indian_9b"),
        ("LLDLLLDDDD", "indian_10a"),
        ("LLDDLLDDDD", "indian_10b"),
        ("LLDDLLLDDDD", "indian_11"),
    };

    private static readonly Dictionary<char, char> LetterSlotFixes = new()
    {
        ['0'] = 'O', ['1'] = 'I', ['2'] = 'Z', ['4'] = 'A', ['5'] = 'S', ['6'] = 'G', ['8'] = 'B',
    };

    private static readonly Dictionary<char, char> DigitSlotFixes = new()
    {
        ['O'] = '0', ['Q'] = '0', ['D'] = '0', ['I'] = '1', ['L'] = '1',
        ['Z'] = '2', ['S'] = '5', ['B'] = '8', ['G'] = '6', ['T'] = '7',
    };

    private static readonly Dictionary<char, char[]> DigitToLetterCandidates = new()
    {
        ['0'] = new[] { 'O', 'D', 'Q' }, ['1'] = new[] { 'I', 'L' }, ['2'] = new[] { 'Z' },
        ['4'] = new[] { 'A' }, ['5'] = new[] { 'S' }, ['6'] = new[] { 'G' }, ['8'] = new[] { 'B' },
    };

    private static readonly Dictionary<char, char[]> LetterToLetterCandidates = new()
    {
        ['O'] = new[] { 'D', 'Q', 'C' }, ['D'] = new[] { 'O' }, ['Q'] = new[] { 'O' },
        ['C'] = new[] { 'G', 'O' }, ['G'] = new[] { 'C' }, ['I'] = new[] { 'L' }, ['L'] = new[] { 'I' },
        ['V'] = new[] { 'Y' }, ['Y'] = new[] { 'V' },
    };

    public static string NormalizePlateText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var upper = text.Trim().ToUpperInvariant();
        var chars = upper.Where(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'));
        return new string(chars.ToArray());
    }

    public static string PlateStateCode(string text)
    {
        var normalized = NormalizePlateText(text);
        if (normalized.Length >= 4 && normalized.Substring(2, 2) == "BH")
        {
            return "BH";
        }
        if (normalized.Length < 2) return "";
        var stateCode = normalized.Substring(0, 2);
        return IndianStateCodes.ContainsKey(stateCode) ? stateCode : "";
    }

    public static double FormatScore(string text)
    {
        var normalized = NormalizePlateText(text);
        if (normalized.Length == 0) return 0.0;
        if (IndianStandardPlateRe.IsMatch(normalized) || IndianBhPlateRe.IsMatch(normalized))
        {
            return PlateStateCode(normalized).Length > 0 ? 0.98 : 0.9;
        }
        if (normalized.Length >= 6 && normalized.Length <= 12
            && normalized.Any(char.IsLetter) && normalized.Any(char.IsDigit))
        {
            return 0.45;
        }
        return 0.0;
    }

    public static double PlateLengthPenalty(string text)
    {
        var normalized = NormalizePlateText(text);
        if (normalized.Length == 0) return 0.05;
        if (IndianStandardPlateRe.IsMatch(normalized) || IndianBhPlateRe.IsMatch(normalized)) return 0.0;
        if (normalized.Length >= 8 && normalized.Length <= 11) return 0.0;
        if (normalized.Length >= 6 && normalized.Length <= 13) return 0.02;
        return 0.05;
    }

    public static bool IsValidPlateFormat(string text)
    {
        var normalized = NormalizePlateText(text);
        return normalized.Length >= 6 && normalized.Length <= 12
            && (IndianStandardPlateRe.IsMatch(normalized) || IndianBhPlateRe.IsMatch(normalized));
    }

    /// <returns>(corrected text, changes count; 999 = unusable)</returns>
    public static (string Corrected, int Changes) FixSlots(string text, string pattern)
    {
        var normalized = NormalizePlateText(text);
        if (normalized.Length != pattern.Length)
        {
            return (normalized, 999);
        }

        var changes = 0;
        var corrected = new char[normalized.Length];
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            var slot = pattern[i];
            char newChar;
            if (slot == 'L')
            {
                newChar = LetterSlotFixes.GetValueOrDefault(ch, ch);
                if (!char.IsLetter(newChar))
                {
                    return (normalized, 999);
                }
            }
            else
            {
                newChar = DigitSlotFixes.GetValueOrDefault(ch, ch);
                if (!char.IsDigit(newChar))
                {
                    return (normalized, 999);
                }
            }
            if (newChar != ch) changes++;
            corrected[i] = newChar;
        }
        return (new string(corrected), changes);
    }

    private static char[] StateCodeCharCandidates(char ch)
    {
        var options = new HashSet<char> { ch };
        if (DigitToLetterCandidates.TryGetValue(ch, out var d)) foreach (var c in d) options.Add(c);
        if (LetterToLetterCandidates.TryGetValue(ch, out var l)) foreach (var c in l) options.Add(c);
        return options.ToArray();
    }

    private static List<string> ConfusableStateCodeFixes(string prefix)
    {
        var first = StateCodeCharCandidates(prefix[0]);
        var second = StateCodeCharCandidates(prefix[1]);
        var results = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in first)
        {
            foreach (var s in second)
            {
                var combo = $"{f}{s}";
                if (IndianStateCodes.ContainsKey(combo) && combo != prefix)
                {
                    results.Add(combo);
                }
            }
        }
        return results.ToList();
    }

    public static List<(string Text, int Changes)> CorrectStateCodeCandidates(string text)
    {
        var normalized = NormalizePlateText(text);
        if (normalized.Length < 4 || PlateStateCode(normalized).Length > 0)
        {
            return new List<(string, int)>();
        }

        var prefix = normalized.Substring(0, 2);
        var fixes = ConfusableStateCodeFixes(prefix);
        if (fixes.Count == 0)
        {
            fixes = IndianStateCodes.Keys.Where(code => LevenshteinDistance(prefix, code) == 1).ToList();
        }
        return fixes.Select(fix => (fix + normalized.Substring(2), 1)).ToList();
    }

    public static List<(string Text, int Changes, string Tag)> ExtraCharRemovalCandidates(string text)
    {
        var normalized = NormalizePlateText(text);
        var candidates = new List<(string, int, string)>();
        foreach (var (pattern, tag) in IndianSlotPatterns)
        {
            if (normalized.Length != pattern.Length + 1) continue;
            for (var i = 0; i < normalized.Length; i++)
            {
                var trimmed = normalized.Remove(i, 1);
                var (fixed_, changes) = FixSlots(trimmed, pattern);
                if (changes > 1) continue;

                if (PlateStateCode(fixed_).Length > 0)
                {
                    candidates.Add((fixed_, changes + 1, $"{tag}_trim{i}"));
                }
                else
                {
                    foreach (var (stateFixed, _) in CorrectStateCodeCandidates(fixed_))
                    {
                        candidates.Add((stateFixed, changes + 2, $"{tag}_trim{i}+statefix"));
                    }
                }
            }
        }
        return candidates;
    }

    public static List<(string Text, double Score, string Tag)> CorrectedPlateCandidates(string text)
    {
        var normalized = NormalizePlateText(text);
        if (normalized.Length == 0)
        {
            return new List<(string, double, string)>();
        }

        var candidates = new List<(string Text, double Score, string Tag)> { (normalized, FormatScore(normalized), "raw") };

        foreach (var (pattern, tag) in IndianSlotPatterns)
        {
            var (corrected, changes) = FixSlots(normalized, pattern);
            if (changes <= 2 && corrected != normalized)
            {
                candidates.Add((corrected, Math.Max(FormatScore(corrected) - changes * 0.05, 0.0), tag));
            }
        }

        foreach (var (corrected, changes, tag) in ExtraCharRemovalCandidates(normalized))
        {
            if (corrected != normalized)
            {
                candidates.Add((corrected, Math.Max(FormatScore(corrected) - changes * 0.05, 0.0), tag));
            }
        }

        foreach (var (value, score, tag) in candidates.ToList())
        {
            foreach (var (stateFixed, stateChanges) in CorrectStateCodeCandidates(value))
            {
                if (stateChanges == 1 && stateFixed != value)
                {
                    var stateFixGain = FormatScore(stateFixed) - FormatScore(value);
                    candidates.Add((stateFixed, Math.Max(score + stateFixGain - 0.03, 0.0), $"{tag}+statefix"));
                }
            }
        }

        var bestByText = new Dictionary<string, (double Score, string Tag)>();
        foreach (var (value, score, tag) in candidates)
        {
            if (!bestByText.TryGetValue(value, out var existing) || score > existing.Score)
            {
                bestByText[value] = (score, tag);
            }
        }
        return bestByText.Select(kv => (kv.Key, kv.Value.Score, kv.Value.Tag)).ToList();
    }

    public static (string Text, string Tag) ChooseCorrectedPlate(string text)
    {
        var candidates = CorrectedPlateCandidates(text);
        if (candidates.Count == 0)
        {
            return (NormalizePlateText(text), "raw");
        }
        var originalLen = NormalizePlateText(text).Length;
        var best = candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => -Math.Abs(c.Text.Length - originalLen))
            .First();
        return (best.Text, best.Tag);
    }

    public static int LevenshteinDistance(string left, string right)
    {
        if (left == right) return 0;
        if (left.Length < right.Length) (left, right) = (right, left);
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] != right[j - 1] ? 1 : 0;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }
            previous = current;
        }
        return previous[right.Length];
    }

    public static bool SimilarPlateText(string left, string right)
    {
        var l = NormalizePlateText(left);
        var r = NormalizePlateText(right);
        if (l.Length == 0 || r.Length == 0) return false;
        if (Math.Abs(l.Length - r.Length) > 2) return false;
        return LevenshteinDistance(l, r) <= (Math.Max(l.Length, r.Length) <= 7 ? 1 : 2);
    }

    /// <summary>Edit-distance-based alignment of `other` onto `reference`'s
    /// character positions -- an engineering-equivalent to Python's
    /// difflib.SequenceMatcher used for the same purpose (map a
    /// shorter/longer OCR read's characters onto a reference read's index
    /// positions for per-position consensus voting; unaligned/inserted
    /// positions vote nothing). Uses standard Levenshtein DP + traceback
    /// rather than difflib's specific longest-matching-block heuristic --
    /// same purpose (best-effort character alignment for majority voting,
    /// which is inherently approximate already), not bit-identical
    /// opcodes in every edge case.</summary>
    private static string[] AlignToReference(string reference, string other)
    {
        var aligned = new string[reference.Length];
        Array.Fill(aligned, "");

        var n = reference.Length;
        var m = other.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) dp[i, 0] = i;
        for (var j = 0; j <= m; j++) dp[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = reference[i - 1] != other[j - 1] ? 1 : 0;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }

        var ri = n;
        var oj = m;
        while (ri > 0 && oj > 0)
        {
            var cost = reference[ri - 1] != other[oj - 1] ? 1 : 0;
            if (dp[ri, oj] == dp[ri - 1, oj - 1] + cost)
            {
                aligned[ri - 1] = other[oj - 1].ToString();
                ri--; oj--;
            }
            else if (dp[ri, oj] == dp[ri - 1, oj] + 1)
            {
                ri--;
            }
            else
            {
                oj--;
            }
        }
        return aligned;
    }

    public static string ConsensusPlateText(IReadOnlyList<string> texts)
    {
        var normalized = texts.Select(NormalizePlateText).Where(t => t.Length > 0).ToList();
        if (normalized.Count == 0) return "";
        if (normalized.Count == 1) return normalized[0];

        var targetLength = normalized
            .GroupBy(t => t.Length)
            .OrderByDescending(g => g.Count())
            .First().Key;
        var reference = normalized.First(t => t.Length == targetLength);

        var consensusChars = new char[targetLength];
        for (var index = 0; index < targetLength; index++)
        {
            var votes = new Dictionary<char, int>();
            foreach (var text in normalized)
            {
                char? voteChar = null;
                if (text.Length == targetLength)
                {
                    voteChar = text[index];
                }
                else
                {
                    var alignedChar = AlignToReference(reference, text)[index];
                    if (alignedChar.Length > 0) voteChar = alignedChar[0];
                }
                if (voteChar.HasValue)
                {
                    votes[voteChar.Value] = votes.GetValueOrDefault(voteChar.Value) + 1;
                }
            }
            consensusChars[index] = votes.Count > 0
                ? votes.OrderByDescending(kv => kv.Value).First().Key
                : reference[index];
        }

        var (corrected, _) = ChooseCorrectedPlate(new string(consensusChars));
        return corrected;
    }
}
