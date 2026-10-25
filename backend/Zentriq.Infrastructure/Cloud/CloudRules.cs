namespace Zentriq.Infrastructure.Cloud;

/// <summary>
/// Findings from a sync: cost hygiene (unattached disks, idle public IPs, machines switched off but kept, stopped but still
/// billed) and security basics (RDP / SSH / all ports open to the internet, public or unencrypted storage and databases),
/// virtualization capacity (datastores filling up, memory over-commit, old checkpoints) and missing tags.
/// </summary>
public static class CloudRules
{
    public const int OffDays = 30;
    public const int SnapshotDays = 7;

    public static readonly IReadOnlyDictionary<string, string> RuleTitles = new Dictionary<string, string>
    {
        ["disk_unattached"] = "Unattached disk", ["ip_idle"] = "Public IP not in use", ["vm_off"] = "Virtual machine switched off for a long time",
        ["vm_stopped_billed"] = "Stopped but still billed", ["exposed_admin"] = "RDP / SSH open to the internet", ["exposed_all"] = "All ports open to the internet",
        ["storage_public"] = "Storage open to the public", ["storage_http"] = "Storage accepts unencrypted HTTP", ["storage_unencrypted"] = "Storage not encrypted",
        ["db_public"] = "Database reachable from the internet", ["db_unencrypted"] = "Database storage not encrypted", ["disk_unencrypted"] = "Disk not encrypted",
        ["snapshot_old"] = "Old checkpoint / snapshot", ["datastore_full"] = "Datastore almost full", ["host_overcommit"] = "Host memory over-committed",
        ["host_eos"] = "Host operating system out of support", ["untagged"] = "No tags",
    };

    public static List<CloudFinding> Evaluate(string provider, IReadOnlyList<CloudItem> items, Func<CloudItem, DateTimeOffset?> since, DateTimeOffset now)
    {
        var list = new List<CloudFinding>();
        var cloud = provider is CloudProviders.Azure or CloudProviders.Aws;
        foreach (var i in items)
        {
            void Add(string rule, string severity, string? detail) => list.Add(new(i.ExternalId, rule, severity, RuleTitles[rule], detail));
            switch (i.Kind)
            {
                case "disk":
                    if (i.Bool("attached") == false) Add("disk_unattached", "medium", $"{i.DiskGb?.ToString("0") ?? "?"} GB disk is not attached to any machine but is still billed.");
                    if (i.Bool("encrypted") == false) Add("disk_unencrypted", "medium", "The disk is not encrypted at rest.");
                    break;
                case "public_ip":
                    if (i.Bool("associated") == false) Add("ip_idle", "low", $"{i.PublicIp ?? i.Name} is reserved but not attached to anything (billed while idle).");
                    break;
                case "vm":
                    var state = (i.State ?? "").ToLowerInvariant();
                    if (state is "stopped" or "deallocated" or "off" or "poweredoff" or "powered_off" or "saved")
                    {
                        var s = since(i);
                        if (s is { } from && (now - from).TotalDays >= OffDays)
                            Add("vm_off", "low", $"Switched off since {from:yyyy-MM-dd} ({(int)(now - from).TotalDays} days). Remove it if it is no longer needed.");
                    }
                    if (provider == CloudProviders.Azure && state == "stopped")
                        Add("vm_stopped_billed", "medium", "Stopped from inside the machine but not deallocated: compute is still billed. Stop (deallocate) it in Azure.");
                    if (i.Long("snapshotOldestDays") is { } days && days >= SnapshotDays)
                        Add("snapshot_old", "low", $"{i.Long("snapshotCount") ?? 1} checkpoint(s); the oldest is {days} days old. Old checkpoints grow and slow the machine down.");
                    break;
                case "security_group":
                    var ports = i.Ports("openPorts").ToList();
                    if (ports.Contains(0)) Add("exposed_all", "critical", "An inbound rule allows every port from any address (0.0.0.0/0 or *).");
                    else if (ports.Any(p => p is 22 or 3389))
                        Add("exposed_admin", "high", $"Inbound {string.Join(" and ", ports.Where(p => p is 22 or 3389).Distinct().Select(p => p == 22 ? "SSH (22)" : "RDP (3389)"))} allowed from any address. Use a VPN, bastion or just-in-time access.");
                    break;
                case "storage":
                    if (i.Bool("publicAccess") == true) Add("storage_public", "high", "Anonymous public read access is allowed.");
                    if (i.Bool("httpsOnly") == false) Add("storage_http", "medium", "Accepts plain HTTP; require secure transfer (HTTPS).");
                    if (i.Bool("encrypted") == false) Add("storage_unencrypted", "medium", "Server-side encryption is not enabled.");
                    break;
                case "database":
                    if (i.Bool("publiclyAccessible") == true) Add("db_public", "medium", "The database accepts connections from the internet.");
                    if (i.Bool("encrypted") == false) Add("db_unencrypted", "medium", "Storage encryption is not enabled.");
                    break;
                case "datastore":
                    if (i.Long("freePercent") is { } free)
                    {
                        if (free < 10) Add("datastore_full", "high", $"Only {free}% free.");
                        else if (free < 20) Add("datastore_full", "medium", $"Only {free}% free.");
                    }
                    break;
                case "host":
                    if (i.Long("memoryCommitPercent") is { } pct && pct > 150) Add("host_overcommit", "medium", $"Virtual machines are given {pct}% of the host's memory.");
                    if (i.Bool("osEndOfSupport") == true) Add("host_eos", "medium", $"{i.Os} no longer receives security updates.");
                    break;
            }
            if (cloud && i.Kind is "vm" or "disk" or "storage" or "database" && i.Tags.Count == 0)
                Add("untagged", "info", "Tag it (for example department, owner, environment) so its cost and owner are known.");
        }
        return list;
    }

    /// <summary>Windows Server / Windows versions out of support, from an OS caption.</summary>
    public static bool OsOutOfSupport(string? os) =>
        os is not null && System.Text.RegularExpressions.Regex.IsMatch(os, @"(windows (xp|vista|7|8(\.1)?|10)\b|server 20(03|08|12))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static int SeverityRank(string s) => s switch { "critical" => 4, "high" => 3, "medium" => 2, "low" => 1, _ => 0 };
}
