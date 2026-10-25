using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Infrastructure.Cloud;

public static class CloudProviders
{
    public const string Azure = "azure";
    public const string Aws = "aws";
    public const string VMware = "vmware";
    public const string HyperV = "hyperv";
    public static readonly string[] All = [Azure, Aws, VMware, HyperV];
    public static string Title(string p) => p switch { Azure => "Microsoft Azure", Aws => "Amazon Web Services", VMware => "VMware vCenter", HyperV => "Microsoft Hyper-V", _ => p };
}

public static class CloudKinds
{
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["vm"] = "Virtual machine", ["host"] = "Host", ["cluster"] = "Cluster", ["datastore"] = "Datastore", ["disk"] = "Disk",
        ["public_ip"] = "Public IP", ["network"] = "Network", ["security_group"] = "Security group", ["load_balancer"] = "Load balancer",
        ["storage"] = "Storage", ["database"] = "Database", ["kubernetes"] = "Kubernetes cluster", ["function"] = "Function",
    };
}

/// <summary>Non-secret settings of a connection (stored as JSON).</summary>
public sealed class CloudSettings
{
    // Azure: app registration (service principal) with the Reader role
    [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
    [JsonPropertyName("clientId")] public string? ClientId { get; set; }
    [JsonPropertyName("subscriptions")] public List<string> Subscriptions { get; set; } = [];   // empty = every subscription the app can read
    // AWS: IAM user access key with a read-only policy (ReadOnlyAccess / SecurityAudit)
    [JsonPropertyName("accessKeyId")] public string? AccessKeyId { get; set; }
    [JsonPropertyName("regions")] public List<string> Regions { get; set; } = [];              // empty = every enabled region
    // VMware vCenter 7.0+ (REST): host name, user (read-only role is enough)
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("skipCertificateCheck")] public bool SkipCertificateCheck { get; set; }
    // Hyper-V: host names or addresses (each host, or the nodes of a cluster), with a saved Windows credential
    [JsonPropertyName("hosts")] public List<string> Hosts { get; set; } = [];

    public static CloudSettings Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<CloudSettings>(json) ?? new();
    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>One resource as a collector reports it (normalised across providers).</summary>
public sealed class CloudItem
{
    public required string Kind { get; init; }
    public required string ExternalId { get; init; }
    public string? Name { get; set; }
    public string? Region { get; set; }
    public string? Group { get; set; }
    public string? ParentId { get; set; }
    public string? State { get; set; }
    /// <summary>When the provider knows how long the resource has been in this state (Hyper-V); otherwise Zentriq tracks it.</summary>
    public DateTimeOffset? StateSince { get; set; }
    public string? Size { get; set; }
    public string? Os { get; set; }
    public string? Hostname { get; set; }
    public string? PrivateIp { get; set; }
    public string? PublicIp { get; set; }
    public int? Cpu { get; set; }
    public long? MemoryMb { get; set; }
    public decimal? DiskGb { get; set; }
    public string? BiosUuid { get; set; }
    public List<string> Macs { get; set; } = [];
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> Properties { get; set; } = [];
    public DateTimeOffset? CreatedAt { get; set; }

    public bool? Bool(string key) => Properties.TryGetValue(key, out var v) && v is bool b ? b : null;
    public long? Long(string key) => Properties.TryGetValue(key, out var v) ? v switch { long l => l, int i => i, decimal d => (long)d, double d2 => (long)d2, _ => null } : null;
    public IEnumerable<int> Ports(string key) => Properties.TryGetValue(key, out var v) && v is IEnumerable<int> p ? p : [];
}

/// <summary>What a collector returns: the resources it could read, and warnings for the parts it could not.</summary>
public sealed class CloudSnapshot
{
    public List<CloudItem> Items { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>True when the collector read everything it should; false keeps resources it could not read (no false deletions).</summary>
    public bool Complete { get; set; } = true;
    /// <summary>Kinds that could only be read in part (permission missing, a region failed): those are not marked deleted.</summary>
    public HashSet<string> PartialKinds { get; } = [];
    public string? Identity { get; set; }

    public void Warn(string message, params string[] kinds)
    {
        Warnings.Add(message);
        if (kinds.Length == 0) Complete = false;
        foreach (var k in kinds) PartialKinds.Add(k);
    }
}

public interface ICloudCollector
{
    /// <summary>Reads everything the connection can see. Throws <see cref="CloudException"/> when it cannot sign in or reach the platform at all.</summary>
    Task<CloudSnapshot> CollectAsync(CloudSettings settings, string? secret, CancellationToken ct);
}

/// <summary>HTTP client for the cloud APIs: system proxy, sensible timeout, optional certificate check skip (vCenter with a self-signed certificate).</summary>
public static class CloudHttp
{
    public static HttpClient Create(bool skipCertificateCheck = false, TimeSpan? timeout = null)
    {
        var h = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All, UseProxy = true,
            DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials,
        };
        if (skipCertificateCheck) h.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        var c = new HttpClient(h) { Timeout = timeout ?? TimeSpan.FromSeconds(100) };
        c.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Zentriq", "1.1"));
        return c;
    }

    public static string Unreachable(string host, Exception ex) =>
        $"Could not reach {host}: {(ex is TaskCanceledException ? "timed out" : (ex.InnerException?.Message ?? ex.Message).TrimEnd('.'))}. The Zentriq server needs outbound HTTPS to it (check the firewall and proxy).";
}

public class CloudException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A finding on one resource.</summary>
public sealed record CloudFinding(string ExternalId, string Rule, string Severity, string Title, string? Detail);
