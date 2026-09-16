using System.Text.Json;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using HinoDocumentAI.Service.Models;

namespace HinoDocumentAI.Service.Services;

public interface ICleaningService
{
    Task<CleanResponse> CleanAsync(
        DocumentType docType,
        string rawText,
        CancellationToken cancellationToken = default);
}

/// Strukturisasi teks mentah OCR jadi field baku, langsung oleh LLM.
public class CleaningService : ICleaningService
{
    private readonly OllamaApiClient _chatClient;
    private readonly ILogger<CleaningService> _logger;
    private readonly TimeSpan _timeout;

    public CleaningService(IConfiguration config, ILogger<CleaningService> logger)
    {
        _logger = logger;
        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var model = config["Ollama:StructuringModel"] ?? "";
        var timeoutSeconds = config.GetValue("Ollama:TimeoutSeconds", 120);
        _timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        _chatClient = new OllamaApiClient(baseUrl, model);
    }

    public async Task<CleanResponse> CleanAsync(
        DocumentType docType,
        string rawText,
        CancellationToken cancellationToken = default)
    {
        string schemaHint = BuildSchemaHint(docType);

        string prompt = $"""
            Kamu mengekstrak data terstruktur dari teks hasil OCR sebuah dokumen {DocTypeLabel(docType)}.
            Teks OCR mungkin ada typo kecil atau urutan sedikit acak. pakai konteks untuk tetap
            mengenali field yang benar.

            ATURAN PENTING:
            - Salin nilai (terutama angka: harga, jumlah, nomor) PERSIS seperti tertulis di teks OCR.
              JANGAN mengubah format, membulatkan, atau menebak angka yang tidak ada di teks.
            - Kalau suatu field tidak ditemukan di teks, isi dengan null. Jangan gunakan "...".
            - Untuk line_items, buat satu object per baris barang yang benar-benar terlihat pada OCR.
            - Balas HANYA dengan satu object JSON valid sesuai skema berikut, tanpa teks lain apa pun, tanpa
              markdown code fence:
            {schemaHint}

            Label yang umum muncul pada dokumen dan padanan field canonical:
            {BuildSynonymHint(docType)}

            Teks OCR:
            \"\"\"
            {rawText}
            \"\"\"
            """;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        var responseBuilder = new System.Text.StringBuilder();
        try
        {
            var request = new ChatRequest
            {
                Model = _chatClient.SelectedModel,
                Messages = [new Message(ChatRole.User, prompt)],
                Format = "json",
                Stream = false,
                Think = false
            };

            await foreach (var response in _chatClient.ChatAsync(request, timeoutCts.Token))
            {
                if (response?.Message?.Content is { Length: > 0 } content)
                {
                    responseBuilder.Append(content);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                "Cleaning timeout setelah {TimeoutSeconds} detik untuk dokumen tipe {DocType}.",
                _timeout.TotalSeconds,
                docType);
            throw new TimeoutException("Ollama tidak menyelesaikan cleaning dalam batas waktu.");
        }

        if (responseBuilder.Length == 0)
        {
            _logger.LogError(
                "Ollama mengembalikan response kosong untuk dokumen tipe {DocType}.",
                docType);
            throw new InvalidOperationException("Ollama mengembalikan response kosong. Periksa model dan endpoint Ollama.");
        }

        try
        {
            string rawJson = ExtractJsonBlock(responseBuilder.ToString());
            using var doc = JsonDocument.Parse(rawJson);
            return ParseCleanResponse(doc.RootElement, docType);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "LLM tidak mengembalikan JSON valid untuk dokumen tipe {DocType}. Output length: {OutputLength}.",
                docType,
                responseBuilder.Length);
            throw new InvalidOperationException(
                $"Ollama mengembalikan JSON cleaning yang tidak valid ({ex.Message}, output {responseBuilder.Length} karakter).",
                ex);
        }
    }

    private static string DocTypeLabel(DocumentType docType) => docType switch
    {
        DocumentType.Invoice => "invoice",
        DocumentType.DeliveryNote => "surat jalan/delivery note",
        DocumentType.TaxInvoice => "faktur pajak",
        _ => "dokumen"
    };

    private static string BuildSchemaHint(DocumentType docType)
    {
        string header = string.Join(", ", CanonicalFields.GetHeader(docType)
            .Select(field => $"\"{field}\": null"));
        string lineItem = string.Join(", ", CanonicalFields.GetLineItems(docType)
            .Select(field => $"\"{field}\": null"));

        return $"{{{header}, \"line_items\": [{{{lineItem}}}]}}";
    }

    private static string BuildSynonymHint(DocumentType docType)
    {
        var supportedFields = CanonicalFields.GetHeader(docType)
            .Concat(CanonicalFields.GetLineItems(docType))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return string.Join("; ", CanonicalFields.KnownSynonyms
            .Where(pair => supportedFields.Contains(pair.Value))
            .Select(pair => $"{pair.Key} => {pair.Value}"));
    }

    /// Mengambil object JSON pertama yang lengkap, termasuk bila model
    /// membungkusnya dengan code fence atau teks tambahan.
    private static string ExtractJsonBlock(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0)
        {
            throw new JsonException("Tidak ditemukan blok JSON pada respons LLM.");
        }

        int depth = 0;
        bool insideString = false;
        bool escaped = false;
        for (int index = start; index < text.Length; index++)
        {
            char current = text[index];

            if (insideString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    insideString = false;
                }

                continue;
            }

            if (current == '"')
            {
                insideString = true;
            }
            else if (current == '{')
            {
                depth++;
            }
            else if (current == '}' && --depth == 0)
            {
                return text.Substring(start, index - start + 1);
            }
        }

        throw new JsonException("Object JSON dari respons LLM tidak lengkap.");
    }

    private static CleanResponse ParseCleanResponse(JsonElement root, DocumentType docType)
    {
        var header = new Dictionary<string, string>();
        foreach (var key in CanonicalFields.GetHeader(docType))
        {
            if (root.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            {
                header[key] = val.GetString() ?? "";
            }
        }

        var lineItems = new List<CleanedLineItem>();
        var lineItemFields = CanonicalFields.GetLineItems(docType);
        if (root.TryGetProperty("line_items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                lineItems.Add(new CleanedLineItem(
                    PartNumber: GetOptionalString(item, lineItemFields, "part_number"),
                    PartName: GetOptionalString(item, lineItemFields, "part_name"),
                    Quantity: GetOptionalString(item, lineItemFields, "quantity"),
                    Price: GetOptionalString(item, lineItemFields, "price"),
                    Amount: GetOptionalString(item, lineItemFields, "amount")
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

    private static string? GetOptionalString(
        JsonElement item,
        IReadOnlyList<string> supportedFields,
        string fieldName)
    {
        if (!supportedFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return item.TryGetProperty(fieldName, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? ""
            : null;
    }
}