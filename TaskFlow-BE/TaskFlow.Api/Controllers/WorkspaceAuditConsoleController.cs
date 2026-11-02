using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;

namespace TaskFlow.Api.Controllers;

[Authorize, ApiController, Route("api/v2/workspaces/{workspaceId}/audit/console")]
[ServiceFilter(typeof(WorkspaceLifecycleFilter), Order = 400)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WorkspaceAuditConsoleController(IWorkspaceAuditConsole audit, IDataProtectionProvider protection, TimeProvider clock) : ControllerBase
{
    private static long Id(string? text) => WorkspaceReadFilter.TryId(text, out var id) ? id : throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    private WorkspaceReadContext Scope(string workspaceId) => new(Id(User.FindFirstValue(ClaimTypes.NameIdentifier)), Id(workspaceId));
    private IDataProtector Protector => protection.CreateProtector("TaskFlow.AuditExport.v1");
    private void Query(params string[] allowed)
    {
        foreach (var pair in Request.Query)
            if (!allowed.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || pair.Value.Count != 1)
                ModelState.AddModelError(pair.Key, "Unsupported or repeated query parameter.");
    }
    private AuditConsoleOptions Options(AuditConsoleQuery query)
    {
        long? actor = null;
        if (query.ActorId is not null)
        {
            if (WorkspaceReadFilter.TryId(query.ActorId, out var id)) actor = id;
            else ModelState.AddModelError("actorId", "Enter a positive decimal account ID.");
        }
        DateTime? Date(string? value, string field)
        {
            if (value is null) return null;
            if (Regex.IsMatch(value, ContractTimestamp) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.UtcDateTime.Year >= 1000) return date.UtcDateTime;
            ModelState.AddModelError(field, "Enter an ISO timestamp with an explicit offset."); return null;
        }
        var from = Date(query.FromUtc, "fromUtc"); var to = Date(query.ToUtc, "toUtc");
        if (from > to) ModelState.AddModelError("fromUtc", "The start must not be later than the end.");
        return new(query.Source, actor, query.Action, query.EntityType, query.EntityId, from, to, query.Outcome, query.ResourceState);
    }
    private const string ContractTimestamp = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$";
    [HttpGet, ProducesResponseType<AuditConsolePage>(200)]
    public async Task<IActionResult> List(string workspaceId, [FromQuery] AuditConsoleQuery query, CancellationToken token)
    {
        Query("page", "pageSize", "source", "actorId", "action", "entityType", "entityId", "fromUtc", "toUtc", "outcome", "resourceState");
        var options = Options(query);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await audit.ReadAsync(Scope(workspaceId), options, query.Page, query.PageSize, token: token));
    }
    [HttpPost("exports"), EnableRateLimiting("governance"), ProducesResponseType<AuditExportTicketDto>(200)]
    public async Task<IActionResult> Export(string workspaceId, AuditExportRequest request, CancellationToken token)
    {
        Query(); var options = Options(request.Filters);
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var scope = Scope(workspaceId);
        var page = await audit.ReadAsync(scope, options, 1, 1, token: token, forExport: true);
        var expires = clock.GetUtcNow().AddMinutes(5);
        var ticket = new ExportTicket(scope.ActorId, scope.WorkspaceId, options, long.Parse(page.HighWatermark, CultureInfo.InvariantCulture), expires);
        return Ok(new AuditExportTicketDto(Protector.Protect(JsonSerializer.Serialize(ticket)), expires, 1000));
    }
    [HttpPost("exports/download"), EnableRateLimiting("governance"), ProducesResponseType<AuditExportDto>(200)]
    public async Task<IActionResult> Download(string workspaceId, AuditDownloadRequest request, CancellationToken token)
    {
        Query(); if (!ModelState.IsValid) return ValidationProblem(ModelState);
        ExportTicket ticket;
        try { ticket = JsonSerializer.Deserialize<ExportTicket>(Protector.Unprotect(request.Ticket)) ?? throw new JsonException(); }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        { throw new ProjectAccessException(ProjectAccessDecision.NotFound); }
        var scope = Scope(workspaceId);
        if (ticket.ActorId != scope.ActorId || ticket.WorkspaceId != scope.WorkspaceId || clock.GetUtcNow() >= ticket.ExpiresAtUtc)
            throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        // Regenerate under current authorization. The ticket never grants authority or stores a sensitive file.
        var rows = await audit.ReadAsync(scope, ticket.Filters, 1, 1001, ticket.HighWatermark, token, forExport: true);
        return Ok(new AuditExportDto(clock.GetUtcNow(), rows.Items.Take(1000).ToArray(), rows.Items.Count > 1000, 1000));
    }
    private sealed record ExportTicket(long ActorId, long WorkspaceId, AuditConsoleOptions Filters, long HighWatermark, DateTimeOffset ExpiresAtUtc);
}
public sealed class AuditConsoleQuery
{
    [Range(1, 10001)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    [Required, RegularExpression("^(Governance|Projects)$")] public string Source { get; set; } = "Governance";
    [StringLength(19)] public string? ActorId { get; set; }
    [StringLength(80), RegularExpression("^[A-Za-z]+$")] public string? Action { get; set; }
    [StringLength(40)] public string? EntityType { get; set; }
    [StringLength(40), RegularExpression("^[0-9:]+$")] public string? EntityId { get; set; }
    [StringLength(40)] public string? FromUtc { get; set; }
    [StringLength(40)] public string? ToUtc { get; set; }
    [StringLength(20)] public string? Outcome { get; set; }
    [Required, RegularExpression("^(All|Active|Deleted)$")] public string ResourceState { get; set; } = "All";
}
public sealed class AuditExportRequest { [Required] public AuditConsoleQuery Filters { get; set; } = new(); }
public sealed class AuditDownloadRequest { [Required, StringLength(8192, MinimumLength = 1)] public string Ticket { get; set; } = ""; }
public sealed record AuditExportTicketDto(string Ticket, DateTimeOffset ExpiresAtUtc, int MaximumRecords);
public sealed record AuditExportDto(DateTimeOffset GeneratedAtUtc, IReadOnlyList<AuditConsoleRowDto> Items, bool Truncated, int MaximumRecords);
