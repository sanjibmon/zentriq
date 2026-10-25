using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using static Zentriq.Infrastructure.Cloud.CloudJson;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>
/// oVirt, Red Hat Virtualization and Oracle Linux Virtualization Manager through the engine's REST API (v4), read-only:
/// clusters, hosts (model, serial, OS, CPU, memory), storage domains (capacity and free space) and virtual machines with
/// status and since when, vCPU, memory, MAC addresses, guest IPs, FQDN and OS (guest agent), host and snapshots.
/// A user with a read-only role (for example ReadOnlyAdmin), e.g. zentriq@internal, is enough.
/// </summary>
public sealed class OvirtCollector(HttpClient? http = null) : ICloudCollector
{
    private HttpClient? _http = http;
    private string _base = "";
    private string _auth = "";

    public async Task<CloudSnapshot> CollectAsync(CloudSettings s, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.Username) || string.IsNullOrEmpty(secret))
            throw new CloudException("Enter the engine's address, the user (for example zentriq@internal) and its password.");
        _http ??= CloudHttp.Create(s.SkipCertificateCheck, TimeSpan.FromSeconds(120));
        _base = $"https://{s.Host.Trim()}{(s.Port is > 0 and not 443 ? $":{s.Port}" : "")}/ovirt-engine/api";
        var user = s.Username.Trim();
        if (!user.Contains('@')) user += "@internal";
        _auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}"));
        JsonElement api;
        try { api = await GetAsync("", ct); }
        catch (CloudHttpException ex) when (ex.Status == HttpStatusCode.Unauthorized) { throw new CloudException("The engine rejected the user name or password (the user has the form name@domain, for example admin@internal)."); }
        catch (CloudHttpException ex) when (ex.Status == HttpStatusCode.NotFound) { throw new CloudException($"{s.Host} does not answer the oVirt / RHV REST API (https://{s.Host}/ovirt-engine/api)."); }
        var snap = new CloudSnapshot
        {
            Identity = $"{user} on {s.Host} · {Str(api, "product_info", "name") ?? "oVirt"} {Str(api, "product_info", "version", "full_version")}".Trim(),
        };
        var clusters = await GetAsync("/clusters", ct);
        var hosts = await GetAsync("/hosts", ct);
        JsonElement domains = default;
        try { domains = await GetAsync("/storagedomains", ct); } catch (CloudHttpException ex) { snap.Warn("Storage domains not read — " + ex.Message, "datastore"); }
        var vms = await GetAsync("/vms?follow=nics,reported_devices,snapshots", ct);
        Map(clusters, hosts, domains, vms, snap, DateTimeOffset.UtcNow);
        return snap;
    }

    /// <summary>REST v4 JSON answers to resources (pure; unit tested).</summary>
    internal static void Map(JsonElement clusters, JsonElement hosts, JsonElement domains, JsonElement vms, CloudSnapshot snap, DateTimeOffset now)
    {
        var clusterName = Arr(clusters, "cluster").ToDictionary(c => Str(c, "id")!, c => Str(c, "name") ?? "");
        static int? Cores(JsonElement e) =>
            Long(e, "cpu", "topology", "sockets") is { } so ? (int)(so * (Long(e, "cpu", "topology", "cores") ?? 1) * (Long(e, "cpu", "topology", "threads") ?? 1)) : null;
        var vmList = Arr(vms, "vm").ToList();
        foreach (var c in Arr(clusters, "cluster"))
        {
            var item = new CloudItem { Kind = "cluster", ExternalId = "cluster/" + Str(c, "id"), Name = Str(c, "name") };
            item.Properties["hosts"] = Arr(hosts, "host").Count(h => Str(h, "cluster", "id") == Str(c, "id"));
            item.Properties["cpuType"] = Str(c, "cpu", "type");
            item.Properties["compatibilityVersion"] = $"{Str(c, "version", "major")}.{Str(c, "version", "minor")}";
            snap.Items.Add(item);
        }
        foreach (var h in Arr(hosts, "host"))
        {
            var id = Str(h, "id")!;
            var item = new CloudItem
            {
                Kind = "host", ExternalId = id, Name = Str(h, "name"), Hostname = Str(h, "address"), State = Str(h, "status"), ParentId = "cluster/" + Str(h, "cluster", "id"),
                Group = Str(h, "cluster", "id") is { } cid ? clusterName.GetValueOrDefault(cid) : null,
                Os = $"{Str(h, "os", "type")} {Str(h, "os", "version", "full_version")}".Trim() is { Length: > 0 } os ? os : null,
                Cpu = Cores(h), MemoryMb = Long(h, "memory") is { } m ? m / 1048576 : null, BiosUuid = Str(h, "hardware_information", "uuid")?.ToUpperInvariant(),
            };
            item.Properties["model"] = $"{Str(h, "hardware_information", "manufacturer")} {Str(h, "hardware_information", "product_name")}".Trim();
            item.Properties["serial"] = Str(h, "hardware_information", "serial_number");
            item.Properties["vms"] = vmList.Count(v => Str(v, "host", "id") == id);
            if (item.MemoryMb is > 0)
                item.Properties["memoryCommitPercent"] = vmList.Where(v => Str(v, "host", "id") == id && Str(v, "status") == "up").Sum(v => Long(v, "memory") ?? 0) / 1048576 * 100 / item.MemoryMb.Value;
            snap.Items.Add(item);
        }
        foreach (var dmn in Arr(domains, "storage_domain").Where(x => Str(x, "type") is null or "data"))
        {
            var avail = Long(dmn, "available");
            var used = Long(dmn, "used");
            var item = new CloudItem
            {
                Kind = "datastore", ExternalId = "sd/" + Str(dmn, "id"), Name = Str(dmn, "name"), Size = Str(dmn, "storage", "type"),
                State = Str(dmn, "external_status") ?? Str(dmn, "status") ?? "ok",
            };
            if (avail is not null && used is not null && avail + used > 0)
            {
                item.DiskGb = Gb(avail.Value + used.Value);
                item.Properties["freeGb"] = Gb(avail.Value);
                item.Properties["freePercent"] = (long)Math.Round(avail.Value * 100m / (avail.Value + used.Value));
            }
            if (Long(dmn, "committed") is { } com && avail + used is > 0) item.Properties["overcommitPercent"] = com * 100 / (avail!.Value + used!.Value);
            snap.Items.Add(item);
        }
        foreach (var v in vmList)
        {
            var status = Str(v, "status");
            var item = new CloudItem
            {
                Kind = "vm", ExternalId = Str(v, "id")!, Name = Str(v, "name"), State = status, ParentId = Str(v, "host", "id"),
                Group = Str(v, "cluster", "id") is { } cid ? clusterName.GetValueOrDefault(cid) : null,
                Cpu = Cores(v), MemoryMb = Long(v, "memory") is { } m ? m / 1048576 : null, BiosUuid = Str(v, "id")?.ToUpperInvariant(),
                Hostname = Str(v, "fqdn")?.Split('.')[0],
                Os = Str(v, "guest_operating_system", "distribution") is { } dist ? $"{dist} {Str(v, "guest_operating_system", "version", "full_version")}".Trim() : Str(v, "os", "type"),
                CreatedAt = EpochMs(Long(v, "creation_time")),
                StateSince = status == "down" ? EpochMs(Long(v, "stop_time")) : status == "up" ? EpochMs(Long(v, "start_time")) : null,
            };
            foreach (var n in Arr(v, "nics", "nic")) if (Str(n, "mac", "address") is { } mac) item.Macs.Add(AzureCollector.NormalizeMac(mac));
            item.PrivateIp = Arr(v, "reported_devices", "reported_device").SelectMany(d => Arr(d, "ips", "ip"))
                .Where(ip => Str(ip, "version") is null or "v4").Select(ip => Str(ip, "address")).FirstOrDefault(a => a is not null && !a.StartsWith("127."));
            var snaps = Arr(v, "snapshots", "snapshot").Where(x => Str(x, "snapshot_type") is not "active").ToList();
            if (snaps.Count > 0)
            {
                item.Properties["snapshotCount"] = (long)snaps.Count;
                if (snaps.Select(x => EpochMs(Long(x, "date"))).Where(x => x is not null).Min() is { } oldest) item.Properties["snapshotOldestDays"] = (long)(now - oldest).TotalDays;
            }
            item.Properties["fqdn"] = Str(v, "fqdn");
            item.Properties["highAvailability"] = Bool(v, "high_availability", "enabled");
            item.Properties["description"] = Str(v, "description");
            snap.Items.Add(item);
        }
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _auth);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("Version", "4");
        return await SendAsync(_http!, req, "oVirt " + (path.Length == 0 ? "API" : path.TrimStart('/').Split('?')[0]), ct);
    }
}
