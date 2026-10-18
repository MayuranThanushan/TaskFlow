using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Infrastructure.Projects;

namespace TaskFlow.Tests;

public sealed class ProjectAccessPolicyTests
{
    [Theory]
    [InlineData(ProjectRole.Owner, ProjectOperation.Read, ProjectAccessDecision.Allowed)]
    [InlineData(ProjectRole.Owner, ProjectOperation.ManageProject, ProjectAccessDecision.Allowed)]
    [InlineData(ProjectRole.Owner, ProjectOperation.ManageMembers, ProjectAccessDecision.Allowed)]
    [InlineData(ProjectRole.Owner, ProjectOperation.WriteTasks, ProjectAccessDecision.Allowed)]
    [InlineData(ProjectRole.Owner, ProjectOperation.DeleteTasks, ProjectAccessDecision.Allowed)]
    [InlineData(ProjectRole.Member, ProjectOperation.Read, ProjectAccessDecision.Allowed)]
    [InlineData(ProjectRole.Member, ProjectOperation.ManageProject, ProjectAccessDecision.Forbidden)]
    [InlineData(ProjectRole.Member, ProjectOperation.ManageMembers, ProjectAccessDecision.Forbidden)]
    [InlineData(ProjectRole.Member, ProjectOperation.WriteTasks, ProjectAccessDecision.Allowed)]
    [InlineData(ProjectRole.Member, ProjectOperation.DeleteTasks, ProjectAccessDecision.Forbidden)]
    public void MatrixMatchesAdoptedPermissions(ProjectRole role, ProjectOperation operation, ProjectAccessDecision expected)
        => Assert.Equal(expected, ProjectAccessPolicy.Decide(1, role, operation));

    [Fact]
    public void MissingAndInvalidRolesAreNotVisible()
    {
        foreach (var operation in Enum.GetValues<ProjectOperation>())
        {
            Assert.Equal(ProjectAccessDecision.NotFound, ProjectAccessPolicy.Decide(1, null, operation));
            Assert.Equal(ProjectAccessDecision.NotFound, ProjectAccessPolicy.Decide(1, (ProjectRole)99, operation));
            Assert.Equal(ProjectAccessDecision.Unauthenticated, ProjectAccessPolicy.Decide(null, ProjectRole.Owner, operation));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectAccessPolicy.Decide(1, ProjectRole.Owner, (ProjectOperation)99));
    }

    [Fact]
    public async Task AnonymousAccessNeverOpensADatabaseConnection()
    {
        await using var database = new TaskFlowDbContext(new DbContextOptionsBuilder<TaskFlowDbContext>()
            .UseMySQL("Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Password=unused").Options, TimeProvider.System);
        var access = new ProjectAccess(database);
        Assert.Equal(ProjectAccessDecision.Unauthenticated, await access.CheckAsync(null, 1, ProjectOperation.Read));
        Assert.Equal(ProjectAccessDecision.Unauthenticated, await access.CheckTaskAsync(0, 1, ProjectOperation.Read));
    }
}
