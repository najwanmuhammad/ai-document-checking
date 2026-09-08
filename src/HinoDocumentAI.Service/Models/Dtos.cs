namespace HinoDocumentAI.Service.Models;

// ---------- /extract ----------

public record ExtractedField(string Label, string Value, double? Confidence = null);

public record ExtractRequest(string DocumentTypeHint); // hint opsional dari .NET Invoice Portal, tetap divalidasi ulang oleh classifier

public record ExtractResponse(
    Services.DocumentType DetectedDocumentType,
    string RawText,
    List<ExtractedField> Fields
);

// ---------- /clean ----------

public record CleanRequest(
    Services.DocumentType DocumentType,
    List<ExtractedField> Fields
);

public record CleanedField(
    string CanonicalField,
    string Value,
    string SourceLabel,
    double Confidence,
    string Method // "dictionary" | "embedding" | "llm_fallback"
);

public record CleanResponse(List<CleanedField> CleanedData);

// ---------- /match ----------

public record MatchRequest(
    List<CleanedField> CleanedData,
    Dictionary<string, string> HesData // key = canonical field, value = data dari sistem HES
);

public record MatchDetail(
    string Field,
    string ExtractedValue,
    string HesValue,
    bool IsMatch,
    double Confidence
);

public record MatchResponse(
    bool OverallMatch,
    double OverallConfidence,
    List<MatchDetail> Details
);
