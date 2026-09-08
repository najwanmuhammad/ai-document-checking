using Microsoft.AspNetCore.Mvc;
using HinoDocumentAI.Service.Models;
using HinoDocumentAI.Service.Services;

namespace HinoDocumentAI.Service.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MatchController : ControllerBase
{
    private readonly IMatchingService _matchingService;

    public MatchController(IMatchingService matchingService)
    {
        _matchingService = matchingService;
    }

    /// <summary>
    /// Bandingkan data hasil cleaning dengan data yang sudah ada di HES.
    /// </summary>
    [HttpPost]
    public ActionResult<MatchResponse> Match([FromBody] MatchRequest request)
    {
        var result = _matchingService.Match(request.CleanedData, request.HesData);
        return Ok(result);
    }
}
