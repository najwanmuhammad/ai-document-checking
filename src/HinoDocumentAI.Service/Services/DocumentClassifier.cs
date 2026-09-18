using System.Text.RegularExpressions;
using FuzzySharp;

namespace HinoDocumentAI.Service.Services;

public record DocumentClassification(
    DocumentType DocumentType,
    double Confidence,
    string? MatchedKeyword);

/// <summary>
/// Classifies each OCR page independently. All document types are scored first
/// so a secondary word such as "invoice" cannot win merely because it was
/// checked before the actual page title.
/// </summary>
public static partial class DocumentClassifier
{
    private const int FuzzyThreshold = 85;

    public static DocumentType Classify(string rawText) =>
        ClassifyDetailed(rawText).DocumentType;

    public static DocumentClassification ClassifyDetailed(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new DocumentClassification(DocumentType.Unknown, 0, null);
        }

        string normalized = Normalize(rawText);
        var scores = new List<(DocumentType Type, int Score, int Position, string Keyword)>();

        foreach ((DocumentType type, string[] keywords) in CanonicalFields.DocumentTypeKeywords)
        {
            foreach (string keyword in keywords)
            {
                string normalizedKeyword = Normalize(keyword);
                int position = normalized.IndexOf(normalizedKeyword, StringComparison.Ordinal);
                int score = position >= 0 ? 100 : BestWindowScore(normalized, normalizedKeyword);
                scores.Add((type, score, position < 0 ? int.MaxValue : position, keyword));
            }
        }

        var best = scores
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Position)
            .ThenBy(item => Priority(item.Type))
            .First();

        if (best.Score < FuzzyThreshold)
        {
            return new DocumentClassification(DocumentType.Unknown, best.Score / 100.0, null);
        }

        return new DocumentClassification(best.Type, best.Score / 100.0, best.Keyword);
    }

    private static int BestWindowScore(string haystack, string needle)
    {
        string[] words = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int needleWordCount = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        int best = 0;

        for (int index = 0; index <= words.Length - needleWordCount; index++)
        {
            string window = string.Join(' ', words.Skip(index).Take(needleWordCount));
            best = Math.Max(best, Fuzz.Ratio(window, needle));
        }

        return best;
    }

    private static string Normalize(string value) =>
        WhitespaceRegex().Replace(value.ToLowerInvariant(), " ").Trim();

    private static int Priority(DocumentType type) => type switch
    {
        DocumentType.TaxInvoice => 0,
        DocumentType.DeliveryNote => 1,
        DocumentType.Invoice => 2,
        _ => 3
    };

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
