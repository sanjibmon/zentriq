using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Zentriq.Infrastructure.Cloud.CloudJson;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>
/// Google Cloud, read-only: a service account JSON key with the Viewer role (or Compute Viewer, Cloud SQL Viewer,
/// Kubernetes Engine Viewer, Storage Object Viewer) on the projects. Per project it reads Compute Engine instances,
/// persistent disks, external addresses, VPC networks, firewall rules and forwarding rules (load balancers), Cloud Storage
/// buckets, Cloud SQL instances, GKE clusters and Cloud Functions. An API that is not enabled in a project is a warning,
/// not a failure. Nothing is changed in Google Cloud.
/// </summary>
public sealed class GcpCollector(HttpClient? http = null, string tokenUrl = "https://oauth2.googleapis.com/token",
    Func<string, string>? apiBase = null) : ICloudCollector
{
    private readonly HttpClient _http = http ?? CloudHttp.Create();
    private readonly Func<string, string> _api = apiBase ?? (api => $"https://{api}.googleapis.com");
    private string _token = "";

    public sealed record ServiceAccountKey(string ClientEmail, string PrivateKey, string? ProjectId, string? TokenUri);

    /// <summary>Reads the JSON key file of a service account; null when it is not one.</summary>
    public static ServiceAccountKey? ParseKey(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (Str(r, "type") != "service_account" || Str(r, "client_email") is not { } email || Str(r, "private_key") is not { } key) return null;
            return new ServiceAccountKey(email, key, Str(r, "project_id"), Str(r, "token_uri"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The signed JWT (RS256) exchanged for an access token.</summary>
    public static string Assertion(ServiceAccountKey key, string audience, DateTimeOffset now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = key.ClientEmail, ["scope"] = "https://www.googleapis.com/auth/cloud-platform.read-only",
            ["aud"] = audience, ["iat"] = now.ToUnixTimeSeconds(), ["exp"] = now.AddMinutes(55).ToUnixTimeSeconds(),
        }));
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(key.PrivateKey); }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException) { throw new CloudException("The private key in the JSON key file could not be read. Paste the whole key file as downloaded.", ex); }
        var sig = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{B64(sig)}";
    }

    public async Task<CloudSnapshot> CollectAsync(CloudSettings s, string? secret, CancellationToken ct)
    {
        var key = ParseKey(secret) ?? throw new CloudException("Paste the service account's JSON key file (IAM & Admin › Service accounts › Keys › Add key › JSON).");
        await SignInAsync(key, ct);
        var snap = new CloudSnapshot();
        var projects = s.Projects.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        if (projects.Count == 0)
        {
            try
            {
                var list = await GetAsync(_api("cloudresourcemanager") + "/v1/projects?filter=lifecycleState%3AACTIVE", "Projects", ct);
                projects = Arr(list, "projects").Select(p => Str(p, "projectId")).OfType<string>().ToList();
            }
            catch (CloudHttpException) { /* Resource Manager API not enabled or not allowed: the key's own project */ }
            if (projects.Count == 0 && key.ProjectId is { } own) projects.Add(own);
        }
        if (projects.Count == 0) throw new CloudException("No project to read. Enter the project IDs, or give the service account the Viewer role on them.");
        snap.Identity = $"{key.ClientEmail} · {projects.Count} project(s): {string.Join(", ", projects.Take(5))}{(projects.Count > 5 ? " …" : "")}";

        foreach (var p in projects)
        {
            ct.ThrowIfCancellationRequested();
            var c = _api("compute") + $"/compute/v1/projects/{p}";
            await Part(snap, p, "Compute Engine instances", ["vm"], async () => MapInstances(await PagedAsync(c + "/aggregated/instances", "Instances", ct), p, snap));
            await Part(snap, p, "persistent disks", ["disk"], async () => MapDisks(await PagedAsync(c + "/aggregated/disks", "Disks", ct), p, snap));
            await Part(snap, p, "external addresses", ["public_ip"], async () => MapAddresses(await PagedAsync(c + "/aggregated/addresses", "Addresses", ct), p, snap));
            await Part(snap, p, "VPC networks", ["network"], async () => MapNetworks(await PagedAsync(c + "/global/networks", "Networks", ct), p, snap));
            await Part(snap, p, "firewall rules", ["security_group"], async () => MapFirewalls(await PagedAsync(c + "/global/firewalls", "Firewall rules", ct), p, snap));
            await Part(snap, p, "load balancers", ["load_balancer"], async () => MapForwardingRules(await PagedAsync(c + "/aggregated/forwardingRules", "Forwarding rules", ct), p, snap));
            await Part(snap, p, "Cloud Storage buckets", ["storage"], async () => MapBuckets(await PagedAsync(_api("storage") + $"/storage/v1/b?project={p}", "Buckets", ct), p, snap));
            await Part(snap, p, "Cloud SQL instances", ["database"], async () => MapSql(await PagedAsync(_api("sqladmin") + $"/v1/projects/{p}/instances", "Cloud SQL", ct), p, snap));
            await Part(snap, p, "GKE clusters", ["kubernetes"], async () => MapGke(await GetAsync(_api("container") + $"/v1/projects/{p}/locations/-/clusters", "GKE", ct), p, snap));
            await Part(snap, p, "Cloud Functions", ["function"], async () => MapFunctions(await PagedAsync(_api("cloudfunctions") + $"/v2/projects/{p}/locations/-/functions", "Cloud Functions", ct), p, snap));
        }
        return snap;
    }

    private static async Task Part(CloudSnapshot snap, string project, string what, string[] kinds, Func<Task> read)
    {
        try { await read(); }
        catch (CloudHttpException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            var reason = ex.Body.Contains("SERVICE_DISABLED") || ex.Body.Contains("has not been used") || ex.Body.Contains("is disabled")
                ? "the API is not enabled in this project (nothing to read there, or enable it)"
                : ex.Status == HttpStatusCode.Forbidden ? "permission missing; give the service account the Viewer role" : ex.Message;
            snap.Warn($"{project}: {what} not read — {reason}", kinds);
        }
    }

    // ---------- mapping (pure; unit tested) ----------

    /// <summary>Items of an aggregated list ({"items":{"zones/x":{"instances":[…]}}}) or a plain list ({"items":[…]}).</summary>
    internal static IEnumerable<JsonElement> Items(IEnumerable<JsonElement> pages, string? field = null)
    {
        foreach (var page in pages)
        {
            var items = Get(page, "items");
            if (items.ValueKind == JsonValueKind.Array) foreach (var i in items.EnumerateArray()) yield return i;
            else if (items.ValueKind == JsonValueKind.Object && field is not null)
                foreach (var scope in items.EnumerateObject())
                    foreach (var i in Arr(scope.Value, field)) yield return i;
        }
    }

    private static Dictionary<string, string> Labels(JsonElement e) =>
        Get(e, "labels").ValueKind == JsonValueKind.Object
            ? Get(e, "labels").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"us-central1-a" → region "us-central1".</summary>
    private static string? RegionOf(string? zone) => zone is null ? null : zone.Count(c => c == '-') >= 2 ? zone[..zone.LastIndexOf('-')] : zone;

    internal static void MapInstances(IEnumerable<JsonElement> pages, string project, CloudSnapshot snap)
    {
        foreach (var i in Items(pages, "instances"))
        {
            var zone = Last(Str(i, "zone"));
            var status = Str(i, "status");
            var item = new CloudItem
            {
                Kind = "vm", ExternalId = Str(i, "id") ?? Str(i, "selfLink")!, Name = Str(i, "name"), Region = zone, Group = project, State = status?.ToLowerInvariant(),
                Size = Last(Str(i, "machineType")), Hostname = Str(i, "name"), Tags = Labels(i), CreatedAt = Date(Str(i, "creationTimestamp")),
            };
            foreach (var n in Arr(i, "networkInterfaces"))
            {
                item.PrivateIp ??= Str(n, "networkIP");
                foreach (var a in Arr(n, "accessConfigs")) item.PublicIp ??= Str(a, "natIP");
            }
            var bootLicense = Arr(i, "disks").Where(d => Bool(d, "boot") == true).SelectMany(d => Arr(d, "licenses")).Select(x => Last(x.GetString())).FirstOrDefault();
            item.Os = bootLicense?.Replace('-', ' ') is { } os ? char.ToUpperInvariant(os[0]) + os[1..] : null;
            if (status is "TERMINATED" or "STOPPED" or "SUSPENDED" && Date(Str(i, "lastStopTimestamp") ?? Str(i, "lastSuspendedTimestamp")) is { } since) item.StateSince = since;
            item.Properties["project"] = project;
            item.Properties["zone"] = zone;
            item.Properties["preemptible"] = Bool(i, "scheduling", "preemptible");
            item.Properties["deletionProtection"] = Bool(i, "deletionProtection");
            item.Properties["disks"] = Arr(i, "disks").Select(d => Last(Str(d, "source"))).OfType<string>().ToList();
            snap.Items.Add(item);
        }
    }

    internal static void MapDisks(IEnumerable<JsonElement> pages, string project, CloudSnapshot snap)
    {
        foreach (var d in Items(pages, "disks"))
        {
            var users = Arr(d, "users").Select(u => Last(u.GetString())).OfType<string>().ToList();
            var item = new CloudItem
            {
                Kind = "disk", ExternalId = Str(d, "id") ?? Str(d, "selfLink")!, Name = Str(d, "name"), Region = Last(Str(d, "zone")) ?? Last(Str(d, "region")),
                Group = project, State = Str(d, "status")?.ToLowerInvariant(), Size = Last(Str(d, "type")), Tags = Labels(d), CreatedAt = Date(Str(d, "creationTimestamp")),
                ParentId = users.FirstOrDefault(),
            };
            if (Long(d, "sizeGb") is { } gb) item.DiskGb = gb;
            item.Properties["attached"] = users.Count > 0;
            item.Properties["usedBy"] = users;
            item.Properties["customerManagedKey"] = Get(d, "diskEncryptionKey", "kmsKeyName").ValueKind == JsonValueKind.String;
            snap.Items.Add(item);
        }
    }

    internal static void MapAddresses(IEnumerable<JsonElement> pages, string project, CloudSnapshot snap)
    {
        foreach (var a in Items(pages, "addresses").Where(a => Str(a, "addressType") is null or "EXTERNAL"))
        {
            var inUse = Str(a, "status") == "IN_USE";
            var item = new CloudItem
            {
                Kind = "public_ip", ExternalId = Str(a, "id") ?? Str(a, "selfLink")!, Name = Str(a, "name"), Region = Last(Str(a, "region")) ?? "global", Group = project,
                PublicIp = Str(a, "address"), State = inUse ? "in use" : "reserved", ParentId = Last(Arr(a, "users").Select(u => u.GetString()).FirstOrDefault()),
                Tags = Labels(a), CreatedAt = Date(Str(a, "creationTimestamp")),
            };
            item.Properties["associated"] = inUse;
            snap.Items.Add(item);
        }
    }

    internal static void MapNetworks(IEnumerable<JsonElement> pages, string project, CloudSnapshot snap)
    {
        foreach (var n in Items(pages))
        {
            var item = new CloudItem
            {
                Kind = "network", ExternalId = Str(n, "id") ?? Str(n, "selfLink")!, Name = Str(n, "name"), Region = "global", Group = project,
                Size = Bool(n, "autoCreateSubnetworks") == true ? "auto mode" : "custom mode", CreatedAt = Date(Str(n, "creationTimestamp")),
            };
            item.Properties["subnets"] = Arr(n, "subnetworks").Count();
            snap.Items.Add(item);
        }
    }

    internal static void MapFirewalls(IEnumerable<JsonElement> pages, string project, CloudSnapshot snap)
    {
        foreach (var f in Items(pages))
        {
            var open = new List<int>();
            var rules = new List<string>();
            var ingress = Str(f, "direction") is null or "INGRESS";
            var anywhere = Arr(f, "sourceRanges").Any(r => r.GetString() is "0.0.0.0/0" or "::/0");
            if (ingress && anywhere && Bool(f, "disabled") != true)
            {
                foreach (var a in Arr(f, "allowed"))
                {
                    var proto = Str(a, "IPProtocol");
                    var ports = Arr(a, "ports").Select(p => p.GetString() ?? "").ToList();
                    var hit = proto == "all" || (proto is "tcp" or "6" && ports.Count == 0) ? new List<int> { 0 }
                        : proto is "tcp" or "6" ? ports.SelectMany(AzureCollector.OpenAdminPorts).ToList() : new List<int>();
                    if (hit.Count == 0) continue;
                    open.AddRange(hit);
                    rules.Add($"{proto} {(ports.Count == 0 ? "all ports" : string.Join(",", ports))} from anywhere");
                }
            }
            var item = new CloudItem
            {
                Kind = "security_group", ExternalId = Str(f, "id") ?? Str(f, "selfLink")!, Name = Str(f, "name"), Region = "global", Group = project,
                State = Bool(f, "disabled") == true ? "disabled" : "enabled", Size = Last(Str(f, "network")), CreatedAt = Date(Str(f, "creationTimestamp")),
            };
            item.Properties["openPorts"] = open.Distinct().ToList();
            item.Properties["openRules"] = rules;
            item.Properties["direction"] = Str(f, "direction");
            item.Properties["description"] = Str(f, "description");
            snap.Items.Add(item);
        }
    }

    internal static void MapForwardingRules(IEnumerable<JsonElement> pages, string project, CloudSnapshot snap)
    {
        foreach (var r in Items(pages, "forwardingRules"))
        {
            var item = new CloudItem
            {
                Kind = "load_balancer", ExternalId = Str(r, "id") ?? Str(r, "selfLink")!, Name = Str(r, "name"), Region = Last(Str(r, "region")) ?? "global", Group = project,
                Size = Str(r, "loadBalancingScheme"), PublicIp = Str(r, "loadBalancingScheme") == "EXTERNAL" || Str(r, "loadBalancingScheme") == "EXTERNAL_MANAGED" ? Str(r, "IPAddress") : null,
                PrivateIp = Str(r, "loadBalancingScheme")?.StartsWith("INTERNAL") == true ? Str(r, "IPAddress") : null, Tags = Labels(r), CreatedAt = Date(Str(r, "creationTimestamp")),
            };
            item.Properties["protocol"] = Str(r, "IPProtocol");
            item.Properties["ports"] = Str(r, "portRange");
            snap.Items.Add(item);
        }
    }

    internal static void MapBuckets(IEnumerable<JsonElement> pages, string project, CloudSnapshot snap)
    {
        foreach (var b in Items(pages))
        {
            var item = new CloudItem
            {
                Kind = "storage", ExternalId = "gs://" + Str(b, "name"), Name = Str(b, "name"), Region = Str(b, "location")?.ToLowerInvariant(), Group = project,
                Size = Str(b, "storageClass"), Os = "Cloud Storage", Tags = Labels(b), CreatedAt = Date(Str(b, "timeCreated")),
            };
            item.Properties["publicAccessPrevention"] = Str(b, "iamConfiguration", "publicAccessPrevention");
            item.Properties["uniformAccess"] = Bool(b, "iamConfiguration", "uniformBucketLevelAccess", "enabled");
            item.Properties["versioning"] = Bool(b, "versioning", "enabled");
            snap.Items.Add(item);
        }
    }

    internal static void MapSql(IEnumerable<JsonElement> pages, string project, CloudSnapshot snap)
    {
        foreach (var d in Items(pages))
        {
            var ip = Get(d, "settings", "ipConfiguration");
            var openToAll = Arr(ip, "authorizedNetworks").Any(n => Str(n, "value") is "0.0.0.0/0" or "::/0");
            var item = new CloudItem
            {
                Kind = "database", ExternalId = $"{project}:{Str(d, "name")}", Name = Str(d, "name"), Region = Str(d, "region"), Group = project,
                State = Str(d, "state")?.ToLowerInvariant(), Size = Str(d, "settings", "tier"), Os = Str(d, "databaseVersion")?.Replace('_', ' '),
                Tags = Get(d, "settings", "userLabels").ValueKind == JsonValueKind.Object
                    ? Get(d, "settings", "userLabels").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase),
                PublicIp = Arr(d, "ipAddresses").Where(a => Str(a, "type") == "PRIMARY").Select(a => Str(a, "ipAddress")).FirstOrDefault(),
                PrivateIp = Arr(d, "ipAddresses").Where(a => Str(a, "type") == "PRIVATE").Select(a => Str(a, "ipAddress")).FirstOrDefault(),
                CreatedAt = Date(Str(d, "createTime")),
            };
            if (Long(d, "settings", "dataDiskSizeGb") is { } gb) item.DiskGb = gb;
            item.Properties["publiclyAccessible"] = Bool(ip, "ipv4Enabled") == true && openToAll;
            item.Properties["requireSsl"] = Bool(ip, "requireSsl");
            item.Properties["backups"] = Bool(d, "settings", "backupConfiguration", "enabled");
            snap.Items.Add(item);
        }
    }

    internal static void MapGke(JsonElement root, string project, CloudSnapshot snap)
    {
        foreach (var c in Arr(root, "clusters"))
        {
            var item = new CloudItem
            {
                Kind = "kubernetes", ExternalId = Str(c, "selfLink") ?? $"{project}/{Str(c, "location")}/{Str(c, "name")}", Name = Str(c, "name"), Region = Str(c, "location"),
                Group = project, State = Str(c, "status")?.ToLowerInvariant(), Os = "Kubernetes " + Str(c, "currentMasterVersion"), Hostname = Str(c, "endpoint"),
                Tags = Get(c, "resourceLabels").ValueKind == JsonValueKind.Object
                    ? Get(c, "resourceLabels").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase),
                CreatedAt = Date(Str(c, "createTime")),
            };
            item.Properties["nodes"] = Long(c, "currentNodeCount");
            item.Properties["nodePools"] = Arr(c, "nodePools").Count();
            item.Properties["privateNodes"] = Bool(c, "privateClusterConfig", "enablePrivateNodes");
            snap.Items.Add(item);
        }
    }

    internal static void MapFunctions(IEnumerable<JsonElement> pages, string project, CloudSnapshot snap)
    {
        foreach (var f in pages.SelectMany(p => Arr(p, "functions")))
        {
            var name = Str(f, "name") ?? "";   // projects/p/locations/r/functions/n
            var parts = name.Split('/');
            var item = new CloudItem
            {
                Kind = "function", ExternalId = name, Name = parts[^1], Region = parts.Length > 3 ? parts[3] : null, Group = project,
                State = Str(f, "state")?.ToLowerInvariant(), Os = Str(f, "buildConfig", "runtime"), Tags = Labels(f), CreatedAt = Date(Str(f, "createTime")),
            };
            if (Str(f, "serviceConfig", "availableMemory") is { } mem && long.TryParse(new string(mem.TakeWhile(char.IsDigit).ToArray()), out var m))
                item.MemoryMb = mem.EndsWith("Gi", StringComparison.Ordinal) || mem.EndsWith('G') ? m * 1024 : m;
            snap.Items.Add(item);
        }
    }

    // ---------- requests ----------

    private async Task SignInAsync(ServiceAccountKey key, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = Assertion(key, "https://oauth2.googleapis.com/token", DateTimeOffset.UtcNow),
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = form };
        try
        {
            var r = await SendAsync(_http, req, "Google sign-in", ct);
            _token = Str(r, "access_token") ?? throw new CloudException("Google sign-in returned no access token.");
        }
        catch (CloudHttpException ex)
        {
            throw new CloudException(ex.Body.Contains("invalid_grant") || ex.Body.Contains("Invalid JWT")
                ? "Google sign-in failed: the key was not accepted (deleted or disabled key, disabled service account, or the server clock is wrong)."
                : "Google sign-in failed: " + ex.Message);
        }
    }

    private async Task<JsonElement> GetAsync(string url, string what, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            try { return await SendAsync(_http, req, what, ct); }
            catch (CloudHttpException ex) when (ex.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }
        }
    }

    private async Task<List<JsonElement>> PagedAsync(string url, string what, CancellationToken ct)
    {
        var pages = new List<JsonElement>();
        string? token = null;
        do
        {
            var u = token is null ? url : url + (url.Contains('?') ? "&" : "?") + "pageToken=" + Uri.EscapeDataString(token);
            var page = await GetAsync(u, what, ct);
            pages.Add(page);
            token = Str(page, "nextPageToken");
        } while (token is not null && pages.Count < 200);
        return pages;
    }
}
