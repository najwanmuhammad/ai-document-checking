using FuzzySharp;
using HinoDocumentAI.Service.Models;

namespace HinoDocumentAI.Service.Services;

public interface IMatchingService
{
    MatchResponse Match(List<CleanedField> cleanedData, Dictionary<string, string> hesData);
}

/// <summary>
/// Pencocokan data hasil cleaning vs data yang sudah ada di HES.
/// Field numerik dibandingkan exact (setelah normalisasi format angka),
/// field teks dibandingkan fuzzy (FuzzySharp) untuk toleransi kesalahan
/// OCR kecil — lihat PRD Bagian 15 (confidence scoring berlapis:
/// keyword + tipe data, terinspirasi ablation study OCRMiner).
/// </summary>
public class MatchingService : IMatchingService
{
    private static readonly HashSet<string> NumericFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "quantity", "price", "tax_amount"
    };

    private static readonly HashSet<string> DateFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoice_date", "delivery_date", "tax_invoice_date"
    };

    public MatchResponse Match(List<CleanedField> cleanedData, Dictionary<string, string> hesData)
    {
        var details = new List<MatchDetail>();

        foreach (var item in cleanedData)
        {
            if (!hesData.TryGetValue(item.CanonicalField, out var hesValue))
            {
                // Field tidak ada di data HES untuk dibandingkan — tetap
                // dicatat sebagai mismatch supaya kelihatan di review,
                // bukan di-skip diam-diam.
                details.Add(new MatchDetail(item.CanonicalField, item.Value, "(tidak ada di HES)", false, 0.0));
                continue;
            }

            var (isMatch, confidence) = CompareField(item.CanonicalField, item.Value, hesValue);
            details.Add(new MatchDetail(item.CanonicalField, item.Value, hesValue, isMatch, confidence));
        }

        bool overallMatch = details.Count > 0 && details.All(d => d.IsMatch);
        double overallConfidence = details.Count > 0 ? details.Average(d => d.Confidence) : 0.0;

        return new MatchResponse(overallMatch, overallConfidence, details);
    }

    private static (bool IsMatch, double Confidence) CompareField(string field, string extractedValue, string hesValue)
    {
        string a = extractedValue.Trim();
        string b = hesValue.Trim();

        if (NumericFields.Contains(field))
        {
            // TODO: perkuat normalisasi angka — sample data nyata memakai
            // format "Rp 63.480,00" (titik = ribuan, koma = desimal) yang
            // berbeda dari format umum "1,234.56". Perlu parsing eksplisit
            // sesuai locale Indonesia sebelum dibandingkan, jangan andalkan
            // decimal.Parse bawaan tanpa CultureInfo yang benar.
            bool numericMatch = NormalizeNumber(a) == NormalizeNumber(b);
            return (numericMatch, numericMatch ? 1.0 : 0.0);
        }

        if (DateFields.Contains(field))
        {
            // TODO: tanggal di sample data muncul dalam banyak format
            // ("07/Aug/26", "21 July 2026", "07 Agustus 2026", bahkan
            // menyatu dalam kalimat lokasi+tanggal — lihat PRD Bagian
            // 13.2 poin 3). Perlu date-parsing yang toleran multi-format,
            // bukan perbandingan string langsung.
            bool dateMatch = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            return (dateMatch, dateMatch ? 1.0 : 0.0);
        }

        // Field teks lain (part_name, supplier_name, dst.) — fuzzy match
        int score = Fuzz.Ratio(a.ToLowerInvariant(), b.ToLowerInvariant());
        double confidence = score / 100.0;
        return (confidence >= 0.9, confidence);
    }

    private static string NormalizeNumber(string raw)
    {
        // Hilangkan simbol mata uang & pemisah ribuan gaya Indonesia,
        // lalu samakan pemisah desimal ke '.'
        return raw
            .Replace("Rp", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".", "")
            .Replace(",", ".")
            .Replace(" ", "")
            .Trim();
    }
}
