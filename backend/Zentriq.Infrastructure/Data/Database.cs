using Npgsql;
using Zentriq.Infrastructure.Configuration;

namespace Zentriq.Infrastructure.Data;

public static class ConnectionStrings
{
    public static string Build(DatabaseOptions db, string user, string password, string? database = null, string applicationName = "Zentriq")
    {
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = db.Host,
            Port = db.Port,
            Database = database ?? db.Database,
            Username = user,
            Password = password,
            ApplicationName = applicationName,
            MaxPoolSize = db.MaxPoolSize,
            Timeout = 15,
            CommandTimeout = 60,
            SslMode = Enum.TryParse<SslMode>(db.SslMode, true, out var m) ? m : SslMode.Prefer,
            IncludeErrorDetail = false,
        };
        return csb.ConnectionString;
    }

    /// <summary>Connection string description safe for logs (no password).</summary>
    public static string Describe(string connectionString)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        return $"Host={csb.Host};Port={csb.Port};Database={csb.Database};Username={csb.Username}";
    }
}
