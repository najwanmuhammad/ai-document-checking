namespace HinoDocumentAI.Service.Services;

/// <summary>
/// Klasifikasi jenis dokumen PER HALAMAN (bukan per file), pakai fuzzy
/// match supaya toleran typo kecil OCR (mis. "DELIVERY SLI" karena satu
/// huruf terpotong saat deteksi). Ini juga yang menyelesaikan masalah
/// "halaman lampiran ikut ke-extract padahal bukan invoice" — halaman yang
/// tidak cocok kata kunci apa pun otomatis dapat DocumentType.Unknown dan
/// tinggal di-skip oleh pemanggil.
/// </summary>
public static class DocumentClassifier
{
    private const int FuzzyThreshold = 85;

    public static DocumentType Classify(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return DocumentType.Unknown;

        // Faktur Pajak dicek lebih dulu — formatnya paling konsisten
        // (format resmi e-Faktur DJP, lihat PRD Bagian 13.2).
        foreach (var keyword in CanonicalFields.DocumentTypeKeywords[DocumentType.TaxInvoice])
        {
            if (ContainsFuzzy(rawText, keyword)) return DocumentType.TaxInvoice;
        }
        foreach (var keyword in CanonicalFields.DocumentTypeKeywords[DocumentType.Invoice])
        {
            if (ContainsFuzzy(rawText, keyword)) return DocumentType.Invoice;
        }
        foreach (var keyword in CanonicalFields.DocumentTypeKeywords[DocumentType.DeliveryNote])
        {
            if (ContainsFuzzy(rawText, keyword)) return DocumentType.DeliveryNote;
        }
        return DocumentType.Unknown;
    }

    private static bool ContainsFuzzy(string haystack, string needle)
    {
        var words = haystack.Split(
            new[] { ' ', '\n', '\r', '\t' },
            StringSplitOptions.RemoveEmptyEntries);
        int needleWordCount = needle.Split(' ').Length;

        for (int i = 0; i <= words.Length - needleWordCount; i++)
        {
            string window = string.Join(" ", words.Skip(i).Take(needleWordCount));
            if (FuzzySharp.Fuzz.Ratio(window.ToLowerInvariant(), needle.ToLowerInvariant()) >= FuzzyThreshold)
            {
                return true;
            }
        }
        return false;
    }
}