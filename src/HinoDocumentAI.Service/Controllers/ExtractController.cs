using Microsoft.AspNetCore.Mvc;
using HinoDocumentAI.Service.Models;
using HinoDocumentAI.Service.Services;

namespace HinoDocumentAI.Service.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ExtractController : ControllerBase
{
    private readonly IOcrService _ocrService;
    private readonly ILogger<ExtractController> _logger;

    public ExtractController(IOcrService ocrService, ILogger<ExtractController> logger)
    {
        _ocrService = ocrService;
        _logger = logger;
    }

    /// <summary>
    /// Ekstrak teks mentah per halaman + klasifikasi jenis dokumen per
    /// halaman. Halaman dengan DetectedType=Unknown (mis. lampiran surat,
    /// instruksi internal, dst.) sebaiknya di-skip oleh pemanggil sebelum
    /// dikirim ke /clean — jangan dipaksa diproses.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ExtractResponse>> Extract(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("File kosong atau tidak valid.");
        }

        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + Path.GetExtension(file.FileName));
        try
        {
            await using (var stream = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(stream);
            }

            var pages = await _ocrService.ExtractPagesAsync(tempPath);
            var classified = pages
                .Select(p => new OcrPage(p.PageNumber, DocumentClassifier.Classify(p.RawText), p.RawText))
                .ToList();

            return Ok(new ExtractResponse(classified));
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