using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Projects;

public sealed class ProjectMembersService(TaskFlowDbContext database, IProjectTransactions transactions) : IProjectMembersService
{
    public async Task<ProjectMemberList> ListAsync(long? actorId, long projectId, CancellationToken cancellationToken = default)
    {
        if (actorId is null or <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var project = await ProjectAccess.VisibleProjects(database, actorId.Value).AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        var owner = await database.Users.AsNoTracking().Where(u => u.Id == project.OwnerId).Select(u => new { u.DisplayName }).SingleAsync(cancellationToken);
        var members = await (from member in database.ProjectMembers.AsNoTracking()
                             join user in database.Users.AsNoTracking() on member.UserId equals user.Id
                             where member.ProjectId == projectId
                             orderby member.UserId
                             select new { member.UserId, user.DisplayName, member.JoinedAtUtc }).ToListAsync(cancellationToken);
        var items = new List<ProjectMemberDto> { new(Id(project.OwnerId), owner.DisplayName, "Owner", project.CreatedAtUtc) };
        items.AddRange(members.Select(m => new ProjectMemberDto(Id(m.UserId), m.DisplayName, "Member", m.JoinedAtUtc)));
        await snapshot.CommitAsync(cancellationToken);
        return new(items);
    }
    public async Task<ProjectMemberDto> AddAsync(long? actorId, long projectId, long userId, CancellationToken cancellationToken = default)
    {
        var added = await transactions.ExecuteAsync(actorId, projectId, ProjectOperation.ManageMembers,
            (scope, ct) => scope.AddMemberAsync(userId, ct), cancellationToken);
        return new(Id(added.Member.UserId), added.DisplayName, "Member", added.Member.JoinedAtUtc);
    }
    public async Task RemoveAsync(long? actorId, long projectId, long userId, CancellationToken cancellationToken = default)
    {
        await transactions.ExecuteAsync(actorId, projectId, ProjectOperation.ManageMembers, async (scope, ct) =>
        {
            await scope.RemoveMemberAsync(userId, ct);
            return true;
        }, cancellationToken);
    }
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
