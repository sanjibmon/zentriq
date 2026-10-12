namespace Zentriq.Infrastructure.Configuration;

/// <summary>Non-secret configuration, read from &lt;ConfigDir&gt;\zentriq.json (section "Zentriq").</summary>
public sealed class ZentriqOptions
{
    public const string Section = "Zentriq";

    public DatabaseOptions Database { get; set; } = new();
    public HttpsOptions Https { get; set; } = new();
    public string? PostgresBinDirectory { get; set; }
    public string? PostgresServiceName { get; set; }
    public string ProductVersion { get; set; } = typeof(ZentriqOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

public sealed class DatabaseOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "zentriq";
    public string AppUser { get; set; } = "zentriq_app";
    public string OwnerUser { get; set; } = "zentriq_owner";
    public string SslMode { get; set; } = "Prefer";
    public int MaxPoolSize { get; set; } = 50;
}

public sealed class HttpsOptions
{
    public int Port { get; set; } = 8443;
    public string HostName { get; set; } = "localhost";
    /// <summary>PFX file (normally in the Secrets directory). Its password is in the app secrets file.</summary>
    public string? CertificatePath { get; set; }
    public string? CertificateThumbprint { get; set; }
    /// <summary>Port for Zentriq Agents (mutual TLS). Only /api/agent is served on it, so it can be published to the internet without exposing the console. 0 disables agents.</summary>
    public int AgentPort { get; set; } = 8444;
    /// <summary>Address agents use to reach this server (e.g. a public gateway name). Defaults to https://HostName:AgentPort.</summary>
    public string? AgentPublicUrl { get; set; }
    /// <summary>Additional plain-HTTP loopback port used only for local health probes; 0 disables.</summary>
    public int LoopbackHealthPort { get; set; } = 0;
}
