using TaskFlow.Domain.Entities;
namespace TaskFlow.Application.Features.Workspaces;
public sealed record WorkOptions(int Page=1,int PageSize=20,string Assignment="mine",string Queue="open",string? Search=null,long? ProjectId=null,TaskState? Status=null,TaskPriority? Priority=null,DateTime? FromUtc=null,DateTime? ToUtc=null,string SortBy="dueDateUtc",string SortDirection="asc");
public sealed record TaskWorkDto(string Id,string ProjectId,string Title,string Status,string Priority,string? AssigneeId,DateTime? DueDateUtc,DateTime CreatedAtUtc,DateTime UpdatedAtUtc,string Version);
public sealed record WorkItemDto(TaskWorkDto Task,string ProjectName,string? AssigneeName);
public sealed record WorkCountsDto(int Open,int Due,int Overdue,int Completed,int Total);
public sealed record WorkPage(IReadOnlyList<WorkItemDto> Items,int Page,int PageSize,int TotalCount,int TotalPages,WorkCountsDto Counts,DateTime AsOfUtc,DateTime DueThroughUtc);
