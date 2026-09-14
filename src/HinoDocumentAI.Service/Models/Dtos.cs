namespace HinoDocumentAI.Service.Models;

// ---------- /extract ----------

public record OcrPage(int PageNumber, Services.DocumentType DetectedType, string RawText);

public record ExtractResponse(List<OcrPage> Pages);

// ---------- /clean ----------

public record CleanRequest(Services.DocumentType DocumentType, string RawText);

public record CleanedLineItem(string PartNumber, string PartName, string Quantity, string Price, string Amount);

public record CleanResponse(Dictionary<string, string> HeaderFields, List<CleanedLineItem> LineItems);

// ---------- /match (BELUM DIUBAH — lihat catatan di bawah) ----------

public record CleanedField(string CanonicalField, string Value, string SourceLabel, double Confidence, string Method);

public record MatchRequest(List<CleanedField> CleanedData, Dictionary<string, string> HesData);

public record MatchDetail(string Field, string ExtractedValue, string HesValue, bool IsMatch, double Confidence);

public record MatchResponse(bool OverallMatch, double OverallConfidence, List<MatchDetail> Details);