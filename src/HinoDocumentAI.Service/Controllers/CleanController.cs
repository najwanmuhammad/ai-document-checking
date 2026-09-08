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

    /// <summary>
    /// Normalisasi label mentah hasil OCR ke field baku. Urutan metode:
    /// dictionary sinonim -> embedding (bge-m3) -> LLM fallback (qwen2.5),
    /// lihat CleaningService untuk detail.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<CleanResponse>> Clean([FromBody] CleanRequest request)
    {
        var cleaned = await _cleaningService.CleanAsync(request.DocumentType, request.Fields);
        return Ok(new CleanResponse(cleaned));
    }
}
