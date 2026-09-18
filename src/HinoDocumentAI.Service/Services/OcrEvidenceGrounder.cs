using System.Globalization;
using System.Text.RegularExpressions;

namespace HinoDocumentAI.Service.Services;

/// <summary>
/// Ensures generative output is backed by OCR evidence. It may recover the
/// original OCR formatting for numbers/dates, but it never invents a value.
/// </summary>
public static partial class OcrEvidenceGrounder
{
    private static readonly CultureInfo[] DateCultures =
    [
        CultureInfo.GetCultureInfo("id-ID"),
        CultureInfo.GetCultureInfo("en-US"),
        CultureInfo.InvariantCulture
    ];

    public static bool TryGround(
        string? candidate,
        string rawText,
        out string? groundedValue,
        out string? sourceText)
    {
        groundedValue = null;
        sourceText = null;

        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(rawText))
        {
            return false;
        }

        string trimmed = candidate.Trim();
        if (TryParseFlexibleDecimal(trimmed, out decimal candidateNumber))
        {
            foreach (Match match in NumberRegex().Matches(rawText))
            {
                string rawNumber = match.Value.Trim();
                if (TryParseFlexibleDecimal(rawNumber, out decimal sourceNumber) &&
                    sourceNumber == candidateNumber)
                {
                    groundedValue = StripCurrency(rawNumber);
                    sourceText = rawNumber;
                    return true;
                }
            }
        }

        if (ContainsWithFlexibleWhitespace(rawText, trimmed))
        {
            groundedValue = trimmed;
            sourceText = trimmed;
            return true;
        }

        if (TryParseDate(trimmed, out DateTime candidateDate))
        {
            foreach (Match match in DateRegex().Matches(rawText))
            {
                string rawDate = match.Value.Trim();
                if (TryParseDate(rawDate, out DateTime sourceDate) &&
                    sourceDate.Date == candidateDate.Date)
                {
                    groundedValue = rawDate;
                    sourceText = rawDate;
                    return true;
                }
            }
        }

        return false;
    }

    public static bool TryParseFlexibleDecimal(string? input, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string normalized = StripCurrency(input)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();
        normalized = NonNumericRegex().Replace(normalized, string.Empty);

        if (normalized.Length == 0 || normalized is "-" or "+")
        {
            return false;
        }

        int lastComma = normalized.LastIndexOf(',');
        int lastDot = normalized.LastIndexOf('.');

        if (lastComma >= 0 && lastDot >= 0)
        {
            char decimalSeparator = lastComma > lastDot ? ',' : '.';
            char thousandsSeparator = decimalSeparator == ',' ? '.' : ',';
            normalized = normalized.Replace(thousandsSeparator.ToString(), string.Empty, StringComparison.Ordinal);
            normalized = normalized.Replace(decimalSeparator, '.');
        }
        else if (lastComma >= 0)
        {
            normalized = NormalizeSingleSeparator(normalized, ',');
        }
        else if (lastDot >= 0)
        {
            normalized = NormalizeSingleSeparator(normalized, '.');
        }

        return decimal.TryParse(
            normalized,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string NormalizeSingleSeparator(string value, char separator)
    {
        int occurrences = value.Count(character => character == separator);
        int lastIndex = value.LastIndexOf(separator);
        int decimalDigits = value.Length - lastIndex - 1;

        if (occurrences == 1 && decimalDigits is 1 or 2)
        {
            return value.Replace(separator, '.');
        }

        return value.Replace(separator.ToString(), string.Empty, StringComparison.Ordinal);
    }

    private static bool ContainsWithFlexibleWhitespace(string rawText, string candidate)
    {
        string normalizedRaw = Regex.Replace(rawText, @"\s+", " ").Trim();
        string normalizedCandidate = Regex.Replace(candidate, @"\s+", " ").Trim();
        return normalizedRaw.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseDate(string input, out DateTime date)
    {
        foreach (CultureInfo culture in DateCultures)
        {
            if (DateTime.TryParse(
                    input,
                    culture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out date))
            {
                return true;
            }
        }

        date = default;
        return false;
    }

    private static string StripCurrency(string input) =>
        CurrencyRegex().Replace(input, string.Empty).Trim();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:Rp\.?\s*)?[+-]?\d[\d.,]*(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"(?<!\d)(?:\d{4}[-/.]\d{1,2}[-/.]\d{1,2}|\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4}|\d{1,2}\s+[\p{L}]+\s+\d{2,4})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\b(?:Rp|IDR|USD|JPY)\.?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyRegex();

    [GeneratedRegex(@"[^0-9,\.\-\+]")]
    private static partial Regex NonNumericRegex();
}
