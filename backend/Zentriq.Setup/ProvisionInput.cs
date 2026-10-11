using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Setup;

public sealed class ProvisionInput
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "install";             // install | repair | upgrade
    [JsonPropertyName("installDir")] public string InstallDir { get; set; } = AppContext.BaseDirectory;
    [JsonPropertyName("dataDir")] public string? DataDir { get; set; }
    [JsonPropertyName("productVersion")] public string ProductVersion { get; set; } = typeof(ProvisionInput).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    [JsonPropertyName("postgres")] public PostgresInput Postgres { get; set; } = new();
    [JsonPropertyName("https")] public HttpsInput Https { get; set; } = new();
    [JsonPropertyName("admin")] public AdminInput? Admin { get; set; }
    [JsonPropertyName("apiService")] public string ApiService { get; set; } = "ZentriqApi";
    [JsonPropertyName("workerService")] public string WorkerService { get; set; } = "ZentriqWorker";
    [JsonPropertyName("skipServiceStart")] public bool SkipServiceStart { get; set; }

    public static ProvisionInput Parse(string json) =>
        JsonSerializer.Deserialize<ProvisionInput>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
        ?? throw new SetupException("Provisioning input is empty.");
}

public sealed class PostgresInput
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "bundled";              // bundled | existing
    [JsonPropertyName("installerPath")] public string? InstallerPath { get; set; }
    [JsonPropertyName("installDir")] public string InstallDir { get; set; } = @"C:\Program Files\PostgreSQL\17";
    [JsonPropertyName("dataDir")] public string? DataDir { get; set; }
    [JsonPropertyName("serviceName")] public string ServiceName { get; set; } = "postgresql-x64-17";
    [JsonPropertyName("host")] public string Host { get; set; } = "localhost";
    [JsonPropertyName("port")] public int Port { get; set; } = 5432;
    [JsonPropertyName("superUser")] public string SuperUser { get; set; } = "postgres";
    [JsonPropertyName("superPassword")] public string? SuperPassword { get; set; }
    [JsonPropertyName("database")] public string Database { get; set; } = "zentriq";
    [JsonPropertyName("binDir")] public string? BinDir { get; set; }
    [JsonPropertyName("sslMode")] public string SslMode { get; set; } = "Prefer";
}

public sealed class HttpsInput
{
    [JsonPropertyName("port")] public int Port { get; set; } = 443;
    [JsonPropertyName("hostName")] public string? HostName { get; set; }
    [JsonPropertyName("certificateThumbprint")] public string? CertificateThumbprint { get; set; }
}

public sealed class AdminInput
{
    [JsonPropertyName("organization")] public string Organization { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
}

/// <summary>Written to Logs\install-result.json; read by the MSI completion dialog and by support.</summary>
public sealed class ProvisionResult
{
    [JsonPropertyName("succeeded")] public bool Succeeded { get; set; }
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("productVersion")] public string ProductVersion { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("failedStep")] public string? FailedStep { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("remedy")] public string? Remedy { get; set; }
    [JsonPropertyName("logPath")] public string? LogPath { get; set; }
    [JsonPropertyName("trialExpiresAt")] public DateTimeOffset? TrialExpiresAt { get; set; }
    [JsonPropertyName("checks")] public Dictionary<string, string> Checks { get; set; } = new();
    [JsonPropertyName("completedAt")] public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}
