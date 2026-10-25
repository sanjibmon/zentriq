using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>
/// Microsoft Azure, read-only: an app registration (tenant id, client id, client secret) with the Reader role on the
/// subscriptions. One Azure Resource Graph query lists virtual machines (with power state), disks, public IPs, networks,
/// network security groups, load balancers, storage accounts, databases, AKS clusters and function apps; network
/// interfaces give each machine its MAC and IP addresses. Nothing is changed in Azure.
/// </summary>
public sealed class AzureCollector(HttpClient? http = null, string loginBase = "https://login.microsoftonline.com", string armBase = "https://management.azure.com") : ICloudCollector
{
    private readonly HttpClient _http = http ?? CloudHttp.Create();

    internal const string Query = """
        Resources
        | where type in~ ('microsoft.compute/virtualmachines','microsoft.compute/disks','microsoft.network/publicipaddresses',
            'microsoft.network/virtualnetworks','microsoft.network/networksecuritygroups','microsoft.network/loadbalancers',
            'microsoft.storage/storageaccounts','microsoft.sql/servers/databases','microsoft.dbforpostgresql/flexibleservers',
            'microsoft.dbformysql/flexibleservers','microsoft.containerservice/managedclusters','microsoft.web/sites',
            'microsoft.network/networkinterfaces')
        | project id, name, type, kind, location, resourceGroup, subscriptionId, tags, sku, properties
        """;

    public async Task<CloudSnapshot> CollectAsync(CloudSettings s, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.TenantId) || string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrEmpty(secret))
            throw new CloudException("Enter the tenant ID, client (application) ID and client secret of the app registration.");
        var token = await TokenAsync(s.TenantId.Trim(), s.ClientId.Trim(), secret, ct);
        var snap = new CloudSnapshot();

        var subs = new List<(string Id, string Name)>();
        using (var doc = await GetAsync($"{armBase}/subscriptions?api-version=2022-12-01", token, ct))
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
                subs.Add((e.GetProperty("subscriptionId").GetString()!, e.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : ""));
        var wanted = s.Subscriptions.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (wanted.Count > 0)
        {
            foreach (var w in wanted.Where(w => !subs.Any(x => x.Id.Equals(w, StringComparison.OrdinalIgnoreCase))))
                snap.Warn($"Subscription {w} is not visible to the app. Give the app the Reader role on it.");
            subs = subs.Where(x => wanted.Contains(x.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        if (subs.Count == 0)
            throw new CloudException("The app registration can sign in but sees no subscription. Give it the Reader role on the subscriptions (Access control (IAM) › Add role assignment).");
        snap.Identity = $"{subs.Count} subscription(s): {string.Join(", ", subs.Select(x => x.Name.Length > 0 ? x.Name : x.Id).Take(5))}{(subs.Count > 5 ? " …" : "")}";

        var rows = new List<JsonElement>();
        foreach (var chunk in subs.Chunk(500))
        {
            string? skip = null;
            do
            {
                var body = new JsonObject
                {
                    ["subscriptions"] = new JsonArray(chunk.Select(x => (JsonNode)JsonValue.Create(x.Id)!).ToArray()),
                    ["query"] = Query,
                    ["options"] = new JsonObject { ["$top"] = 1000, ["resultFormat"] = "objectArray" },
                };
                if (skip is not null) body["options"]!["$skipToken"] = skip;
                using var doc = await PostAsync($"{armBase}/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01", token, body.ToJsonString(), ct);
                foreach (var e in doc.RootElement.GetProperty("data").EnumerateArray()) rows.Add(e.Clone());
                skip = doc.RootElement.TryGetProperty("$skipToken", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                if (doc.RootElement.TryGetProperty("resultTruncated", out var tr) && tr.ValueKind == JsonValueKind.String && tr.GetString() == "true" && skip is null)
                    snap.Warn("Azure Resource Graph truncated the result; some resources were not read.");
            } while (skip is not null);
        }
        Map(rows, snap);
        return snap;
    }

    /// <summary>Turns Resource Graph rows into resources (pure; unit tested).</summary>
    internal static void Map(IReadOnlyList<JsonElement> rows, CloudSnapshot snap)
    {
        static string? Str(JsonElement e, params string[] path)
        {
            foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
            return e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.ToString(), _ => null };
        }
        static JsonElement Get(JsonElement e, params string[] path)
        {
            foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return default; }
            return e;
        }
        static IEnumerable<JsonElement> Arr(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
        static string Id(JsonElement e) => (Str(e, "id") ?? "").ToLowerInvariant();

        // Network interfaces: MAC, private IPs and public IP ids per virtual machine; public IP addresses by id.
        var nicsByVm = new Dictionary<string, List<JsonElement>>();
        var publicIps = new Dictionary<string, string>();
        foreach (var r in rows)
        {
            var type = (Str(r, "type") ?? "").ToLowerInvariant();
            if (type == "microsoft.network/networkinterfaces" && Str(r, "properties", "virtualMachine", "id") is { } vm)
            {
                var key = vm.ToLowerInvariant();
                if (!nicsByVm.TryGetValue(key, out var l)) nicsByVm[key] = l = [];
                l.Add(r);
            }
            else if (type == "microsoft.network/publicipaddresses" && Str(r, "properties", "ipAddress") is { } ip) publicIps[Id(r)] = ip;
        }

        foreach (var r in rows)
        {
            var type = (Str(r, "type") ?? "").ToLowerInvariant();
            var p = Get(r, "properties");
            CloudItem New(string kind) => new()
            {
                Kind = kind, ExternalId = Id(r), Name = Str(r, "name"), Region = Str(r, "location"), Group = Str(r, "resourceGroup"),
                Tags = Get(r, "tags").ValueKind == JsonValueKind.Object
                    ? Get(r, "tags").EnumerateObject().ToDictionary(t => t.Name, t => t.Value.ToString(), StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase),
                Properties = { ["subscriptionId"] = Str(r, "subscriptionId"), ["type"] = Str(r, "type") },
            };
            CloudItem? item = null;
            switch (type)
            {
                case "microsoft.compute/virtualmachines":
                {
                    item = New("vm");
                    var power = Str(p, "extended", "instanceView", "powerState", "code");
                    item.State = power?.Replace("PowerState/", "", StringComparison.OrdinalIgnoreCase) ?? Str(p, "provisioningState");
                    item.Size = Str(p, "hardwareProfile", "vmSize");
                    var osName = Str(p, "extended", "instanceView", "osName");
                    var img = Get(p, "storageProfile", "imageReference");
                    item.Os = osName is { Length: > 0 }
                        ? $"{osName} {Str(p, "extended", "instanceView", "osVersion")}".Trim()
                        : Str(img, "offer") is { } offer ? $"{offer} {Str(img, "sku")}".Trim() : Str(p, "storageProfile", "osDisk", "osType");
                    item.Hostname = Str(p, "extended", "instanceView", "computerName") ?? Str(p, "osProfile", "computerName");
                    item.BiosUuid = Str(p, "vmId")?.ToUpperInvariant();
                    if (DateTimeOffset.TryParse(Str(p, "timeCreated"), out var created)) item.CreatedAt = created;
                    item.Properties["osType"] = Str(p, "storageProfile", "osDisk", "osType");
                    item.Properties["priority"] = Str(p, "priority");
                    if (nicsByVm.TryGetValue(item.ExternalId, out var nics))
                    {
                        foreach (var n in nics)
                        {
                            if (Str(n, "properties", "macAddress") is { Length: > 0 } mac) item.Macs.Add(NormalizeMac(mac));
                            foreach (var c in Arr(Get(n, "properties", "ipConfigurations")))
                            {
                                item.PrivateIp ??= Str(c, "properties", "privateIPAddress");
                                if (Str(c, "properties", "publicIPAddress", "id") is { } pid && publicIps.TryGetValue(pid.ToLowerInvariant(), out var pub)) item.PublicIp ??= pub;
                            }
                        }
                    }
                    break;
                }
                case "microsoft.compute/disks":
                {
                    item = New("disk");
                    var state = Str(p, "diskState");
                    item.State = state;
                    item.Size = Str(r, "sku", "name");
                    if (decimal.TryParse(Str(p, "diskSizeGB"), System.Globalization.CultureInfo.InvariantCulture, out var gb)) item.DiskGb = gb;
                    item.ParentId = Str(r, "managedBy")?.ToLowerInvariant() ?? Str(p, "managedBy")?.ToLowerInvariant();
                    item.Properties["attached"] = !string.Equals(state, "Unattached", StringComparison.OrdinalIgnoreCase);
                    item.Properties["encryption"] = Str(p, "encryption", "type");
                    item.Os = Str(p, "osType");
                    if (DateTimeOffset.TryParse(Str(p, "timeCreated"), out var created)) item.CreatedAt = created;
                    break;
                }
                case "microsoft.network/publicipaddresses":
                {
                    item = New("public_ip");
                    item.PublicIp = Str(p, "ipAddress");
                    item.Size = Str(r, "sku", "name");
                    item.State = Str(p, "publicIPAllocationMethod");
                    var cfg = Str(p, "ipConfiguration", "id") ?? Str(p, "natGateway", "id");
                    item.ParentId = cfg?.ToLowerInvariant();
                    item.Properties["associated"] = cfg is not null;
                    break;
                }
                case "microsoft.network/virtualnetworks":
                    item = New("network");
                    item.Size = string.Join(", ", Arr(Get(p, "addressSpace", "addressPrefixes")).Select(x => x.GetString()));
                    item.Properties["subnets"] = Arr(Get(p, "subnets")).Count();
                    break;
                case "microsoft.network/networksecuritygroups":
                {
                    item = New("security_group");
                    var open = new List<int>();
                    var rules = new List<string>();
                    foreach (var rule in Arr(Get(p, "securityRules")))
                    {
                        var rp = Get(rule, "properties");
                        if (!string.Equals(Str(rp, "access"), "Allow", StringComparison.OrdinalIgnoreCase) || !string.Equals(Str(rp, "direction"), "Inbound", StringComparison.OrdinalIgnoreCase)) continue;
                        var sources = Arr(Get(rp, "sourceAddressPrefixes")).Select(x => x.GetString() ?? "").Append(Str(rp, "sourceAddressPrefix") ?? "");
                        if (!sources.Any(IsAnySource)) continue;
                        var ranges = Arr(Get(rp, "destinationPortRanges")).Select(x => x.GetString() ?? "").Append(Str(rp, "destinationPortRange") ?? "").Where(x => x.Length > 0).ToList();
                        var ports = ranges.SelectMany(OpenAdminPorts).ToList();
                        if (ports.Count > 0) { open.AddRange(ports); rules.Add($"{Str(rule, "name")}: {string.Join(",", ranges)}"); }
                    }
                    item.Properties["openPorts"] = open.Distinct().ToList();
                    item.Properties["openRules"] = rules;
                    item.Properties["attachedTo"] = Arr(Get(p, "networkInterfaces")).Count() + Arr(Get(p, "subnets")).Count();
                    break;
                }
                case "microsoft.network/loadbalancers":
                    item = New("load_balancer");
                    item.Size = Str(r, "sku", "name");
                    item.Properties["frontends"] = Arr(Get(p, "frontendIPConfigurations")).Count();
                    item.Properties["backendPools"] = Arr(Get(p, "backendAddressPools")).Count();
                    break;
                case "microsoft.storage/storageaccounts":
                    item = New("storage");
                    item.Size = Str(r, "sku", "name");
                    item.State = Str(p, "statusOfPrimary");
                    if (Get(p, "allowBlobPublicAccess").ValueKind is JsonValueKind.True or JsonValueKind.False) item.Properties["publicAccess"] = Get(p, "allowBlobPublicAccess").GetBoolean();
                    if (Get(p, "supportsHttpsTrafficOnly").ValueKind is JsonValueKind.True or JsonValueKind.False) item.Properties["httpsOnly"] = Get(p, "supportsHttpsTrafficOnly").GetBoolean();
                    item.Properties["publicNetworkAccess"] = Str(p, "publicNetworkAccess");
                    item.Properties["minimumTlsVersion"] = Str(p, "minimumTlsVersion");
                    item.Properties["kind"] = Str(r, "kind");
                    if (DateTimeOffset.TryParse(Str(p, "creationTime"), out var sc)) item.CreatedAt = sc;
                    break;
                case "microsoft.sql/servers/databases":
                    if (string.Equals(Str(r, "name"), "master", StringComparison.OrdinalIgnoreCase)) break;
                    item = New("database");
                    item.Os = "Azure SQL Database";
                    item.State = Str(p, "status");
                    item.Size = Str(r, "sku", "name");
                    if (long.TryParse(Str(p, "maxSizeBytes"), out var max)) item.DiskGb = Math.Round(max / 1073741824m, 1);
                    if (DateTimeOffset.TryParse(Str(p, "creationDate"), out var dc)) item.CreatedAt = dc;
                    break;
                case "microsoft.dbforpostgresql/flexibleservers":
                case "microsoft.dbformysql/flexibleservers":
                    item = New("database");
                    item.Os = (type.Contains("postgres") ? "PostgreSQL " : "MySQL ") + Str(p, "version");
                    item.State = Str(p, "state");
                    item.Size = Str(r, "sku", "name");
                    item.Hostname = Str(p, "fullyQualifiedDomainName");
                    if (decimal.TryParse(Str(p, "storage", "storageSizeGB"), System.Globalization.CultureInfo.InvariantCulture, out var sg)) item.DiskGb = sg;
                    if (Str(p, "network", "publicNetworkAccess") is { } pna) item.Properties["publiclyAccessible"] = string.Equals(pna, "Enabled", StringComparison.OrdinalIgnoreCase);
                    break;
                case "microsoft.containerservice/managedclusters":
                    item = New("kubernetes");
                    item.Os = "Kubernetes " + Str(p, "kubernetesVersion");
                    item.State = Str(p, "powerState", "code");
                    item.Hostname = Str(p, "fqdn");
                    item.Cpu = null;
                    item.Properties["nodes"] = Arr(Get(p, "agentPoolProfiles")).Sum(a => Get(a, "count").ValueKind == JsonValueKind.Number ? Get(a, "count").GetInt32() : 0);
                    item.Properties["nodePools"] = Arr(Get(p, "agentPoolProfiles")).Count();
                    break;
                case "microsoft.web/sites":
                    if (!(Str(r, "kind") ?? "").Contains("functionapp", StringComparison.OrdinalIgnoreCase)) break;
                    item = New("function");
                    item.State = Str(p, "state");
                    item.Hostname = Str(p, "defaultHostName");
                    item.Os = Str(p, "siteConfig", "linuxFxVersion") is { Length: > 0 } fx ? fx : (Str(r, "kind") ?? "").Contains("linux") ? "Linux" : "Windows";
                    if (Get(p, "httpsOnly").ValueKind is JsonValueKind.True or JsonValueKind.False) item.Properties["httpsOnly"] = Get(p, "httpsOnly").GetBoolean();
                    break;
            }
            if (item is not null) snap.Items.Add(item);
        }
    }

    internal static bool IsAnySource(string s) => s.Trim() is "*" or "Internet" or "0.0.0.0/0" or "0.0.0.0" or "::/0" or "Any";

    /// <summary>For a destination port expression ("*", "3389", "20-3400"): 0 when every port, else 22 / 3389 when covered.</summary>
    internal static IEnumerable<int> OpenAdminPorts(string range)
    {
        range = range.Trim();
        if (range is "*" or "0-65535" or "1-65535") { yield return 0; yield break; }
        int lo, hi;
        var dash = range.IndexOf('-');
        if (dash > 0) { if (!int.TryParse(range[..dash], out lo) || !int.TryParse(range[(dash + 1)..], out hi)) yield break; }
        else { if (!int.TryParse(range, out lo)) yield break; hi = lo; }
        if (lo <= 1 && hi >= 65535) { yield return 0; yield break; }
        if (lo <= 22 && hi >= 22) yield return 22;
        if (lo <= 3389 && hi >= 3389) yield return 3389;
    }

    internal static string NormalizeMac(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : mac.ToUpperInvariant();
    }

    private async Task<string> TokenAsync(string tenant, string client, string secret, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = client, ["client_secret"] = secret,
            ["scope"] = $"{armBase}/.default",
        });
        HttpResponseMessage resp;
        try { resp = await _http.PostAsync($"{loginBase}/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token", form, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { throw new CloudException(CloudHttp.Unreachable("login.microsoftonline.com", ex), ex); }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return JsonDocument.Parse(text).RootElement.GetProperty("access_token").GetString()!;
            string? desc = null;
            try { desc = JsonDocument.Parse(text).RootElement.GetProperty("error_description").GetString(); } catch (Exception) { /* not JSON */ }
            var first = desc?.Split('\n')[0].Trim();
            throw new CloudException(first switch
            {
                { } d when d.Contains("AADSTS7000215") => "Sign-in failed: the client secret is wrong. Copy the secret Value (not the Secret ID) from Certificates & secrets.",
                { } d when d.Contains("AADSTS7000222") => "Sign-in failed: the client secret has expired. Create a new one in Certificates & secrets.",
                { } d when d.Contains("AADSTS700016") => "Sign-in failed: no application with this client ID in the tenant. Check the Application (client) ID and Directory (tenant) ID.",
                { } d when d.Contains("AADSTS90002") || d.Contains("AADSTS900023") => "Sign-in failed: the tenant ID is not valid. Use the Directory (tenant) ID from the app's Overview page.",
                { } d => "Sign-in failed: " + d,
                _ => $"Sign-in failed (HTTP {(int)resp.StatusCode}).",
            });
        }
    }

    private Task<JsonDocument> GetAsync(string url, string token, CancellationToken ct) => SendAsync(HttpMethod.Get, url, token, null, ct);
    private Task<JsonDocument> PostAsync(string url, string token, string json, CancellationToken ct) => SendAsync(HttpMethod.Post, url, token, json, ct);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string token, string? json, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { throw new CloudException(CloudHttp.Unreachable(new Uri(url).Host, ex), ex); }
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode) return JsonDocument.Parse(text);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                    await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, ct);
                    continue;
                }
                string? msg = null;
                try { msg = JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("message").GetString(); } catch (Exception) { /* not JSON */ }
                throw new CloudException(resp.StatusCode == HttpStatusCode.Forbidden
                    ? "Azure refused the request: the app needs the Reader role on the subscriptions. " + msg
                    : $"Azure returned HTTP {(int)resp.StatusCode}: {msg ?? text[..Math.Min(text.Length, 300)]}");
            }
        }
    }
}
