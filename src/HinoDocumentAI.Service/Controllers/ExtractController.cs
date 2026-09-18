using Microsoft.AspNetCore.Mvc;
using HinoDocumentAI.Service.Models;
using HinoDocumentAI.Service.Services;

namespace HinoDocumentAI.Service.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ExtractController : ControllerBase
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"
    };

    private readonly IOcrService _ocrService;
    private readonly ILogger<ExtractController> _logger;
    private readonly long _maximumFileSize;

    public ExtractController(
        IOcrService ocrService,
        IConfiguration configuration,
        ILogger<ExtractController> logger)
    {
        _ocrService = ocrService;
        _logger = logger;
        _maximumFileSize = configuration.GetValue("Ocr:MaximumFileSizeBytes", 20 * 1024 * 1024L);
    }

    /// Ekstrak teks mentah per halaman + klasifikasi jenis dokumen per halaman.
    /// Halaman dengan DocumentType=Unknown (mis. lampiran surat, instruksi internal, dst.)
    /// sebaiknya di-skip oleh pemanggil sebelum dikirim ke /clean.
    [HttpPost]
    public async Task<ActionResult<ExtractResponse>> Extract(
        IFormFile file,
        [FromQuery] bool includeDiagnostics = false)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("File kosong atau tidak valid.");
        }

        if (file.Length > _maximumFileSize)
        {
            return BadRequest($"Ukuran file melebihi batas {_maximumFileSize} byte.");
        }

        string extension = Path.GetExtension(file.FileName);
        if (!AllowedExtensions.Contains(extension))
        {
            return BadRequest("Format file tidak didukung. Gunakan PDF atau format gambar umum.");
        }

        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + extension);
        try
        {
            await using (var stream = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(stream);
            }

            var pages = await _ocrService.ExtractPagesAsync(tempPath, HttpContext.RequestAborted);
            var classified = pages
                .Select((page, index) =>
                {
                    DocumentClassification classification =
                        DocumentClassifier.ClassifyDetailed(page.RawText);
                    return new OcrPage(
                        PageNumber: index + 1,
                        DocumentType: classification.DocumentType,
                        ClassificationConfidence: classification.Confidence,
                        RawText: page.RawText,
                        LayoutText: includeDiagnostics ? page.LayoutText : null,
                        Regions: includeDiagnostics ? page.Regions : null);
                })
                .ToList();

            return Ok(new ExtractResponse(classified));
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "File OCR tidak dapat dibaca: {FileName}", file.FileName);
            return BadRequest(ex.Message);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }
}
