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

    /// Strukturisasi teks mentah satu halaman jadi field baku, langsung oleh LLM.
    [HttpPost]
    public async Task<ActionResult<CleanResponse>> Clean([FromBody] CleanRequest request)
    {
        try
        {
            var result = await _cleaningService.CleanAsync(
                request.DocumentType,
                request.RawText,
                HttpContext.RequestAborted);
            return Ok(result);
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
}