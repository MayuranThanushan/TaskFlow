using Microsoft.Extensions.Configuration;
using TaskFlow.Api.Operations;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Tests;
public sealed class DatabaseMaintenanceTests
{
    [Fact] public void SchemaInventorySeparatesMissingFromUnknownWithoutDiscardingEither()
    {
        var schema=DatabaseSchemaState.Compare(["20260101000000_First","20260102000000_Second"],["20260101000000_First","20990101000000_Future"]);
        Assert.False(schema.IsCurrent);Assert.Equal(2,schema.ExpectedCount);Assert.Equal(2,schema.AppliedCount);Assert.Equal(new[]{"20260102000000_Second"},schema.PendingMigrations);Assert.Equal(1,schema.UnexpectedCount);
    }
    [Fact] public async Task MissingPrivateConfigurationFailsWithInstructionsWithoutStartingAHost()
    {
        using var output=new StringWriter();var result=await DatabaseMaintenanceCommand.ExecuteAsync(["--database-status"],new ConfigurationBuilder().Build(),output);
        Assert.Equal(1,result);Assert.Contains("database.configuration_missing",output.ToString());
    }
    [Fact] public async Task InvalidArgumentsNeverEchoPrivateValues()
    {
        using var output=new StringWriter();var result=await DatabaseMaintenanceCommand.ExecuteAsync(["--database-update","PRIVATE_ARGUMENT_CANARY"],new ConfigurationBuilder().Build(),output);
        Assert.Equal(1,result);Assert.DoesNotContain("PRIVATE_ARGUMENT_CANARY",output.ToString());
    }
    [Theory]
    [InlineData("Server=example.test;Database=test;User ID=PRIVATE_USER_CANARY;Password=PRIVATE_PASSWORD_CANARY;SslMode=Preferred;")]
    [InlineData("mysql -h PRIVATE_HOST_CANARY -pPRIVATE_PASSWORD_CANARY")]
    public async Task UnsafeOrMalformedRemoteConnectionsDoNotEchoCredentials(string connection)
    {
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:TaskFlow"]=connection}).Build();using var output=new StringWriter();
        Assert.Equal(1,await DatabaseMaintenanceCommand.ExecuteAsync(["--database-status"],configuration,output));Assert.DoesNotContain("PRIVATE_",output.ToString());
    }
    [Theory]
    [InlineData("sys")][InlineData("SYS")][InlineData("mysql")][InlineData("information_schema")][InlineData("performance_schema")][InlineData("metrics_schema")]
    public async Task SystemSchemasAreRefusedBeforeAnyConnectionOrMigration(string database)
    {
        var connection="mysql://PRIVATE_USER_CANARY:PRIVATE_PASSWORD_CANARY@does-not-exist.invalid:4000/"+database;
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:TaskFlow"]=connection}).Build();using var output=new StringWriter();
        Assert.Equal(1,await DatabaseMaintenanceCommand.ExecuteAsync(["--database-update"],configuration,output));Assert.Contains("database.system_schema",output.ToString());Assert.DoesNotContain("database.migration_starting",output.ToString());Assert.DoesNotContain("PRIVATE_",output.ToString());
    }
    [Theory][InlineData("taskflow")][InlineData("taskflow_test")][InlineData("test")]
    public void ApplicationDatabaseNamesRemainSupported(string database)=>Assert.False(DatabaseSchemaState.IsSystemDatabase(database));

    [Fact] public async Task MissingCaFailsBeforeNetworkAndDoesNotExposeConnectionOrPath()
    {
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["ConnectionStrings:TaskFlow"]="mysql://PRIVATE_USER_CANARY:PRIVATE_PASSWORD_CANARY@does-not-exist.invalid:4000/taskflow",
            ["DatabaseMaintenance:CaFile"]="PRIVATE_CA_CANARY/missing.pem"
        }).Build();using var output=new StringWriter();
        Assert.Equal(1,await DatabaseMaintenanceCommand.ExecuteAsync(["--database-status"],configuration,output));
        Assert.Contains("database.ca_missing",output.ToString());Assert.DoesNotContain("PRIVATE_",output.ToString());
    }

}
