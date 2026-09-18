using Microsoft.AspNetCore.Mvc;
using HinoDocumentAI.Service.Models;
using HinoDocumentAI.Service.Services;

namespace HinoDocumentAI.Service.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CleanController : ControllerBase
{
    private readonly ICleaningService _cleaningService;

    public CleanController(ICleaningService cleaningService)
    {
        _cleaningService = cleaningService;
    }

    /// Strukturisasi semua halaman dari response /extract. Halaman Unknown
    /// dicatat sebagai skipped agar satu lampiran tidak menggagalkan dokumen.
    [HttpPost]
    public async Task<ActionResult<CleanDocumentResponse>> Clean([FromBody] CleanDocumentRequest request)
    {
        List<OcrPage>? pages = request.Pages;
        if (pages is null && !string.IsNullOrWhiteSpace(request.RawText))
        {
            if (request.DocumentType == DocumentType.Unknown)
            {
                return BadRequest(new { error = "documentType wajib invoice, deliveryNote, atau taxInvoice." });
            }

            pages =
            [
                new OcrPage(
                    request.PageNumber,
                    request.DocumentType,
                    request.ClassificationConfidence,
                    request.RawText,
                    request.LayoutText,
                    request.Regions)
            ];
        }

        if (pages is null || pages.Count == 0)
        {
            return BadRequest(new { error = "pages wajib berisi minimal satu halaman hasil /api/extract." });
        }

        var results = new List<CleanPageResult>(pages.Count);
        foreach (OcrPage page in pages.OrderBy(page => page.PageNumber))
        {
            HttpContext.RequestAborted.ThrowIfCancellationRequested();

            if (page.DocumentType == DocumentType.Unknown)
            {
                results.Add(new CleanPageResult(
                    page.PageNumber,
                    page.DocumentType,
                    "skipped",
                    null,
                    "Jenis dokumen tidak termasuk invoice, delivery note, atau faktur pajak."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(page.RawText))
            {
                results.Add(new CleanPageResult(
                    page.PageNumber,
                    page.DocumentType,
                    "failed",
                    null,
                    "rawText halaman kosong."));
                continue;
            }

            try
            {
                CleanResponse data = await CleanPageAsync(
                    page.DocumentType,
                    page.RawText,
                    page.LayoutText,
                    page.Regions);
                results.Add(new CleanPageResult(
                    page.PageNumber,
                    page.DocumentType,
                    "cleaned",
                    data));
            }
            catch (ArgumentException ex)
            {
                results.Add(new CleanPageResult(
                    page.PageNumber, page.DocumentType, "failed", null, ex.Message));
            }
            catch (TimeoutException ex)
            {
                results.Add(new CleanPageResult(
                    page.PageNumber, page.DocumentType, "failed", null, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                results.Add(new CleanPageResult(
                    page.PageNumber, page.DocumentType, "failed", null, ex.Message));
            }
        }

        return Ok(new CleanDocumentResponse(
            results,
            results.Count(result => result.Status == "cleaned"),
            results.Count(result => result.Status == "skipped"),
            results.Count(result => result.Status == "failed")));
    }

    /// Backward-compatible endpoint untuk satu halaman.
    [HttpPost("page")]
    public async Task<ActionResult<CleanResponse>> CleanPage([FromBody] CleanRequest request)
    {
        if (request.DocumentType == DocumentType.Unknown)
        {
            return BadRequest(new { error = "documentType wajib invoice, deliveryNote, atau taxInvoice." });
        }

        if (string.IsNullOrWhiteSpace(request.RawText))
        {
            return BadRequest(new { error = "rawText wajib diisi." });
        }

        try
        {
            CleanResponse result = await CleanPageAsync(
                request.DocumentType,
                request.RawText,
                request.LayoutText,
                request.Regions);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (TimeoutException ex)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                error = ex.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = ex.Message
            });
        }
    }

    private Task<CleanResponse> CleanPageAsync(
        DocumentType documentType,
        string rawText,
        string? layoutText,
        List<OcrTextRegion>? regions)
    {
        // Rebuild from coordinates when present. Besides improving normal
        // requests, this also repairs payloads produced before the
        // RotatedRect width/height bug was fixed.
        if (regions is { Count: > 0 })
        {
            OcrPageLayout rebuilt = OcrLayoutBuilder.Build(regions);
            rawText = rebuilt.RawText;
            layoutText = rebuilt.LayoutText;
        }

        return _cleaningService.CleanAsync(
            documentType,
            rawText,
            layoutText,
            HttpContext.RequestAborted);
    }
}
