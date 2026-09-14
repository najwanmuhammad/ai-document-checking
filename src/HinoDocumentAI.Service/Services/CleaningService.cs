using System.Text.RegularExpressions;
using OllamaSharp;
using HinoDocumentAI.Service.Models;

namespace HinoDocumentAI.Service.Services;

public interface ICleaningService
{
    Task<List<CleanedField>> CleanAsync(DocumentType docType, List<ExtractedField> fields);
}

/// <summary>
/// Normalisasi label kolom mentah -> field baku. Urutan pengecekan (murah
/// ke mahal), lihat PRD Bagian 12 & 15:
///   1. Dictionary sinonim (butuh label asli hasil OCR, bukan "unknown").
///   2. Pola nilai (regex berdasarkan BENTUK value) — jalan walau label
///      tidak ketemu, menangani mayoritas kasus tanpa perlu AI sama sekali.
///   3. Embedding similarity via Ollama (bge-m3) — HANYA kalau ada label
///      asli yang bisa dibandingkan maknanya.
///   4. LLM fallback via Ollama (qwen2.5:1.5b) — benar-benar upaya terakhir.
/// </summary>
public class CleaningService : ICleaningService
{
    private readonly OllamaApiClient _embedClient;
    private readonly OllamaApiClient _chatClient;
    private readonly double _embeddingConfidenceThreshold;
    private readonly ILogger<CleaningService> _logger;

    private Dictionary<string, float[]>? _canonicalFieldEmbeddings;

    private static readonly Regex PricePattern = new(@"^\d{1,3}(,\d{3})*\.\d{2}$", RegexOptions.Compiled);
    private static readonly Regex QuantityWithUnitPattern = new(
        @"^\d+(\.\d+)?\s*(KT|PC|PCS|KG|SET|UNIT|PAIR)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PartNumberPattern = new(@"^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly string[] MonthNames =
    {
        "jan","feb","mar","apr","mei","jun","jul","agu","aug","sep","okt","oct","nov","des","dec",
        "january","february","march","april","may","june","july","august","september","october","november","december",
        "januari","februari","maret","april","juni","juli","agustus","september","oktober","november","desember"
    };

    public CleaningService(IConfiguration config, ILogger<CleaningService> logger)
    {
        _logger = logger;
        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var embeddingModel = config["Ollama:EmbeddingModel"] ?? "bge-m3";
        var llmModel = config["Ollama:LlmFallbackModel"] ?? "qwen2.5:1.5b";

        _embedClient = new OllamaApiClient(baseUrl, embeddingModel);
        _chatClient = new OllamaApiClient(baseUrl, llmModel);

        _embeddingConfidenceThreshold = config.GetValue("Ollama:EmbeddingConfidenceThreshold", 0.75);
    }

    public async Task<List<CleanedField>> CleanAsync(DocumentType docType, List<ExtractedField> fields)
    {
        string[] canonicalFields = docType switch
        {
            DocumentType.Invoice => CanonicalFields.InvoiceHeader.Concat(CanonicalFields.LineItem).ToArray(),
            DocumentType.DeliveryNote => CanonicalFields.DeliveryNoteHeader.Concat(CanonicalFields.LineItem).ToArray(),
            DocumentType.TaxInvoice => CanonicalFields.TaxInvoiceHeader.Concat(CanonicalFields.LineItem).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(docType), "Jenis dokumen tidak dikenali — pastikan sudah melalui langkah klasifikasi (FR-8) sebelum cleaning.")
        };

        await EnsureCanonicalEmbeddingsAsync(canonicalFields);

        var results = new List<CleanedField>();
        foreach (var field in fields)
        {
            // Buang token yang jelas tidak berguna (simbol mata uang lepas,
            // tanda baca lepas) sebelum diproses sama sekali.
            if (CanonicalFields.NoiseValues.Contains(field.Value.Trim()))
            {
                continue;
            }

            results.Add(await CleanOneFieldAsync(docType, field, canonicalFields));
        }
        return results;
    }

    private async Task<CleanedField> CleanOneFieldAsync(DocumentType docType, ExtractedField field, string[] canonicalFields)
    {
        var normalizedLabel = field.Label.Trim().ToLowerInvariant();
        bool hasRealLabel = !string.IsNullOrWhiteSpace(normalizedLabel) && normalizedLabel != "unknown";

        // 1. Dictionary sinonim (butuh label asli)
        if (hasRealLabel && CanonicalFields.KnownSynonyms.TryGetValue(normalizedLabel, out var known))
        {
            return Finalize(docType, known, field, 1.0, "dictionary");
        }

        // 2. Pola nilai (regex) — jalan walau label "unknown". Ini yang
        // menangkap mayoritas kasus yang kemarin salah lempar ke LLM.
        var (patternField, patternConfidence) = TryClassifyByValuePattern(field.Value);
        if (patternField != null && (!hasRealLabel || patternConfidence >= _embeddingConfidenceThreshold))
        {
            return Finalize(docType, patternField, field, patternConfidence, "pattern");
        }

        // 3. Embedding similarity — HANYA kalau ada label asli. Meng-embed
        // literal kata "unknown" ke nama field baku tidak ada artinya dan
        // cuma buang-buang panggilan Ollama (ini salah satu sumber
        // performa lambat & hasil ngaco kemarin).
        if (hasRealLabel)
        {
            var (bestField, score) = await FindClosestFieldByEmbeddingAsync(field.Label, canonicalFields);
            if (score >= _embeddingConfidenceThreshold)
            {
                return Finalize(docType, bestField, field, score, "embedding");
            }
        }

        // 4. Fallback LLM — benar-benar upaya terakhir sekarang, bukan
        // jalur utama seperti kemarin.
        _logger.LogInformation("Fallback LLM untuk label '{Label}' / value '{Value}'.", field.Label, field.Value);
        var (llmField, llmConfidence) = await AskLlmForFieldAsync(field.Label, field.Value, canonicalFields);
        return Finalize(docType, llmField, field, llmConfidence, "llm_fallback");
    }

    /// <summary>
    /// Field placeholder "document_number"/"document_date" (dipetakan oleh
    /// dictionary/pattern generik) diselesaikan ke nama field final sesuai
    /// jenis dokumen di sini — sebelumnya cuma dikomentari "akan dipetakan"
    /// tapi kodenya belum ditulis, jadi field ini nyangkut sebagai
    /// "document_number" mentah di semua hasil cleaning.
    /// </summary>
    private static CleanedField Finalize(DocumentType docType, string canonicalField, ExtractedField field, double confidence, string method)
    {
        string resolved = canonicalField switch
        {
            "document_number" => docType switch
            {
                DocumentType.Invoice => "invoice_number",
                DocumentType.DeliveryNote => "delivery_note_number",
                DocumentType.TaxInvoice => "tax_invoice_number",
                _ => canonicalField
            },
            "document_date" => docType switch
            {
                DocumentType.Invoice => "invoice_date",
                DocumentType.DeliveryNote => "delivery_date",
                DocumentType.TaxInvoice => "tax_invoice_date",
                _ => canonicalField
            },
            _ => canonicalField
        };
        return new CleanedField(resolved, field.Value, field.Label, confidence, method);
    }

    /// <summary>
    /// Klasifikasi berdasarkan BENTUK value, bukan label — berguna untuk
    /// OCR fragment yang labelnya tidak ketemu ("unknown"). Pola-pola ini
    /// dikalibrasi dari sample data nyata (lihat PRD Bagian 13.2), bukan
    /// contoh hipotetis. Confidence sengaja tidak 1.0 karena ini tetap
    /// heuristik, bukan pemetaan pasti.
    /// </summary>
    private static (string? Field, double Confidence) TryClassifyByValuePattern(string rawValue)
    {
        string value = rawValue.Trim();
        if (value.Length == 0) return (null, 0);

        // "63,480.00", "268,570.00", dst.
        if (PricePattern.IsMatch(value))
            return ("price", 0.85);

        // "4.0 KT", "100 pair" — bare number TANPA unit sengaja tidak
        // dianggap quantity karena ambigu dengan nomor urut baris ("No").
        if (QuantityWithUnitPattern.IsMatch(value))
            return ("quantity", 0.85);

        string lowerValue = value.ToLowerInvariant();
        if (value.Any(char.IsDigit) && MonthNames.Any(m => lowerValue.Contains(m)))
            return ("document_date", 0.7);

        // Kode alfanumerik tanpa spasi, mengandung digit, panjang wajar,
        // dan BUKAN pola harga — mis. "04905-37220", "047733725000",
        // "47041F101000", "TB60573N".
        if (value.Length >= 6
            && !value.Contains(' ')
            && value.Any(char.IsDigit)
            && !PricePattern.IsMatch(value)
            && PartNumberPattern.IsMatch(value))
            return ("part_number", 0.75);

        return (null, 0);
    }

    private async Task EnsureCanonicalEmbeddingsAsync(string[] canonicalFields)
    {
        _canonicalFieldEmbeddings ??= new Dictionary<string, float[]>();
        var missing = canonicalFields.Where(f => !_canonicalFieldEmbeddings.ContainsKey(f)).ToList();
        if (missing.Count == 0) return;

        foreach (var field in missing)
        {
            _canonicalFieldEmbeddings[field] = await GetEmbeddingAsync(field);
        }
    }

    private async Task<float[]> GetEmbeddingAsync(string text)
    {
        var response = await _embedClient.EmbedAsync(text);
        return response.Embeddings[0];
    }

    private async Task<(string Field, double Score)> FindClosestFieldByEmbeddingAsync(string label, string[] canonicalFields)
    {
        float[] labelEmbedding = await GetEmbeddingAsync(label);

        string bestField = canonicalFields[0];
        double bestScore = double.MinValue;
        foreach (var field in canonicalFields)
        {
            double score = CosineSimilarity(labelEmbedding, _canonicalFieldEmbeddings![field]);
            if (score > bestScore)
            {
                bestScore = score;
                bestField = field;
            }
        }
        return (bestField, bestScore);
    }

    private async Task<(string Field, double Confidence)> AskLlmForFieldAsync(string label, string value, string[] canonicalFields)
    {
        // Prompt few-shot (contoh Input -> Output konkret), BUKAN daftar
        // aturan abstrak seperti sebelumnya. Model kecil (1.5B) terbukti
        // jauh lebih akurat mengikuti contoh nyata daripada aturan
        // if-else tertulis — sebelumnya model ini nyaris selalu menjawab
        // "price" untuk apa pun karena kebingungan dengan prompt lama.
        string prompt = $"""
            Kamu mengklasifikasikan satu data dari invoice ke salah satu field baku berikut:
            {string.Join(", ", canonicalFields)}

            Contoh:
            "CUP KIT, WHEEL CYLINDER PISTON, FR" -> part_name
            "04905-37220" -> part_number
            "63,480.00" -> price
            "4.0 KT" -> quantity
            "PT. AKEBONO BRAKE ASTRA INDONESIA" -> supplier_name

            Data yang perlu diklasifikasi:
            Label (mungkin kosong/tidak akurat): "{label}"
            Isi/Value: "{value}"

            Jawab HANYA dengan satu nama field baku dari daftar di atas, tanpa tanda kutip atau penjelasan apa pun.
            """;

        var chat = new Chat(_chatClient);
        var responseBuilder = new System.Text.StringBuilder();
        await foreach (var token in chat.SendAsync(prompt))
        {
            responseBuilder.Append(token);
        }
        string answer = responseBuilder.ToString().Trim().Trim('"', '.', ' ');

        var matched = canonicalFields.FirstOrDefault(f => string.Equals(f, answer, StringComparison.OrdinalIgnoreCase));
        return matched != null ? (matched, 0.6) : (canonicalFields[0], 0.3);
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}