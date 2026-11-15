using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Api.Operations;
public static class DatabaseMaintenanceCommand
{
    public static bool IsRequested(string[] args)=>args.Any(a=>a.StartsWith("--database",StringComparison.OrdinalIgnoreCase));
    public static async Task<int> ExecuteAsync(string[] args,IConfiguration configuration,TextWriter output,CancellationToken token=default)
    {
        void Report(object value)=>output.WriteLine(JsonSerializer.Serialize(value));
        if(args.Length!=1||args[0] is not ("--database-status" or "--database-update")){Report(new{code="database.command_invalid",message="Use --database-status or --database-update without additional arguments."});return 1;}
        try
        {
            var raw=configuration.GetConnectionString("TaskFlow");if(string.IsNullOrWhiteSpace(raw)){Report(new{code="database.configuration_missing",message="Supply the private database connection through environment or key-per-file settings."});return 1;}
            var connection=TaskFlowConnectionString.Normalize(raw);var parsed=new MySqlConnectionStringBuilder(connection);
            if(string.IsNullOrWhiteSpace(parsed.Database))throw new DatabaseConnectionConfigurationException();
            if(parsed.Server is not ("localhost" or "127.0.0.1" or "::1")&&parsed.SslMode!=MySqlSslMode.VerifyFull)
            {Report(new{code="database.tls_required",message="Remote maintenance requires SslMode=VerifyFull or a verified-TLS mysql URL."});return 1;}
            await using var db=new TaskFlowDbContext(new DbContextOptionsBuilder<TaskFlowDbContext>().UseMySQL(connection).Options,TimeProvider.System,configuration);
            async Task<DatabaseSchemaState> Status()=>DatabaseSchemaState.Compare(db.Database.GetMigrations(),await db.Database.GetAppliedMigrationsAsync(token));
            var before=await Status();
            void Inventory(DatabaseSchemaState state,string code)=>Report(new{code,expectedMigrationCount=state.ExpectedCount,appliedMigrationCount=state.AppliedCount,pendingMigrationCount=state.PendingMigrations.Length,pendingMigrations=state.PendingMigrations,unexpectedMigrationCount=state.UnexpectedCount});
            if(before.UnexpectedCount>0){Inventory(before,"database.schema_unrecognized");Report(new{message="Verify the database and deployed revision. No migration history was deleted and no downgrade was attempted."});return 3;}
            if(args[0]=="--database-status"){Inventory(before,before.IsCurrent?"database.schema_current":"database.schema_pending");return before.IsCurrent?0:2;}
            if(before.IsCurrent){Inventory(before,"database.schema_current");return 0;}
            Inventory(before,"database.migration_starting");
            // Explicit offline operator command only. Never called from web startup/health.
            await db.Database.MigrateAsync(token);
            var after=await Status();Inventory(after,after.IsCurrent?"database.migration_completed":"database.schema_mismatch");return after.IsCurrent?0:1;
        }
        catch(OperationCanceledException){Report(new{code="database.maintenance_cancelled",message="Check schema status before retrying; database DDL may not be transactional."});return 1;}
        catch(Exception error)
        {
            var mysql=error;while(mysql is not MySqlException&&mysql.InnerException is not null)mysql=mysql.InnerException;
            Report(new{code="database.maintenance_failed",databaseErrorNumber=mysql is MySqlException sql?(int?)sql.Number:null,message="Verify the private connection, network access, verified TLS and migration-account privileges. Raw diagnostics withheld. Check status before retrying partially applied DDL."});return 1;
        }
    }
}
