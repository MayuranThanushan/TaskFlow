using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Api.Persistence;

public sealed class TaskFlowDesignTimeFactory : IDesignTimeDbContextFactory<TaskFlowDbContext>
{
    public TaskFlowDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__TaskFlow");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Set ConnectionStrings__TaskFlow for EF commands; never commit credentials.");
        return new TaskFlowDbContext(new DbContextOptionsBuilder<TaskFlowDbContext>()
            .UseMySQL(connection).Options, TimeProvider.System);
    }
}
