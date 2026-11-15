namespace TaskFlow.Infrastructure.Persistence;
public sealed record DatabaseSchemaState(int ExpectedCount,int AppliedCount,string[] PendingMigrations,int UnexpectedCount)
{
    public static bool IsSystemDatabase(string name)=>name.Trim().ToLowerInvariant() is "sys" or "mysql" or "information_schema" or "performance_schema" or "metrics_schema";
    public bool IsCurrent=>PendingMigrations.Length==0&&UnexpectedCount==0;
    public static DatabaseSchemaState Compare(IEnumerable<string> expected,IEnumerable<string> applied)
    {
        var known=expected.ToHashSet(StringComparer.Ordinal);var actual=applied.ToHashSet(StringComparer.Ordinal);
        return new(known.Count,actual.Count,known.Except(actual).Order(StringComparer.Ordinal).ToArray(),actual.Except(known).Count());
    }
}
