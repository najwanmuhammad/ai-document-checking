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
    /// Strukturisasi teks mentah satu halaman jadi field baku, langsung
    /// oleh LLM (bukan lagi cascade dictionary/pattern/embedding manual).
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<CleanResponse>> Clean([FromBody] CleanRequest request)
    {
        var result = await _cleaningService.CleanAsync(request.DocumentType, request.RawText);
        return Ok(result);
    }
}