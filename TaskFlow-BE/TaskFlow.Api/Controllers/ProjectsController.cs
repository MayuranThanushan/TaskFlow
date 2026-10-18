using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Application.Features.Projects;

namespace TaskFlow.Api.Controllers;

[ApiController, Route("api/v1/projects")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProjectsController(IProjectService projects) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(ProjectWriteRequest request, CancellationToken cancellationToken)
    {
        var project = await projects.CreateAsync(Actor(), request.Name, request.Description, cancellationToken);
        return CreatedAtAction(nameof(Get), new { projectId = project.Id }, project);
    }

    [HttpGet]
    [ProducesResponseType<ProjectPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] ProjectListQuery query, CancellationToken cancellationToken)
    {
        string[] allowed = ["page", "pageSize", "search", "sortBy", "sortDirection"];
        foreach (var parameter in Request.Query)
            if (!allowed.Contains(parameter.Key, StringComparer.OrdinalIgnoreCase) || parameter.Value.Count != 1)
                ModelState.AddModelError(parameter.Key, "Unsupported or repeated query parameter.");
        if ((long)(query.Page - 1) * query.PageSize > int.MaxValue)
            ModelState.AddModelError("page", "The requested page exceeds the supported range.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await projects.ListAsync(Actor(), new(query.Page, query.PageSize, query.Search, query.SortBy, query.SortDirection), cancellationToken));
    }

    [HttpGet("{projectId}")]
    [ProducesResponseType<ProjectDetailsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string projectId, CancellationToken cancellationToken)
    {
        if (!TryId(projectId, out var id)) return ValidationProblem(ModelState);
        return Ok(await projects.GetAsync(Actor(), id, cancellationToken));
    }

    [HttpPut("{projectId}")]
    public async Task<IActionResult> Update(string projectId, ProjectWriteRequest request, CancellationToken cancellationToken)
    {
        if (!TryId(projectId, out var id)) return ValidationProblem(ModelState);
        return Ok(await projects.UpdateAsync(Actor(), id, request.Name, request.Description, cancellationToken));
    }

    [HttpDelete("{projectId}")]
    public async Task<IActionResult> Delete(string projectId, CancellationToken cancellationToken)
    {
        if (!TryId(projectId, out var id)) return ValidationProblem(ModelState);
        await projects.DeleteAsync(Actor(), id, cancellationToken);
        return NoContent();
    }

    private bool TryId(string value, out long id)
    {
        id = 0;
        if (value.Length > 0 && value.All(char.IsAsciiDigit)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0) return true;
        ModelState.AddModelError("projectId", "Enter a positive decimal ID within the signed BIGINT range.");
        return false;
    }

    private long? Actor() => long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None,
        CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}

public sealed class ProjectWriteRequest
{
    private string name = "";
    private string? description;
    [Required, StringLength(150)]
    public string Name { get => name; set => name = value?.Trim() ?? ""; }
    [StringLength(10_000)]
    public string? Description { get => description; set => description = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
}

public sealed class ProjectListQuery
{
    private string? search;
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    [StringLength(200)] public string? Search { get => search; set => search = value?.Trim(); }
    [Required, RegularExpression("^(name|createdAtUtc|updatedAtUtc)$")] public string SortBy { get; set; } = "createdAtUtc";
    [Required, RegularExpression("^(asc|desc)$")] public string SortDirection { get; set; } = "desc";
}
