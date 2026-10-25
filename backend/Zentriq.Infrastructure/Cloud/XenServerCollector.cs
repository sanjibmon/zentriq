using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Zentriq.Infrastructure.Cloud.CloudJson;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>
/// XCP-ng and Citrix Hypervisor (XenServer) through the XAPI JSON-RPC interface of the pool master, read-only: the pool,
/// hosts, storage repositories (capacity and free space), virtual machines with power state, vCPU, memory, MAC
/// addresses, snapshots and — with the guest tools — IP addresses and operating system. A pool user with the Read Only
/// role (or root) is enough. A Xen VM's UUID is its SMBIOS UUID, so VMs link to their inventoried devices.
/// </summary>
public sealed class XenServerCollector(HttpClient? http = null) : ICloudCollector
{
    private HttpClient? _http = http;
    private string _url = "";
    private int _id;

    public async Task<CloudSnapshot> CollectAsync(CloudSettings s, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.Username) || string.IsNullOrEmpty(secret))
            throw new CloudException("Enter the pool master's address, the user (for example root) and its password.");
        _http ??= CloudHttp.Create(s.SkipCertificateCheck, TimeSpan.FromSeconds(90));
        _url = $"https://{s.Host.Trim()}{(s.Port is > 0 and not 443 ? $":{s.Port}" : "")}/jsonrpc";
        string session;
        try { session = (await CallAsync("session.login_with_password", [s.Username.Trim(), secret, "1.0", "Zentriq"], ct)).GetString()!; }
        catch (XapiException ex) when (ex.Code == "HOST_IS_SLAVE")
        {
            throw new CloudException($"{s.Host} is a pool member, not the pool master. Use the master's address{(ex.Args.Length > 0 ? $" ({ex.Args[0]})" : "")}.");
        }
        catch (XapiException ex) when (ex.Code == "SESSION_AUTHENTICATION_FAILED")
        {
            throw new CloudException("The pool rejected the user name or password.");
        }
        try
        {
            var data = new Dictionary<string, JsonElement>();
            foreach (var cls in new[] { "pool", "host", "host_metrics", "VM", "VIF", "VM_guest_metrics", "SR" })
            {
                try { data[cls] = await CallAsync($"{cls}.get_all_records", [session], ct); }
                catch (XapiException) when (cls is "VM_guest_metrics" or "host_metrics" or "VIF") { data[cls] = default; }
            }
            var snap = new CloudSnapshot();
            var pool = data["pool"].ValueKind == JsonValueKind.Object ? data["pool"].EnumerateObject().Select(p => Str(p.Value, "name_label")).FirstOrDefault() : null;
            snap.Identity = $"{s.Username} on {s.Host}{(string.IsNullOrEmpty(pool) ? "" : $" · pool {pool}")}";
            Map(data, snap, DateTimeOffset.UtcNow);
            return snap;
        }
        finally
        {
            try { await CallAsync("session.logout", [session], CancellationToken.None); } catch (Exception) { /* expires by itself */ }
        }
    }

    private static IEnumerable<(string Ref, JsonElement Rec)> Records(IReadOnlyDictionary<string, JsonElement> d, string cls) =>
        d.TryGetValue(cls, out var e) && e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().Select(p => (p.Name, p.Value)) : [];

    /// <summary>"20250301T10:22:13Z" (XAPI date) to a date.</summary>
    internal static DateTimeOffset? XapiDate(string? s) =>
        s is null ? null : DateTimeOffset.TryParseExact(s.Replace("-", "").Replace(":", ""), ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss"],
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1970 ? d : null;

    /// <summary>get_all_records answers to resources (pure; unit tested).</summary>
    internal static void Map(IReadOnlyDictionary<string, JsonElement> d, CloudSnapshot snap, DateTimeOffset now)
    {
        var pool = Records(d, "pool").Select(p => p.Rec).FirstOrDefault();
        var poolName = Str(pool, "name_label");
        var hostByRef = Records(d, "host").ToDictionary(h => h.Ref, h => h.Rec);
        var metrics = Records(d, "host_metrics").ToDictionary(m => m.Ref, m => m.Rec);
        var guests = Records(d, "VM_guest_metrics").ToDictionary(m => m.Ref, m => m.Rec);
        var vifs = Records(d, "VIF").ToLookup(v => Str(v.Rec, "VM") ?? "", v => v.Rec);
        var allVms = Records(d, "VM").ToList();
        var byRef = allVms.ToDictionary(v => v.Ref, v => v.Rec);
        var realVms = allVms.Where(v => Bool(v.Rec, "is_a_template") != true && Bool(v.Rec, "is_a_snapshot") != true && Bool(v.Rec, "is_control_domain") != true).ToList();

        if (pool.ValueKind == JsonValueKind.Object)
        {
            var item = new CloudItem { Kind = "cluster", ExternalId = "pool/" + (Str(pool, "uuid") ?? poolName), Name = string.IsNullOrEmpty(poolName) ? "Pool" : poolName };
            item.Properties["hosts"] = hostByRef.Count;
            item.Properties["ha"] = Bool(pool, "ha_enabled");
            snap.Items.Add(item);
        }
        foreach (var (hr, h) in hostByRef)
        {
            var sv = Get(h, "software_version");
            var item = new CloudItem
            {
                Kind = "host", ExternalId = Str(h, "uuid")!, Name = Str(h, "name_label"), Hostname = Str(h, "hostname"), PrivateIp = Str(h, "address"),
                State = Bool(h, "enabled") == false ? "disabled" : "running", Group = poolName,
                Os = $"{Str(sv, "product_brand") ?? "XenServer"} {Str(sv, "product_version") ?? Str(sv, "platform_version")}".Trim(),
                Cpu = (int?)Long(h, "cpu_info", "cpu_count"),
                MemoryMb = Str(h, "metrics") is { } mr && metrics.TryGetValue(mr, out var m) && Long(m, "memory_total") is { } mt ? mt / 1048576 : null,
                BiosUuid = null,
            };
            item.Properties["model"] = $"{Str(h, "bios_strings", "system-manufacturer")} {Str(h, "bios_strings", "system-product-name")}".Trim();
            item.Properties["serial"] = Str(h, "bios_strings", "system-serial-number");
            var onHost = realVms.Where(v => Str(v.Rec, "resident_on") == hr).ToList();
            item.Properties["vms"] = onHost.Count;
            if (item.MemoryMb is > 0)
                item.Properties["memoryCommitPercent"] = onHost.Where(v => Str(v.Rec, "power_state") == "Running").Sum(v => Long(v.Rec, "memory_static_max") ?? 0) / 1048576 * 100 / item.MemoryMb.Value;
            snap.Items.Add(item);
        }
        foreach (var (sr, r) in Records(d, "SR"))
        {
            if (Str(r, "content_type") is "iso" || Str(r, "type") is "udev" or "iso") continue;
            var size = Long(r, "physical_size");
            var used = Long(r, "physical_utilisation");
            var item = new CloudItem { Kind = "datastore", ExternalId = Str(r, "uuid") ?? sr, Name = Str(r, "name_label"), Size = Str(r, "type"), Group = poolName, State = "attached" };
            if (size is > 0)
            {
                item.DiskGb = Gb(size.Value);
                item.Properties["freeGb"] = Gb(size.Value - (used ?? 0));
                item.Properties["freePercent"] = (long)Math.Round((size.Value - (used ?? 0)) * 100m / size.Value);
            }
            item.Properties["shared"] = Bool(r, "shared");
            snap.Items.Add(item);
        }
        foreach (var (vr, v) in realVms)
        {
            var host = Str(v, "resident_on") is { } hr && hostByRef.TryGetValue(hr, out var h) ? h : default;
            var guest = Str(v, "guest_metrics") is { } gr && guests.TryGetValue(gr, out var g) ? g : default;
            var state = Str(v, "power_state")?.ToLowerInvariant();
            var item = new CloudItem
            {
                Kind = "vm", ExternalId = Str(v, "uuid")!, Name = Str(v, "name_label"), State = state, ParentId = Str(host, "uuid"), Group = poolName,
                Cpu = (int?)Long(v, "VCPUs_max"), MemoryMb = Long(v, "memory_static_max") is { } mem ? mem / 1048576 : null, BiosUuid = Str(v, "uuid")?.ToUpperInvariant(),
                Os = Str(guest, "os_version", "name")?.Split('|')[0], Hostname = Str(guest, "os_version", "hostname") ?? Str(guest, "other", "hostname"),
                PrivateIp = Get(guest, "networks").ValueKind == JsonValueKind.Object
                    ? Get(guest, "networks").EnumerateObject().Where(n => n.Name.EndsWith("/ip", StringComparison.Ordinal) || n.Name.Contains("/ipv4/")).Select(n => n.Value.GetString()).FirstOrDefault()
                    : null,
            };
            foreach (var vif in vifs[vr]) if (Str(vif, "MAC") is { } mac) item.Macs.Add(AzureCollector.NormalizeMac(mac));
            foreach (var t in Arr(v, "tags")) if (t.GetString() is { Length: > 0 } tag) item.Tags[tag] = "";
            var snaps = Arr(v, "snapshots").Select(x => x.GetString()).OfType<string>().Where(byRef.ContainsKey).Select(x => byRef[x]).ToList();
            if (snaps.Count > 0)
            {
                item.Properties["snapshotCount"] = (long)snaps.Count;
                if (snaps.Select(x => XapiDate(Str(x, "snapshot_time"))).Where(x => x is not null).Min() is { } oldest) item.Properties["snapshotOldestDays"] = (long)(now - oldest).TotalDays;
            }
            item.Properties["toolsRunning"] = guest.ValueKind == JsonValueKind.Object;
            item.Properties["description"] = Str(v, "name_description");
            snap.Items.Add(item);
        }
    }

    private sealed class XapiException(string code, string[] args) : CloudException($"XAPI error {code}{(args.Length > 0 ? ": " + string.Join(", ", args) : "")}")
    {
        public string Code { get; } = code;
        public string[] Args { get; } = args;
    }

    private async Task<JsonElement> CallAsync(string method, object[] args, CancellationToken ct)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = JsonSerializer.SerializeToNode(args), ["id"] = ++_id };
        using var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        var r = await SendAsync(_http!, req, "XAPI " + method, ct);
        if (Get(r, "error").ValueKind == JsonValueKind.Object)
        {
            var e = Get(r, "error");
            var data = Get(e, "data");
            var parts = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Select(x => x.ToString()).ToArray() : [];
            // XAPI: {"error":{"code":1,"message":"SESSION_AUTHENTICATION_FAILED","data":[…]}} or the error name as data[0]
            var msg = Str(e, "message");
            if (msg is not null && System.Text.RegularExpressions.Regex.IsMatch(msg, "^[A-Z][A-Z0-9_]+$")) throw new XapiException(msg, parts);
            throw new XapiException(parts.FirstOrDefault() ?? msg ?? "unknown", parts.Skip(1).ToArray());
        }
        return Get(r, "result");
    }
}
