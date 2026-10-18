using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Application.Features.Activity;
namespace TaskFlow.Api.Controllers;

[ApiController, Route("api/v1/projects/{projectId}/activity")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ActivityController(IActivityService activity) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ActivityPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string projectId, [FromQuery] ActivityQuery query, CancellationToken cancellationToken)
    {
        long id = 0;
        if (projectId.Length == 0 || !projectId.All(char.IsAsciiDigit) || !long.TryParse(projectId, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0) ModelState.AddModelError("projectId", "Enter a positive decimal ID within the signed BIGINT range.");
        string[] allowed = ["page", "pageSize", "fromUtc", "toUtc"];
        foreach (var parameter in Request.Query) if (!allowed.Contains(parameter.Key, StringComparer.OrdinalIgnoreCase) || parameter.Value.Count != 1) ModelState.AddModelError(parameter.Key, "Unsupported or repeated query parameter.");
        if ((long)(query.Page - 1) * query.PageSize > int.MaxValue) ModelState.AddModelError("page", "The requested page exceeds the supported range.");
        var from = Bound(query.FromUtc, "fromUtc"); var to = Bound(query.ToUtc, "toUtc");
        if (from > to) ModelState.AddModelError("fromUtc", "The start must not be later than the end.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var actor = long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var user) && user > 0 ? user : (long?)null;
        return Ok(await activity.ListAsync(actor, id, new(query.Page, query.PageSize, from, to), cancellationToken));
    }
    private DateTime? Bound(string? text, string field)
    {
        if (text is null && !Request.Query.ContainsKey(field)) return null;
        if (text is null || !Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.UtcDateTime.Year < 1000)
        { ModelState.AddModelError(field, "Enter an ISO timestamp with Z or an explicit offset, within UTC years 1000-9999."); return null; }
        return parsed.UtcDateTime;
    }
}
public sealed class ActivityQuery
{
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    public string? FromUtc { get; set; }
    public string? ToUtc { get; set; }
}
