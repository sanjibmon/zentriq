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
    /// <summary>Additional plain-HTTP loopback port used only for local health probes; 0 disables.</summary>
    public int LoopbackHealthPort { get; set; } = 0;
}
