using Microsoft.AspNetCore.Mvc;
using HinoDocumentAI.Service.Models;
using HinoDocumentAI.Service.Services;
using Microsoft.AspNetCore.Cors.Infrastructure;
using System.Xml.Linq;


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
    /// Terima file scan (invoice/faktur pajak/surat jalan) dari Invoice
    /// Portal, kembalikan raw text + field mentah hasil OCR.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ExtractResponse>> Extract(IFormFile file, [FromForm] string? documentTypeHint)
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

            var (rawText, fields) = await _ocrService.ExtractAsync(tempPath);

            // TODO (FR-8): jalankan klasifikasi jenis dokumen di sini
            // berdasarkan CanonicalFields.DocumentTypeKeywords dan isi
            // rawText, alih-alih langsung memakai documentTypeHint dari
            // caller mentah-mentah — hint dari .NET Invoice Portal tetap
            // berguna sebagai sinyal tambahan, tapi divalidasi ulang,
            // karena satu file bisa berisi jenis dokumen yang tidak
            // terduga (lihat PRD Bagian 13.2 poin 5 — temuan dokumen
            // yang ternyata tidak sesuai konteksnya).
            var detectedType = DocumentType.Unknown;

            return Ok(new ExtractResponse(
                DetectedDocumentType: detectedType,
                RawText: rawText,
                Fields: fields
            ));
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
