using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using static Zentriq.Infrastructure.Cloud.CloudJson;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>
/// Nutanix AHV through Prism Central (or Prism Element) v3 REST API on port 9440, read-only: clusters, hosts (model,
/// serial, hypervisor, CPU, memory) and virtual machines (power state, vCPU, memory, disks, MAC and IP addresses,
/// categories as tags). A Prism user with the Viewer role is enough. The AHV VM UUID is the machine's SMBIOS UUID,
/// so VMs link to their inventoried devices.
/// </summary>
public sealed class NutanixCollector(HttpClient? http = null) : ICloudCollector
{
    private HttpClient? _http = http;
    private string _base = "";
    private string _auth = "";

    public async Task<CloudSnapshot> CollectAsync(CloudSettings s, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.Username) || string.IsNullOrEmpty(secret))
            throw new CloudException("Enter the Prism Central (or Prism Element) address, the user and its password.");
        _http ??= CloudHttp.Create(s.SkipCertificateCheck, TimeSpan.FromSeconds(90));
        _base = $"https://{s.Host.Trim()}:{(s.Port is > 0 ? s.Port : 9440)}/api/nutanix/v3";
        _auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.Username.Trim()}:{secret}"));
        var snap = new CloudSnapshot();
        List<JsonElement> clusters, hosts, vms;
        try
        {
            clusters = await ListAsync("clusters", "cluster", ct);
        }
        catch (CloudHttpException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            throw new CloudException("Prism rejected the user name or password.");
        }
        catch (CloudHttpException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            throw new CloudException($"{s.Host} does not answer the Nutanix v3 API (Prism Central, or Prism Element AOS 5.x or later, on port 9440).");
        }
        hosts = await ListAsync("hosts", "host", ct);
        vms = await ListAsync("vms", "vm", ct);
        snap.Identity = $"{s.Username} on {s.Host} · {clusters.Count(c => !IsPrismCentral(c))} cluster(s)";
        Map(clusters, hosts, vms, snap);
        return snap;
    }

    private static bool IsPrismCentral(JsonElement c) => Arr(c, "status", "resources", "config", "service_list").Any(x => x.GetString() == "PRISM_CENTRAL");

    /// <summary>v3 list answers to resources (pure; unit tested).</summary>
    internal static void Map(IReadOnlyList<JsonElement> clusters, IReadOnlyList<JsonElement> hosts, IReadOnlyList<JsonElement> vms, CloudSnapshot snap)
    {
        var clusterName = new Dictionary<string, string>();
        foreach (var c in clusters.Where(c => !IsPrismCentral(c)))
        {
            var id = Str(c, "metadata", "uuid")!;
            clusterName[id] = Str(c, "status", "name") ?? id;
            var item = new CloudItem { Kind = "cluster", ExternalId = id, Name = clusterName[id], State = Str(c, "status", "state")?.ToLowerInvariant() };
            item.Properties["hosts"] = Arr(c, "status", "resources", "nodes", "hypervisor_server_list").Count();
            item.Properties["aos"] = Str(c, "status", "resources", "config", "build", "version");
            item.Properties["redundancyFactor"] = Long(c, "status", "resources", "config", "redundancy_factor");
            snap.Items.Add(item);
        }
        var vmCount = vms.GroupBy(v => Str(v, "status", "resources", "host_reference", "uuid") ?? "").ToDictionary(g => g.Key, g => g.Count());
        var memByHost = vms.Where(v => Str(v, "status", "resources", "power_state") == "ON")
            .GroupBy(v => Str(v, "status", "resources", "host_reference", "uuid") ?? "").ToDictionary(g => g.Key, g => g.Sum(v => Long(v, "status", "resources", "memory_size_mib") ?? 0));
        foreach (var h in hosts)
        {
            var r = Get(h, "status", "resources");
            if (Get(r, "hypervisor").ValueKind == JsonValueKind.Undefined && Str(h, "status", "name") is null) continue;   // Prism Central VM entry
            var id = Str(h, "metadata", "uuid")!;
            var cluster = Str(h, "status", "cluster_reference", "uuid");
            var item = new CloudItem
            {
                Kind = "host", ExternalId = id, Name = Str(h, "status", "name"), Hostname = Str(h, "status", "name"), State = Str(h, "status", "state")?.ToLowerInvariant() ?? "running",
                Os = Str(r, "hypervisor", "hypervisor_full_name"), ParentId = cluster, Group = cluster is null ? null : clusterName.GetValueOrDefault(cluster),
                Cpu = (int?)Long(r, "num_cpu_cores"), MemoryMb = Long(r, "memory_capacity_mib"),
                PrivateIp = Str(r, "hypervisor", "ip"),
            };
            item.Properties["model"] = Str(r, "block", "block_model");
            item.Properties["serial"] = Str(r, "serial_number");
            item.Properties["cvmIp"] = Str(r, "controller_vm", "ip");
            item.Properties["vms"] = vmCount.GetValueOrDefault(id);
            if (item.MemoryMb is > 0) item.Properties["memoryCommitPercent"] = memByHost.GetValueOrDefault(id) * 100 / item.MemoryMb.Value;
            snap.Items.Add(item);
        }
        foreach (var v in vms)
        {
            var r = Get(v, "status", "resources");
            var id = Str(v, "metadata", "uuid")!;
            var cluster = Str(v, "status", "cluster_reference", "uuid");
            var item = new CloudItem
            {
                Kind = "vm", ExternalId = id, Name = Str(v, "status", "name") ?? Str(v, "spec", "name"),
                State = Str(r, "power_state") switch { "ON" => "running", "OFF" => "off", var x => x?.ToLowerInvariant() },
                Cpu = (int?)((Long(r, "num_sockets") ?? 1) * (Long(r, "num_vcpus_per_socket") ?? 1)), MemoryMb = Long(r, "memory_size_mib"),
                ParentId = Str(r, "host_reference", "uuid"), Group = cluster is null ? null : clusterName.GetValueOrDefault(cluster) ?? Str(v, "status", "cluster_reference", "name"),
                BiosUuid = id.ToUpperInvariant(), CreatedAt = Date(Str(v, "metadata", "creation_time")), Os = Str(r, "guest_tools", "nutanix_guest_tools", "guest_os_version"),
            };
            foreach (var n in Arr(r, "nic_list"))
            {
                if (Str(n, "mac_address") is { } mac) item.Macs.Add(AzureCollector.NormalizeMac(mac));
                item.PrivateIp ??= Arr(n, "ip_endpoint_list").Select(e => Str(e, "ip")).FirstOrDefault(ip => ip is not null);
            }
            var disks = Arr(r, "disk_list").Where(d => Str(d, "device_properties", "device_type") is null or "DISK").ToList();
            if (disks.Count > 0) item.DiskGb = Gb(disks.Sum(d => Long(d, "disk_size_bytes") ?? (Long(d, "disk_size_mib") ?? 0) * 1048576));
            var cats = Get(v, "metadata", "categories");
            if (cats.ValueKind == JsonValueKind.Object) foreach (var c in cats.EnumerateObject()) item.Tags[c.Name] = c.Value.ToString();
            item.Properties["machineType"] = Str(r, "machine_type");
            item.Properties["guestToolsInstalled"] = Get(r, "guest_tools").ValueKind == JsonValueKind.Object;
            snap.Items.Add(item);
        }
    }

    private async Task<List<JsonElement>> ListAsync(string what, string kind, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        for (var offset = 0; ; )
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/{what}/list")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { kind, length = 500, offset }), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _auth);
            var page = await SendAsync(_http!, req, $"Nutanix {what}", ct);
            var entities = Arr(page, "entities").ToList();
            all.AddRange(entities);
            var total = Long(page, "metadata", "total_matches") ?? all.Count;
            offset += entities.Count;
            if (entities.Count == 0 || offset >= total || offset > 100_000) break;
        }
        return all;
    }
}
