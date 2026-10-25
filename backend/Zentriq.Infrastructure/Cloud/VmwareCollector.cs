using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>
/// VMware vCenter 7.0 or later (vSphere Automation REST API), read-only: clusters, ESXi hosts, datastores (capacity and
/// free space) and virtual machines with their host, BIOS UUID, network adapters (MAC), disks and — when VMware Tools
/// run — guest host name, IP address and operating system. A user with the Read-only role is enough. Older 6.5/6.7
/// servers answer on /rest and are read the same way.
/// </summary>
public sealed class VmwareCollector(HttpClient? http = null) : ICloudCollector
{
    private HttpClient? _http = http;
    private string _base = "", _prefix = "/api";
    private bool _legacy;

    public async Task<CloudSnapshot> CollectAsync(CloudSettings s, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.Username) || string.IsNullOrEmpty(secret))
            throw new CloudException("Enter the vCenter host name, the user (for example zentriq@vsphere.local) and its password.");
        _http ??= CloudHttp.Create(s.SkipCertificateCheck, TimeSpan.FromSeconds(60));
        _base = $"https://{s.Host.Trim()}{(s.Port is > 0 and not 443 ? $":{s.Port}" : "")}";
        var snap = new CloudSnapshot();
        var session = await LoginAsync(s.Username.Trim(), secret, ct);
        try
        {
            snap.Identity = $"{s.Username} on {s.Host}{(_legacy ? " (vSphere 6.x API)" : "")}";
            var clusters = await ListAsync("/vcenter/cluster", session, ct);
            var hosts = await ListAsync("/vcenter/host", session, ct);
            var datastores = await ListAsync("/vcenter/datastore", session, ct);
            var vms = await ListAsync("/vcenter/vm", session, ct);

            var hostCluster = new Dictionary<string, string>();
            foreach (var c in clusters)
                foreach (var h in await ListAsync($"/vcenter/host?{Filter("clusters")}={Str(c, "cluster")}", session, ct))
                    if (Str(h, "host") is { } hid) hostCluster[hid] = Str(c, "cluster")!;
            var vmHost = new Dictionary<string, string>();
            foreach (var h in hosts.Where(h => Str(h, "connection_state") == "CONNECTED"))
                foreach (var v in await ListAsync($"/vcenter/vm?{Filter("hosts")}={Str(h, "host")}", session, ct))
                    if (Str(v, "vm") is { } vid) vmHost[vid] = Str(h, "host")!;

            foreach (var c in clusters)
            {
                var item = new CloudItem { Kind = "cluster", ExternalId = Str(c, "cluster")!, Name = Str(c, "name") };
                item.Properties["ha"] = Bool(c, "ha_enabled");
                item.Properties["drs"] = Bool(c, "drs_enabled");
                item.Properties["hosts"] = hostCluster.Values.Count(x => x == item.ExternalId);
                snap.Items.Add(item);
            }
            foreach (var h in hosts)
            {
                var item = new CloudItem { Kind = "host", ExternalId = Str(h, "host")!, Name = Str(h, "name"), Hostname = Str(h, "name"), Os = "VMware ESXi" };
                item.State = Str(h, "connection_state") == "CONNECTED" ? (Str(h, "power_state") ?? "POWERED_ON").ToLowerInvariant().Replace("powered_", "powered ") : Str(h, "connection_state")?.ToLowerInvariant();
                item.ParentId = hostCluster.GetValueOrDefault(item.ExternalId);
                item.Group = item.ParentId is { } cid ? clusters.Where(c => Str(c, "cluster") == cid).Select(c => Str(c, "name")).FirstOrDefault() : null;
                item.Properties["vms"] = vmHost.Values.Count(x => x == item.ExternalId);
                snap.Items.Add(item);
            }
            foreach (var d in datastores) snap.Items.Add(MapDatastore(d));

            using var gate = new SemaphoreSlim(6);
            var details = await Task.WhenAll(vms.Select(async v =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var id = Str(v, "vm")!;
                    JsonElement? detail = null, guest = null;
                    try { detail = await GetAsync($"/vcenter/vm/{id}", session, ct); }
                    catch (CloudException ex) { lock (snap) snap.Warn($"Virtual machine {Str(v, "name")}: details not read — {ex.Message}", "vm"); }
                    if (Str(v, "power_state") == "POWERED_ON")
                    {
                        try { guest = await GetAsync($"/vcenter/vm/{id}/guest/identity", session, ct); }
                        catch (CloudException) { /* VMware Tools not running */ }
                    }
                    return (v, detail, guest);
                }
                finally { gate.Release(); }
            }));
            foreach (var (v, detail, guest) in details)
            {
                var item = MapVm(v, detail, guest);
                item.ParentId = vmHost.GetValueOrDefault(item.ExternalId);
                item.Group = item.ParentId is { } hid && hostCluster.TryGetValue(hid, out var cid)
                    ? clusters.Where(c => Str(c, "cluster") == cid).Select(c => Str(c, "name")).FirstOrDefault() : null;
                snap.Items.Add(item);
            }
            if (vms.Count >= 4000) snap.Warn("vCenter lists at most 4000 virtual machines per request; machines beyond that were not read.", "vm");
        }
        finally
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, _base + (_legacy ? "/rest/com/vmware/cis/session" : "/api/session"));
                req.Headers.Add("vmware-api-session-id", session);
                using var _ = await _http.SendAsync(req, CancellationToken.None);
            }
            catch (Exception) { /* the session expires by itself */ }
        }
        return snap;
    }

    private string Filter(string name) => _legacy ? $"filter.{name}" : name;

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(), _ => null,
        } : null;
    private static bool? Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static long? Long(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    /// <summary>Entries of a map that /api returns as an object ({"4000": {...}}) and /rest as a list ([{"key":"4000","value":{...}}]).</summary>
    internal static IEnumerable<JsonElement> MapValues(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Select(p => p.Value),
        JsonValueKind.Array => e.EnumerateArray().Select(x => x.TryGetProperty("value", out var v) ? v : x),
        _ => [],
    };

    internal static CloudItem MapDatastore(JsonElement d)
    {
        var item = new CloudItem { Kind = "datastore", ExternalId = Str(d, "datastore")!, Name = Str(d, "name"), Size = Str(d, "type") };
        var cap = Long(d, "capacity");
        var free = Long(d, "free_space");
        if (cap is > 0)
        {
            item.DiskGb = Math.Round(cap.Value / 1073741824m, 1);
            if (free is { } f)
            {
                item.Properties["freeGb"] = Math.Round(f / 1073741824m, 1);
                item.Properties["freePercent"] = (long)Math.Round(f * 100m / cap.Value);
            }
        }
        item.State = "accessible";
        return item;
    }

    internal static CloudItem MapVm(JsonElement summary, JsonElement? detail, JsonElement? guest)
    {
        var item = new CloudItem
        {
            Kind = "vm", ExternalId = Str(summary, "vm")!, Name = Str(summary, "name"),
            State = (Str(summary, "power_state") ?? "").ToLowerInvariant() switch { "powered_on" => "running", "powered_off" => "off", "suspended" => "suspended", var x => x },
        };
        if (int.TryParse(Str(summary, "cpu_count"), out var cpu)) item.Cpu = cpu;
        if (long.TryParse(Str(summary, "memory_size_MiB"), out var mem)) item.MemoryMb = mem;
        if (detail is { } d)
        {
            if (d.TryGetProperty("identity", out var idn)) item.BiosUuid = Str(idn, "bios_uuid")?.ToUpperInvariant();
            item.Os = Str(d, "guest_OS")?.Replace('_', ' ');
            if (d.TryGetProperty("nics", out var nics))
                foreach (var n in MapValues(nics)) if (Str(n, "mac_address") is { Length: > 0 } mac) item.Macs.Add(AzureCollector.NormalizeMac(mac));
            if (d.TryGetProperty("disks", out var disks))
                item.DiskGb = Math.Round(MapValues(disks).Sum(x => Long(x, "capacity") ?? 0) / 1073741824m, 1);
            if (d.TryGetProperty("hardware", out var hw)) item.Properties["hardwareVersion"] = Str(hw, "version");
            if (d.TryGetProperty("cpu", out var c) && int.TryParse(Str(c, "count"), out var cc)) item.Cpu = cc;
            if (d.TryGetProperty("memory", out var m) && long.TryParse(Str(m, "size_MiB"), out var mm)) item.MemoryMb = mm;
        }
        if (guest is { } g)
        {
            item.Hostname = Str(g, "host_name")?.Split('.')[0];
            item.PrivateIp = Str(g, "ip_address");
            if (g.TryGetProperty("full_name", out var fn) && Str(fn, "default_message") is { Length: > 0 } os) item.Os = os;
            item.Properties["fqdn"] = Str(g, "host_name");
        }
        item.Properties["toolsRunning"] = guest is not null;
        return item;
    }

    private async Task<string> LoginAsync(string user, string password, CancellationToken ct)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        foreach (var (legacy, path) in new[] { (false, "/api/session"), (true, "/rest/com/vmware/cis/session") })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _base + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            HttpResponseMessage resp;
            try { resp = await _http!.SendAsync(req, ct); }
            catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
            {
                throw new CloudException($"The certificate of {new Uri(_base).Host} is not trusted. Install the vCenter root certificate on the Zentriq server, or tick 'Skip the certificate check'.", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new CloudException(CloudHttp.Unreachable(new Uri(_base).Host, ex), ex);
            }
            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.NotFound && !legacy) continue;
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new CloudException("vCenter rejected the user name or password.");
                if (!resp.IsSuccessStatusCode) throw new CloudException($"vCenter sign-in failed (HTTP {(int)resp.StatusCode}). Is {new Uri(_base).Host} a vCenter Server 6.5 or later?");
                using var doc = JsonDocument.Parse(text);
                _legacy = legacy;
                _prefix = legacy ? "/rest" : "/api";
                return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString()! : doc.RootElement.GetProperty("value").GetString()!;
            }
        }
        throw new CloudException($"{new Uri(_base).Host} does not answer the vSphere REST API. Zentriq needs vCenter Server 6.5 or later (ESXi hosts on their own are not supported; add them to vCenter, or use the Hyper-V or agent route).");
    }

    private async Task<List<JsonElement>> ListAsync(string path, string session, CancellationToken ct)
    {
        var e = await GetAsync(path, session, ct);
        return e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList() : [];
    }

    private async Task<JsonElement> GetAsync(string path, string session, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _base + _prefix + path);
        req.Headers.Add("vmware-api-session-id", session);
        HttpResponseMessage resp;
        try { resp = await _http!.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { throw new CloudException(CloudHttp.Unreachable(new Uri(_base).Host, ex), ex); }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new CloudException(resp.StatusCode == HttpStatusCode.Forbidden ? "the user may not read this (give it the Read-only role on the vCenter, propagated)" : $"HTTP {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (_legacy && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var v)) return v.Clone();
            return root.Clone();
        }
    }
}
