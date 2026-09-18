using System.Text.Json.Serialization;

namespace HinoDocumentAI.Service.Models;

// ---------- /extract ----------
public record OcrTextRegion(
    string Text,
    double Confidence,
    double X,
    double Y,
    double Width,
    double Height);

public record OcrPage(
    int PageNumber,
    Services.DocumentType DocumentType,
    double ClassificationConfidence,
    string RawText,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LayoutText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    List<OcrTextRegion>? Regions = null);

public record ExtractResponse(List<OcrPage> Pages);

// ---------- /clean ----------
public record CleanDocumentRequest(
    List<OcrPage>? Pages = null,
    int PageNumber = 1,
    Services.DocumentType DocumentType = Services.DocumentType.Unknown,
    double ClassificationConfidence = 0,
    string? RawText = null,
    string? LayoutText = null,
    List<OcrTextRegion>? Regions = null);

// Single-page shape used by /clean/page and also accepted by /clean for
// compatibility with the original API payload.
public record CleanRequest(
    Services.DocumentType DocumentType,
    string RawText,
    string? LayoutText = null,
    List<OcrTextRegion>? Regions = null);

public record CleanPageResult(
    int PageNumber,
    Services.DocumentType DocumentType,
    string Status,
    CleanResponse? Data,
    string? Message = null);

public record CleanDocumentResponse(
    List<CleanPageResult> Pages,
    int CleanedPageCount,
    int SkippedPageCount,
    int FailedPageCount);

public record CleanedLineItem(
    string? PartNumber,
    string? PartName,
    string? Quantity,
    string? Price,
    string? Amount);

public record CleanedFieldEvidence(
    string CanonicalField,
    string? Value,
    string? SourceText,
    double Confidence,
    string Method,
    int? LineItemIndex = null);

public record CleanResponse(
    Services.DocumentType DocumentType,
    Dictionary<string, string?> HeaderFields,
    List<CleanedLineItem> LineItems,
    double Confidence,
    bool RequiresReview,
    List<string> Warnings,
    List<CleanedFieldEvidence> Evidence);

// ---------- /match ----------
public record CleanedField(
    string CanonicalField,
    string Value,
    string SourceLabel,
    double Confidence,
    string Method);

public record MatchRequest(List<CleanedField> CleanedData, Dictionary<string, string> HesData);

public record MatchDetail(
    string Field,
    string ExtractedValue,
    string HesValue,
    bool IsMatch,
    double Confidence);

public record MatchResponse(bool OverallMatch, double OverallConfidence, List<MatchDetail> Details);
