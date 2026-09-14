using System.Text.Json;
using OllamaSharp;
using HinoDocumentAI.Service.Models;

namespace HinoDocumentAI.Service.Services;

public interface ICleaningService
{
    Task<CleanResponse> CleanAsync(DocumentType docType, string rawText);
}

/// <summary>
/// Strukturisasi teks mentah OCR jadi field baku, langsung oleh LLM
/// (qwen2.5:3b) — menggantikan pendekatan heuristik posisi yang terbukti
/// tidak generalize ke variasi layout supplier (lihat PRD Bagian 13.2).
/// Model dinaikkan dari 1.5B ke 3B karena tugas structuring jauh lebih
/// berat dari sekadar klasifikasi satu label -> field.
/// </summary>
public class CleaningService : ICleaningService
{
    private readonly OllamaApiClient _chatClient;
    private readonly ILogger<CleaningService> _logger;

    public CleaningService(IConfiguration config, ILogger<CleaningService> logger)
    {
        _logger = logger;
        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var model = config["Ollama:StructuringModel"] ?? "qwen2.5:3b";
        _chatClient = new OllamaApiClient(baseUrl, model);
    }

    public async Task<CleanResponse> CleanAsync(DocumentType docType, string rawText)
    {
        string schemaHint = docType switch
        {
            DocumentType.Invoice => """
                {"invoice_number": "...", "invoice_date": "...", "supplier_name": "...",
                 "sub_total_amount": "...", "tax_amount": "...", "total_amount": "...",
                 "signer_name": "...", "signer_position": "...",
                 "line_items": [{"part_number": "...", "part_name": "...", "quantity": "...", "price": "...", "amount": "..."}]}
                """,
            DocumentType.DeliveryNote => """
                {"delivery_note_number": "...", "delivery_note_date": "...", "supplier_name": "...",
                 "line_items": [{"part_number": "...", "part_name": "...", "quantity": "..."}]}
                """,
            DocumentType.TaxInvoice => """
                {"tax_invoice_number": "...", "tax_invoice_date": "...", "supplier_name": "...", "tax_amount": "...",
                 "line_items": [{"part_number": "...", "part_name": "...", "quantity": "..."}]}
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(docType))
        };

        string prompt = $"""
            Kamu mengekstrak data terstruktur dari teks hasil OCR sebuah dokumen {DocTypeLabel(docType)}.
            Teks OCR mungkin ada typo kecil atau urutan sedikit acak — pakai konteks untuk tetap
            mengenali field yang benar.

            ATURAN PENTING:
            - Salin nilai (terutama angka: harga, jumlah, nomor) PERSIS seperti tertulis di teks OCR.
              JANGAN mengubah format, membulatkan, atau menebak angka yang tidak ada di teks.
            - Kalau suatu field tidak ditemukan di teks, isi dengan null.
            - Balas HANYA dengan JSON valid sesuai skema berikut, tanpa teks lain apa pun, tanpa
              markdown code fence:
            {schemaHint}

            Teks OCR:
            \"\"\"
            {rawText}
            \"\"\"
            """;

        var chat = new Chat(_chatClient);
        var responseBuilder = new System.Text.StringBuilder();
        await foreach (var token in chat.SendAsync(prompt))
        {
            responseBuilder.Append(token);
        }

        try
        {
            string rawJson = ExtractJsonBlock(responseBuilder.ToString());
            using var doc = JsonDocument.Parse(rawJson);
            return ParseCleanResponse(doc.RootElement, docType);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "LLM tidak mengembalikan JSON valid untuk dokumen tipe {DocType}. Raw output: {Raw}",
                docType, responseBuilder.ToString());
            // Dikembalikan kosong, bukan crash — dokumen ini otomatis dapat
            // confidence rendah di tahap matching dan masuk antrian review.
            return new CleanResponse(new Dictionary<string, string>(), new List<CleanedLineItem>());
        }
    }

    private static string DocTypeLabel(DocumentType docType) => docType switch
    {
        DocumentType.Invoice => "invoice",
        DocumentType.DeliveryNote => "surat jalan/delivery note",
        DocumentType.TaxInvoice => "faktur pajak",
        _ => "dokumen"
    };

    /// <summary>Model kecil kadang membungkus JSON dengan teks tambahan
    /// atau code fence markdown meski sudah diminta jangan — ambil blok
    /// { ... } terluar saja supaya parsing tetap jalan.</summary>
    private static string ExtractJsonBlock(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start == -1 || end == -1 || end <= start)
        {
            throw new JsonException("Tidak ditemukan blok JSON pada respons LLM.");
        }
        return text.Substring(start, end - start + 1);
    }

    private static CleanResponse ParseCleanResponse(JsonElement root, DocumentType docType)
    {
        var header = new Dictionary<string, string>();
        string[] headerKeys = docType switch
        {
            DocumentType.Invoice => [
                "invoice_number", "invoice_date", "supplier_name", "sub_total_amount", "tax_amount",
                "total_amount", "signer_name", "signer_position"
            ],
            DocumentType.DeliveryNote => ["delivery_note_number", "delivery_note_date", "supplier_name"],
            DocumentType.TaxInvoice => ["tax_invoice_number", "tax_invoice_date", "supplier_name", "tax_amount"],
            _ => []
        };

        foreach (var key in headerKeys)
        {
            if (root.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            {
                header[key] = val.GetString() ?? "";
            }
        }

        var lineItems = new List<CleanedLineItem>();
        if (root.TryGetProperty("line_items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                lineItems.Add(new CleanedLineItem(
                    PartNumber: GetStringOrEmpty(item, "part_number"),
                    PartName: GetStringOrEmpty(item, "part_name"),
                    Quantity: GetStringOrEmpty(item, "quantity"),
                    Price: GetStringOrEmpty(item, "price"),
                    Amount: GetStringOrEmpty(item, "amount")
                ));
            }
        }

        // TODO (belum diimplementasi): validasi murah terhadap halusinasi —
        // cek tiap angka (price/quantity/tax_amount) yang dikembalikan LLM
        // benar-benar muncul di rawText asli (toleransi spasi/pemisah
        // ribuan). Ini pengaman terhadap kemungkinan LLM "membetulkan" atau
        // salah menyalin angka alih-alih menyalin persis dari OCR.

        return new CleanResponse(header, lineItems);
    }

    private static string GetStringOrEmpty(JsonElement item, string key)
    {
        return item.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? ""
            : "";
    }
}