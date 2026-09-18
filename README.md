# Hino Document AI Checking

Self-hosted .NET 8 service for OCR, page classification, document structuring,
and comparison of invoices, delivery notes, and Indonesian tax invoices.

## Prerequisites

- .NET 8 SDK/runtime
- Ollama running at `http://localhost:11434`
- Local model: `ollama pull openbmb/minicpm5-2b`
- Windows x64 for the bundled PaddleInference MKL runtime

No document is sent to an external AI API.

## Run locally

Run from the service project directory so `appsettings.json` is loaded:

```powershell
cd src/HinoDocumentAI.Service
dotnet run
```

The configured development address is `http://localhost:5080`. Swagger is
available in Development mode and the health endpoint is `GET /health`.

## Pipeline

### 1. Extract and classify

`POST /api/extract` as `multipart/form-data`, field name `file`.

Upload the PDF itself; callers do not need to convert it to images. The service
renders every PDF page internally at the configured OCR DPI and returns all
pages in one response. The Latin recognition model covers the two supported
document languages in this project: Indonesian and English.

The response contains one object per page:

- `documentType`: `invoice`, `deliveryNote`, `taxInvoice`, or `unknown`
- `classificationConfidence`
- `rawText`: human-readable rows in top-to-bottom, left-to-right order

`layoutText` and `regions` are diagnostics, not business output. They are hidden
by default. Add `?includeDiagnostics=true` only while investigating an OCR/layout
problem.

OCR first recognizes the original pixels. A second colour-recovery pass is
merged only for missing or materially better regions, so red/blue/green text is
recoverable without degrading normal black text. Reading order is rebuilt from
the four corners of each rotated OCR box. A serialized OCR lock protects the
native Paddle engine from concurrent access.

### 2. Clean/structure the complete extract response

Send the complete response from `/api/extract` directly to `POST /api/clean`:

```json
{
  "pages": [
    {
      "pageNumber": 1,
      "documentType": "invoice",
      "classificationConfidence": 1.0,
      "rawText": "..."
    }
  ]
}
```

All supported pages are cleaned sequentially. Pages classified as `unknown`
are returned with `status: "skipped"` instead of being forced into the wrong
schema. Each page result has `cleaned`, `skipped`, or `failed` status, and the
response includes counts for each status.

The original single-page JSON shape is still accepted by `POST /api/clean` and
returns the same document-level wrapper. Use `POST /api/clean/page` only when a
flat single-page response is required by an older caller.

Cleaning behaviour:

- Invoice and delivery note use supplier-independent document profiles,
  coordinate-aware prompts, a strict JSON Schema, and deterministic Ollama
  generation settings.
- Invoice fields and rows that can be proven from labels and arithmetic use a
  deterministic fast path. Unfamiliar supplier layouts fall back to Ollama.
- Indonesian e-Faktur uses a deterministic fast path when all critical fields
  can be proven. Ollama is only used when the OCR is incomplete/non-standard.
- Every generated value must be found in OCR. Ungrounded values are rejected.
- Invoice line items must satisfy `quantity × price = amount`.
- Totals and e-Faktur DPP/PPN are cross-validated.
- Missing or ambiguous values remain `null`; they are never guessed.

The response always contains the full canonical header schema, line items,
`confidence`, `requiresReview`, `warnings`, and field-level `evidence`.

## Important configuration

See `src/HinoDocumentAI.Service/appsettings.json`:

- `Ollama:KeepAlive`: unload delay for constrained server RAM
- `Ollama:Seed`, `NumPredict`, `NumContext`, `NumThread`
- `Ocr:PdfDpi` (300 by default)
- `Ocr:EnableColorContrastPass` and `ColorPassConfidenceThreshold`
- `Ocr:MaximumFileSizeBytes`
- `Cleaning:ReviewThreshold`

Production Kestrel is bound to `127.0.0.1:5080` because the service is intended
to be called only by the Invoice Portal on the same server.
