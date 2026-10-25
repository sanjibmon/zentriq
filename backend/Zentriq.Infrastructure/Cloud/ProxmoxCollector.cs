using System.Text.Json;
using static Zentriq.Infrastructure.Cloud.CloudJson;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>
/// Proxmox VE (one node or a whole cluster) through its REST API on port 8006, read-only, with an API token
/// (user@realm!tokenid and the token secret; the PVEAuditor role is enough): nodes, storage (capacity and free space),
/// QEMU virtual machines and LXC containers with state, vCPU, memory, disks, MAC addresses, SMBIOS UUID, tags, snapshots
/// and — with the QEMU guest agent — IP addresses and the guest operating system.
/// </summary>
public sealed class ProxmoxCollector(HttpClient? http = null) : ICloudCollector
{
    private HttpClient? _http = http;
    private string _base = "";
    private string _auth = "";

    public async Task<CloudSnapshot> CollectAsync(CloudSettings s, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.Username) || string.IsNullOrEmpty(secret))
            throw new CloudException("Enter the Proxmox host, the API token ID (for example zentriq@pve!inventory) and the token secret.");
        if (!s.Username.Contains('!')) throw new CloudException("The token ID has the form user@realm!tokenname, for example zentriq@pve!inventory (Datacenter › Permissions › API Tokens).");
        _http ??= CloudHttp.Create(s.SkipCertificateCheck, TimeSpan.FromSeconds(60));
        _base = $"https://{s.Host.Trim()}:{(s.Port is > 0 ? s.Port : 8006)}/api2/json";
        _auth = $"PVEAPIToken={s.Username.Trim()}={secret.Trim()}";
        var snap = new CloudSnapshot();

        JsonElement resources;
        try { resources = await GetAsync("/cluster/resources", ct); }
        catch (CloudHttpException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new CloudException("Proxmox rejected the API token. Check the token ID and secret, and that the token is not expired.");
        }
        var rows = Arr(resources, "data").ToList();
        if (rows.Count == 0) snap.Warn("The token sees nothing. Give it the PVEAuditor role on / (Datacenter › Permissions › Add › API Token Permission), and untick 'Privilege Separation' or grant the role to the token itself.");
        var details = new Dictionary<string, (JsonElement Config, JsonElement? Snapshots, JsonElement? Agent, JsonElement? AgentOs)>();
        foreach (var g in rows.Where(r => Str(r, "type") is "qemu" or "lxc" && Long(r, "template") != 1))
        {
            var type = Str(g, "type")!;
            var node = Str(g, "node");
            var vmid = Long(g, "vmid");
            var path = $"/nodes/{node}/{type}/{vmid}";
            JsonElement config = default;
            JsonElement? snaps = null, agent = null, agentOs = null;
            try { config = await GetAsync(path + "/config", ct); }
            catch (CloudException ex) { snap.Warn($"{Str(g, "name")} ({vmid}): configuration not read — {ex.Message}", "vm"); }
            try { snaps = await GetAsync(path + "/snapshot", ct); } catch (CloudException) { /* not allowed */ }
            if (type == "qemu" && Str(g, "status") == "running" && Str(Get(config, "data"), "agent") is { } ag && (ag.StartsWith('1') || ag.Contains("enabled=1")))
            {
                try { agent = await GetAsync(path + "/agent/network-get-interfaces", ct); } catch (CloudException) { /* guest agent not running */ }
                try { agentOs = await GetAsync(path + "/agent/get-osinfo", ct); } catch (CloudException) { /* older agent */ }
            }
            details[$"{type}/{vmid}"] = (config, snaps, agent, agentOs);
        }
        var nodes = rows.Count(r => Str(r, "type") == "node");
        snap.Identity = $"{s.Username} on {s.Host} · {nodes} node(s)";
        Map(rows, details, snap, DateTimeOffset.UtcNow);
        return snap;
    }

    /// <summary>/cluster/resources rows plus per-guest config, snapshots and guest agent answers (pure; unit tested).</summary>
    internal static void Map(IReadOnlyList<JsonElement> rows, IReadOnlyDictionary<string, (JsonElement Config, JsonElement? Snapshots, JsonElement? Agent, JsonElement? AgentOs)> details,
        CloudSnapshot snap, DateTimeOffset now)
    {
        var running = rows.Where(r => Str(r, "type") is "qemu" or "lxc" && Str(r, "status") == "running")
            .GroupBy(r => Str(r, "node") ?? "").ToDictionary(g => g.Key, g => g.Sum(r => Long(r, "maxmem") ?? 0));
        var count = rows.Where(r => Str(r, "type") is "qemu" or "lxc").GroupBy(r => Str(r, "node") ?? "").ToDictionary(g => g.Key, g => g.Count());
        foreach (var r in rows)
        {
            switch (Str(r, "type"))
            {
                case "node":
                {
                    var node = Str(r, "node")!;
                    var item = new CloudItem
                    {
                        Kind = "host", ExternalId = "node/" + node, Name = node, Hostname = node, Os = "Proxmox VE",
                        State = Str(r, "status") == "online" ? "running" : Str(r, "status"), Cpu = (int?)Long(r, "maxcpu"),
                        MemoryMb = Long(r, "maxmem") is { } m ? m / 1048576 : null,
                    };
                    item.Properties["vms"] = count.GetValueOrDefault(node);
                    if (item.MemoryMb is > 0) item.Properties["memoryCommitPercent"] = running.GetValueOrDefault(node) / 1048576 * 100 / item.MemoryMb.Value;
                    if (Long(r, "uptime") is { } up) item.Properties["uptimeHours"] = up / 3600;
                    snap.Items.Add(item);
                    break;
                }
                case "storage":
                {
                    var max = Long(r, "maxdisk");
                    var used = Long(r, "disk");
                    var item = new CloudItem
                    {
                        Kind = "datastore", ExternalId = Str(r, "id") ?? $"storage/{Str(r, "node")}/{Str(r, "storage")}", Name = $"{Str(r, "storage")} ({Str(r, "node")})",
                        Size = Str(r, "plugintype"), ParentId = "node/" + Str(r, "node"), Group = Str(r, "node"), State = Str(r, "status"),
                    };
                    if (max is > 0)
                    {
                        item.DiskGb = Gb(max.Value);
                        item.Properties["freeGb"] = Gb(max.Value - (used ?? 0));
                        item.Properties["freePercent"] = (long)Math.Round((max.Value - (used ?? 0)) * 100m / max.Value);
                    }
                    item.Properties["shared"] = Long(r, "shared") == 1;
                    item.Properties["content"] = Str(r, "content");
                    snap.Items.Add(item);
                    break;
                }
                case "qemu" or "lxc" when Long(r, "template") != 1:
                {
                    var type = Str(r, "type")!;
                    var vmid = Long(r, "vmid");
                    var node = Str(r, "node");
                    details.TryGetValue($"{type}/{vmid}", out var d);
                    var cfg = Get(d.Config, "data");
                    var item = new CloudItem
                    {
                        Kind = "vm", ExternalId = $"{type}/{vmid}", Name = Str(r, "name") ?? $"{type} {vmid}", State = Str(r, "status"), ParentId = "node/" + node, Group = node,
                        Cpu = (int?)Long(r, "maxcpu"), MemoryMb = Long(r, "maxmem") is { } m ? m / 1048576 : null, DiskGb = Long(r, "maxdisk") is > 0 and var md ? Gb(md) : null,
                        Hostname = Str(cfg, "hostname") ?? Str(r, "name"),
                        Os = type == "lxc" ? $"Linux container ({Str(cfg, "ostype") ?? "linux"})" : OsType(Str(cfg, "ostype")),
                    };
                    foreach (var tag in (Str(r, "tags") ?? "").Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries)) item.Tags[tag] = "";
                    // net0: "virtio=BC:24:11:AA:BB:CC,bridge=vmbr0" (VM) or "name=eth0,bridge=vmbr0,hwaddr=BC:24:…,ip=10.0.0.5/24" (container)
                    if (cfg.ValueKind == JsonValueKind.Object)
                        foreach (var p in cfg.EnumerateObject().Where(p => p.Name.StartsWith("net", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.String))
                        {
                            foreach (var part in p.Value.GetString()!.Split(','))
                            {
                                var kv = part.Split('=', 2);
                                if (kv.Length != 2) continue;
                                if (System.Text.RegularExpressions.Regex.IsMatch(kv[1], "^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")) item.Macs.Add(AzureCollector.NormalizeMac(kv[1]));
                                else if (kv[0] == "ip" && kv[1] is not ("dhcp" or "manual")) item.PrivateIp ??= kv[1].Split('/')[0];
                            }
                        }
                    if (Str(cfg, "smbios1") is { } smbios && smbios.Split(',').Select(x => x.Split('=', 2)).FirstOrDefault(x => x[0] == "uuid") is { Length: 2 } u)
                        item.BiosUuid = u[1].ToUpperInvariant();
                    if (d.Agent is { } ag)
                        item.PrivateIp ??= Arr(ag, "data", "result").Where(i => Str(i, "name") is not ("lo" or "Loopback Pseudo-Interface 1"))
                            .SelectMany(i => Arr(i, "ip-addresses")).Where(a => Str(a, "ip-address-type") == "ipv4" && !(Str(a, "ip-address") ?? "").StartsWith("127.") && !(Str(a, "ip-address") ?? "").StartsWith("169.254."))
                            .Select(a => Str(a, "ip-address")).FirstOrDefault();
                    if (d.AgentOs is { } os && Str(os, "data", "result", "pretty-name") is { } pretty) item.Os = pretty;
                    if (d.Snapshots is { } sn)
                    {
                        var list = Arr(sn, "data").Where(x => Str(x, "name") != "current").ToList();
                        if (list.Count > 0)
                        {
                            item.Properties["snapshotCount"] = (long)list.Count;
                            if (list.Select(x => Epoch(Long(x, "snaptime"))).Where(x => x is not null).Min() is { } oldest) item.Properties["snapshotOldestDays"] = (long)(now - oldest).TotalDays;
                        }
                    }
                    item.Properties["vmid"] = vmid;
                    item.Properties["type"] = type == "lxc" ? "container" : "virtual machine";
                    if (Long(r, "uptime") is > 0 and var up) item.Properties["uptimeHours"] = up / 3600;
                    if (Str(r, "hastate") is { } ha) item.Properties["ha"] = ha;
                    snap.Items.Add(item);
                    break;
                }
            }
        }
    }

    private static string? OsType(string? t) => t switch
    {
        null => null, "win11" => "Windows 11 / Server 2022+", "win10" => "Windows 10 / Server 2016-2019", "win8" => "Windows 8 / Server 2012", "win7" => "Windows 7 / Server 2008 R2",
        "l26" => "Linux (kernel 2.6+)", "l24" => "Linux (kernel 2.4)", "solaris" => "Solaris", "other" => "Other", var x => x,
    };

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _base + path);
        req.Headers.TryAddWithoutValidation("Authorization", _auth);
        return await SendAsync(_http!, req, "Proxmox " + path.Split('/').Last(), ct);
    }
}
