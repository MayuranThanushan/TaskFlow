namespace TaskFlow.Tests.Fixtures;

public class MySqlFixtureSafetyTests
{
    [Theory]
    [InlineData("Server=localhost;Database=taskflow_dev;User ID=tester")]
    [InlineData("Server=localhost;Database=production;User ID=tester")]
    [InlineData("Server=localhost;Database=taskflow_test;User ID=root")]
    [InlineData("Server=localhost;Database=taskflow_test")]
    public void RejectsUnsafeConnectionsBeforeAnyDatabaseAccess(string connection) =>
        Assert.Throws<InvalidOperationException>(() => MySqlFixture.ValidateConnection(connection));

    [Theory]
    [InlineData("Server=localhost;Database=taskflow_test;User ID=taskflow_test")]
    [InlineData("Server=localhost;Database=taskflow_test_ci;User ID=taskflow_test")]
    public void AcceptsExplicitTestDatabasesAndRestrictedAccounts(string connection) =>
        MySqlFixture.ValidateConnection(connection);
}
