using MySql.Data.MySqlClient;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Tests;

public sealed class ConnectionStringTests
{
    [Theory]
    [InlineData("mysql://pilot:p%40ss%3B%3D%2Bword@db.example:4000/test")]
    [InlineData("mysql://pilot:p%40ss%3B%3D%2Bword@db.example:4000/test?ssl-mode=VERIFY_IDENTITY")]
    [InlineData("mysqls://pilot:p%40ss%3B%3D%2Bword@db.example:4000/test?sslmode=VerifyFull")]
    public void CloudUrlPreservesEscapedCredentialsAndRequiresVerifiedTls(string input)
    {
        var value = new MySqlConnectionStringBuilder(TaskFlowConnectionString.Normalize(input));
        Assert.Equal("db.example", value.Server); Assert.Equal(4000u, value.Port);
        Assert.Equal("pilot", value.UserID); Assert.Equal("p@ss;=+word", value.Password);
        Assert.Equal("test", value.Database); Assert.Equal(MySqlSslMode.VerifyFull, value.SslMode);
    }

    [Fact]
    public void ExistingLocalConnectionKeepsItsExplicitTransportPolicy()
    {
        var value = new MySqlConnectionStringBuilder(TaskFlowConnectionString.Normalize("Server=localhost;Port=13306;Database=test;User ID=pilot;Password=local-only;SslMode=Disabled"));
        Assert.Equal(13306u, value.Port); Assert.Equal(MySqlSslMode.Disabled, value.SslMode);
    }

    [Fact]
    public void CloudUrlWithoutPortUsesMysqlDefault()
    {
        var value = new MySqlConnectionStringBuilder(TaskFlowConnectionString.Normalize("mysql://pilot:synthetic@db.example/test"));
        Assert.Equal(3306u, value.Port); Assert.Equal(MySqlSslMode.VerifyFull, value.SslMode);
    }

    [Theory]
    [InlineData("mysql -h db.example -u pilot -pPRIVATE_CANARY")]
    [InlineData("mysql://pilot:PRIVATE_CANARY@db.example")]
    [InlineData("mysql://pilot:PRIVATE_CANARY@db.example/test?ssl-mode=DISABLED")]
    [InlineData("mysql://pilot:PRIVATE_CANARY@db.example/test?unknown=secret")]
    [InlineData("mysql://pilot:PRIVATE_CANARY@db.example/test#fragment")]
    public void InvalidInputIsRejectedWithoutLeakingSecrets(string input)
    {
        var error = Assert.Throws<DatabaseConnectionConfigurationException>(() => TaskFlowConnectionString.Normalize(input));
        Assert.DoesNotContain("PRIVATE_CANARY", error.ToString()); Assert.Null(error.InnerException);
    }
}
