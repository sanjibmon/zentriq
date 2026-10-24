using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Infrastructure.Integrations;

/// <summary>Event types the detectors raise, with the default severity and a description for the console.</summary>
public static class EventTypes
{
    public sealed record Info(string Type, string Name, string Description, bool PerDevice);

    public static readonly IReadOnlyList<Info> All =
    [
        new("vulnerability.new", "New vulnerabilities", "New vulnerability findings on a device (one message per device per evaluation; severity = the worst finding, known exploited = critical).", true),
        new("vulnerability.overdue", "Vulnerabilities past their due date", "Open findings that passed their SLA due date.", true),
        new("security.failed", "Security checks failing", "Configuration checks that started failing on a device.", true),
        new("hardware.changed", "Hardware changed", "Hardware added, removed or replaced on a device (memory, disks, processor, serial number, network adapters…).", true),
        new("software.changed", "Software changed", "Software installed, uninstalled or updated on a device, Windows updates and operating system upgrades.", true),
        new("asset.discovered", "New device discovered", "A device seen for the first time (discovery, Active Directory or agent enrolment).", true),
        new("asset.offline", "Device not seen", "A device has not been seen (agent, scan or inventory) for the configured number of hours.", true),
        new("agent.offline", "Agent not checking in", "An agent has not checked in for the configured number of hours.", true),
        new("scan.completed", "Discovery scan finished", "A discovery scan finished (with or without errors).", false),
        new("scan.failed", "Discovery scan failed", "A discovery scan failed or was cancelled.", false),
        new("accessory.low", "IT accessory stock low", "An IT accessory fell to or below its reorder level at a location (out of stock = medium).", false),
        new("lifecycle.warranty", "Warranty ending", "An IT asset's warranty ends in 30 days, or ended today (Asset Lifecycle).", true),
        new("lifecycle.contract", "Contract ending", "A support, AMC, lease or insurance contract reaches its reminder date or ends (Asset Lifecycle).", false),
        new("lifecycle.loan_overdue", "Loaned asset not returned", "An IT asset on loan passed its return date (Asset Lifecycle).", true),
        new("lifecycle.missing", "IT asset not seen", "An IT asset in use was not seen for the configured days and is now Missing · Not seen (Asset Lifecycle).", true),
        new("lifecycle.retired_seen", "Retired IT asset seen again", "A retired or disposed IT asset was seen on the network again (Asset Lifecycle).", true),
        new("job.failed", "Background job failed", "A background job (report delivery, feed update, inventory...) failed.", false),
    ];

    public static bool Exists(string type) => All.Any(e => e.Type == type);

    public static readonly string[] Severities = ["info", "low", "medium", "high", "critical"];
    public static int Rank(string? severity) => Math.Max(0, Array.IndexOf(Severities, severity ?? "info"));
}

public static class ChannelKinds
{
    public static readonly string[] All = ["webhook", "slack", "teams", "email", "syslog", "servicenow", "jira"];
    /// <summary>Channels that create tickets (one per event; the ticket number is kept in the delivery log).</summary>
    public static bool IsTicketing(string kind) => kind is "servicenow" or "jira";
}

/// <summary>Non-secret channel settings (stored as JSON, returned by the API).</summary>
public sealed class ChannelSettings
{
    // webhook
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("headerName")] public string? HeaderName { get; set; }
    // e-mail
    [JsonPropertyName("recipients")] public List<string> Recipients { get; set; } = [];
    // syslog
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }   // udp | tcp
    [JsonPropertyName("format")] public string? Format { get; set; }       // cef | json
    // ServiceNow / Jira
    [JsonPropertyName("instanceUrl")] public string? InstanceUrl { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("table")] public string? Table { get; set; }
    [JsonPropertyName("assignmentGroup")] public string? AssignmentGroup { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("projectKey")] public string? ProjectKey { get; set; }
    [JsonPropertyName("issueType")] public string? IssueType { get; set; }
    [JsonPropertyName("auth")] public string? Auth { get; set; }           // basic | bearer (Jira Data Center tokens)
    [JsonPropertyName("labels")] public List<string> Labels { get; set; } = [];
    /// <summary>Hint shown in the console for secret values that are set (e.g. "hooks.slack.com/…").</summary>
    [JsonPropertyName("secretHint")] public string? SecretHint { get; set; }
}

/// <summary>Secret channel values (encrypted at rest, never returned).</summary>
public sealed class ChannelSecrets
{
    [JsonPropertyName("url")] public string? Url { get; set; }                     // Slack / Teams incoming webhook URL
    [JsonPropertyName("signingSecret")] public string? SigningSecret { get; set; } // webhook HMAC key
    [JsonPropertyName("headerValue")] public string? HeaderValue { get; set; }     // webhook auth header value
    [JsonPropertyName("password")] public string? Password { get; set; }           // ServiceNow password / Jira API token
}

public sealed record Channel(Guid Id, string Kind, string Name, bool Enabled, ChannelSettings Settings, ChannelSecrets Secrets);

/// <summary>An event as sent to channels (the webhook body is this object as JSON).</summary>
public sealed class OutboundEvent
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "info";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("occurredAt")] public DateTimeOffset OccurredAt { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("asset")] public OutboundAsset? Asset { get; set; }
    [JsonPropertyName("data")] public JsonElement? Data { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "Zentriq";
}

public sealed class OutboundAsset
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("criticality")] public string? Criticality { get; set; }
    [JsonPropertyName("os")] public string? Os { get; set; }
}

public sealed class RuleConditions
{
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];
    [JsonPropertyName("criticality")] public List<string> Criticality { get; set; } = [];

    public bool Matches(OutboundAsset? asset) =>
        (Categories.Count == 0 || (asset?.Category is { } c && Categories.Contains(c))) &&
        (Criticality.Count == 0 || (asset?.Criticality is { } k && Criticality.Contains(k)));
}

/// <summary>Result of one send: ok, a short response (HTTP status, ticket number) and an error text.</summary>
public sealed record SendResult(bool Ok, string? Response, string? Error, bool Permanent = false, string? Ticket = null);
