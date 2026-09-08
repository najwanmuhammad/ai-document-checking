using OllamaSharp;
using HinoDocumentAI.Service.Models;
using HinoDocumentAI.Service.Services;

namespace HinoDocumentAI.Service.Services;

public interface ICleaningService
{
    Task<List<CleanedField>> CleanAsync(DocumentType docType, List<ExtractedField> fields);
}

/// <summary>
/// Normalisasi label kolom mentah -> field baku. Urutan pengecekan (murah
/// ke mahal), lihat PRD Bagian 12 & 15:
///   1. Dictionary sinonim (CanonicalFields.KnownSynonyms) — gratis, instan.
///   2. Embedding similarity via Ollama (bge-m3) — metode utama untuk label
///      yang belum ada di dictionary.
///   3. LLM fallback via Ollama (qwen2.5:1.5b) — HANYA kalau confidence
///      embedding di bawah threshold. Ini yang menjaga beban Ollama tetap
///      ringan sesuai keterbatasan RAM server (PRD Bagian 16).
///
/// Dua client Ollama terpisah dipakai (bukan satu client dengan
/// SelectedModel yang diganti-ganti) supaya aman dipanggil concurrent oleh
/// ASP.NET Core tanpa race condition pada properti yang di-share.
/// </summary>
public class CleaningService : ICleaningService
{
    private readonly OllamaApiClient _embedClient;
    private readonly OllamaApiClient _chatClient;
    private readonly double _embeddingConfidenceThreshold;
    private readonly ILogger<CleaningService> _logger;

    // Cache embedding field baku supaya tidak dihitung ulang tiap request.
    private Dictionary<string, float[]>? _canonicalFieldEmbeddings;

    public CleaningService(IConfiguration config, ILogger<CleaningService> logger)
    {
        _logger = logger;
        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var embeddingModel = config["Ollama:EmbeddingModel"] ?? "bge-m3";
        var llmModel = config["Ollama:LlmFallbackModel"] ?? "qwen2.5:1.5b";

        // Model diikat lewat constructor (bukan properti yang diganti-ganti),
        // sesuai contoh resmi OllamaSharp: new OllamaApiClient(baseUrl, model)
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
            var cleaned = await CleanOneFieldAsync(field, canonicalFields);
            results.Add(cleaned);
        }
        return results;
    }

    private async Task<CleanedField> CleanOneFieldAsync(ExtractedField field, string[] canonicalFields)
    {
        var normalizedLabel = field.Label.Trim().ToLowerInvariant();

        // 1. Dictionary sinonim dulu — cepat & gratis
        if (CanonicalFields.KnownSynonyms.TryGetValue(normalizedLabel, out var known))
        {
            return new CleanedField(known, field.Value, field.Label, Confidence: 1.0, Method: "dictionary");
        }

        // 2. Embedding similarity (metode utama)
        var (bestField, score) = await FindClosestFieldByEmbeddingAsync(field.Label, canonicalFields);
        if (score >= _embeddingConfidenceThreshold)
        {
            return new CleanedField(bestField, field.Value, field.Label, score, Method: "embedding");
        }

        // 3. Fallback ke LLM (Qwen2.5) — hanya kalau embedding kurang yakin
        _logger.LogInformation(
            "Embedding confidence rendah ({Score:F2}) untuk label '{Label}', fallback ke LLM.",
            score, field.Label);
        var (llmField, llmConfidence) = await AskLlmForFieldAsync(field.Label, canonicalFields);
        return new CleanedField(llmField, field.Value, field.Label, llmConfidence, Method: "llm_fallback");
    }

    private async Task EnsureCanonicalEmbeddingsAsync(string[] canonicalFields)
    {
        _canonicalFieldEmbeddings ??= new Dictionary<string, float[]>();
        var missing = canonicalFields.Where(f => !_canonicalFieldEmbeddings.ContainsKey(f)).ToList();
        if (missing.Count == 0) return;

        foreach (var field in missing)
        {
            // TODO: verifikasi nama method persis di versi OllamaSharp yang
            // ter-install (lihat README poin 3) — method embedding Ollama
            // mengikuti endpoint resmi POST /api/embed. Nama field baku
            // dipakai apa adanya sebagai teks yang di-embed; bisa
            // disempurnakan dengan deskripsi lebih natural per field
            // (mis. "part_number" -> "nomor atau kode part barang").
            float[] embedding = await GetEmbeddingAsync(field);
            _canonicalFieldEmbeddings[field] = embedding;
        }
    }

    private async Task<float[]> GetEmbeddingAsync(string text)
    {
        // API dikonfirmasi dari dokumentasi resmi OllamaSharp:
        // https://awaescher.github.io/OllamaSharp/docs/model-management.html
        //   var response = await ollama.EmbedAsync("teks polos");
        //   float[] vector = response.Embeddings[0];
        // (Bukan objek EmbedRequest seperti draft pertama saya — versi itu salah.)
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

    private async Task<(string Field, double Confidence)> AskLlmForFieldAsync(string label, string[] canonicalFields)
    {
        string prompt = $"""
            Kamu membantu memetakan label kolom invoice ke daftar field baku.
            Daftar field baku: {string.Join(", ", canonicalFields)}
            Label mentah dari dokumen: "{label}"
            Jawab HANYA dengan satu nama field baku yang paling cocok, tanpa penjelasan tambahan.
            """;

        // Chat.SendAsync mengembalikan IAsyncEnumerable<string> (streaming
        // token demi token) — dikonfirmasi dari dokumentasi resmi & juga
        // dari error compile yang sudah kita temui sebelumnya.
        var chat = new Chat(_chatClient);
        var responseBuilder = new System.Text.StringBuilder();
        await foreach (var token in chat.SendAsync(prompt))
        {
            responseBuilder.Append(token);
        }
        string answer = responseBuilder.ToString().Trim();

        bool isValid = canonicalFields.Contains(answer, StringComparer.OrdinalIgnoreCase);
        // Confidence LLM fallback sengaja diberi skor moderat (bukan 1.0)
        // karena tidak ada ukuran "seberapa yakin" LLM selain jawabannya —
        // dokumen dengan hasil dari jalur ini disarankan tetap masuk
        // pertimbangan review manual kalau confidence akhir borderline
        // (lihat PRD Bagian 15).
        return isValid ? (answer, 0.6) : (canonicalFields[0], 0.3);
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
