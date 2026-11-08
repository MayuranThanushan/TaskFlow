using MySql.Data.MySqlClient;

namespace TaskFlow.Infrastructure.Persistence;

public static class TaskFlowConnectionString
{
    public static string Normalize(string connection)
    {
        try
        {
            connection = connection.Trim();
            if (!connection.StartsWith("mysql://", StringComparison.OrdinalIgnoreCase)
                && !connection.StartsWith("mysqls://", StringComparison.OrdinalIgnoreCase))
                return new MySqlConnectionStringBuilder(connection).ConnectionString;

            var uri = new Uri(connection, UriKind.Absolute);
            if (string.IsNullOrWhiteSpace(uri.Host) || uri.Fragment.Length != 0) throw new FormatException();
            var credentials = uri.UserInfo.Split(':', 2);
            var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (credentials.Length != 2 || credentials[0].Length == 0 || credentials[1].Length == 0
                || string.IsNullOrWhiteSpace(database) || database.Contains('/')) throw new FormatException();
            // Cloud URLs must retain hostname and certificate verification. Reject ambiguous
            // or unsupported query settings instead of silently weakening transport security.
            foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = item.Split('=', 2);
                var name = Uri.UnescapeDataString(pair[0]);
                var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
                if (!(name.Equals("ssl-mode", StringComparison.OrdinalIgnoreCase) || name.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                    || !(value.Equals("VERIFY_IDENTITY", StringComparison.OrdinalIgnoreCase) || value.Equals("VerifyFull", StringComparison.OrdinalIgnoreCase))) throw new FormatException();
            }
            return new MySqlConnectionStringBuilder
            {
                Server = uri.Host, Port = uri.IsDefaultPort ? 3306u : checked((uint)uri.Port),
                Database = database, UserID = Uri.UnescapeDataString(credentials[0]),
                Password = Uri.UnescapeDataString(credentials[1]), SslMode = MySqlSslMode.VerifyFull
            }.ConnectionString;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or MySqlException)
        {
            // Never retain the parser's exception: it can contain the original secret.
            throw new DatabaseConnectionConfigurationException();
        }
    }
}

public sealed class DatabaseConnectionConfigurationException()
    : InvalidOperationException("Database connection format is invalid. Use a .NET MySQL connection string or mysql://user:password@host:port/database URL with verified TLS. A mysql shell command is not a connection string.");
