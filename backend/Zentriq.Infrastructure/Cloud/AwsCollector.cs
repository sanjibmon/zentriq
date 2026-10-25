using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>
/// Amazon Web Services, read-only: an IAM access key with the ReadOnlyAccess (or SecurityAudit) policy. Per region it
/// reads EC2 instances, EBS volumes, Elastic IPs, security groups, VPCs, RDS databases, load balancers, EKS clusters and
/// Lambda functions; S3 buckets once. Requests are signed with Signature Version 4. Nothing is changed in AWS.
/// </summary>
public sealed class AwsCollector(HttpClient? http = null, Func<string, string, string>? endpoint = null) : ICloudCollector
{
    private readonly HttpClient _http = http ?? CloudHttp.Create();
    /// <summary>(service, region) → base URL; overridable for tests.</summary>
    private readonly Func<string, string, string> _endpoint = endpoint ?? ((service, region) => service switch
    {
        "sts" => "https://sts.amazonaws.com",
        "s3" => "https://s3.amazonaws.com",
        "elasticloadbalancing" => $"https://elasticloadbalancing.{region}.amazonaws.com",
        _ => $"https://{service}.{region}.amazonaws.com",
    });

    private string _key = "", _secret = "";

    public async Task<CloudSnapshot> CollectAsync(CloudSettings s, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.AccessKeyId) || string.IsNullOrEmpty(secret))
            throw new CloudException("Enter the access key ID and secret access key of an IAM user with read-only access.");
        _key = s.AccessKeyId.Trim();
        _secret = secret.Trim();
        var snap = new CloudSnapshot();

        var who = await QueryAsync("sts", "us-east-1", "GetCallerIdentity", "2011-06-15", [], ct);
        snap.Identity = $"{V(who.Root, "Arn")} (account {V(who.Root, "Account")})";

        var regions = s.Regions.Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        if (regions.Count == 0)
        {
            var doc = await QueryAsync("ec2", "us-east-1", "DescribeRegions", "2016-11-15", [], ct);
            regions = Items(doc.Root, "regionInfo").Select(i => V(i, "regionName")).OfType<string>().Order().ToList();
        }

        foreach (var region in regions)
        {
            ct.ThrowIfCancellationRequested();
            await Part(snap, region, "EC2 instances", ["vm"], async () => ParseInstances(await PagedAsync("ec2", region, "DescribeInstances", "2016-11-15", "NextToken", "nextToken", ct), region, snap));
            await Part(snap, region, "EBS volumes", ["disk"], async () => ParseVolumes(await PagedAsync("ec2", region, "DescribeVolumes", "2016-11-15", "NextToken", "nextToken", ct), region, snap));
            await Part(snap, region, "Elastic IPs", ["public_ip"], async () => ParseAddresses([await QueryAsync("ec2", region, "DescribeAddresses", "2016-11-15", [], ct)], region, snap));
            await Part(snap, region, "security groups", ["security_group"], async () => ParseSecurityGroups(await PagedAsync("ec2", region, "DescribeSecurityGroups", "2016-11-15", "NextToken", "nextToken", ct), region, snap));
            await Part(snap, region, "VPCs", ["network"], async () => ParseVpcs(await PagedAsync("ec2", region, "DescribeVpcs", "2016-11-15", "NextToken", "nextToken", ct), region, snap));
            await Part(snap, region, "RDS databases", ["database"], async () => ParseDatabases(await PagedAsync("rds", region, "DescribeDBInstances", "2014-10-31", "Marker", "Marker", ct), region, snap));
            await Part(snap, region, "load balancers", ["load_balancer"], async () => ParseLoadBalancers(await PagedAsync("elasticloadbalancing", region, "DescribeLoadBalancers", "2015-12-01", "Marker", "NextMarker", ct), region, snap));
            await Part(snap, region, "Lambda functions", ["function"], async () => await LambdaAsync(region, snap, ct));
            await Part(snap, region, "EKS clusters", ["kubernetes"], async () => await EksAsync(region, snap, ct));
        }
        await Part(snap, "global", "S3 buckets", ["storage"], async () =>
        {
            var doc = await RestAsync("s3", "us-east-1", "/", [], ct);
            foreach (var b in Descendants(doc.Root, "Bucket"))
            {
                var name = V(b, "Name")!;
                var item = new CloudItem { Kind = "storage", ExternalId = $"arn:aws:s3:::{name}", Name = name, Os = "Amazon S3" };
                if (DateTimeOffset.TryParse(V(b, "CreationDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c)) item.CreatedAt = c;
                snap.Items.Add(item);
            }
        });
        return snap;
    }

    private static async Task Part(CloudSnapshot snap, string region, string what, string[] kinds, Func<Task> read)
    {
        try { await read(); }
        catch (AwsException ex) when (!ex.Fatal)
        {
            snap.Warn($"{region}: {what} not read — {ex.Message}", kinds);
        }
    }

    // ---------- parsing (pure; unit tested) ----------

    internal static IEnumerable<XElement> Items(XElement? parent, string set) =>
        parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == set)?.Elements().Where(e => e.Name.LocalName is "item" or "member") ?? [];
    internal static IEnumerable<XElement> Descendants(XElement? parent, string name) => parent?.Descendants().Where(e => e.Name.LocalName == name) ?? [];
    internal static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    internal static string? V(XElement? e, string name) => e is null ? null : (e.Name.LocalName == name ? e : Child(e, name) ?? e.Descendants().FirstOrDefault(x => x.Name.LocalName == name))?.Value is { Length: > 0 } v ? v : null;
    private static IEnumerable<XElement> SetItems(XElement? e, string set) => Child(e, set)?.Elements().Where(x => x.Name.LocalName is "item" or "member" or "Tag") ?? [];
    private static Dictionary<string, string> Tags(XElement e) =>
        SetItems(e, "tagSet").Concat(SetItems(e, "TagList")).Where(t => V(t, "key") ?? V(t, "Key") is not null)
            .GroupBy(t => (V(t, "key") ?? V(t, "Key"))!).ToDictionary(g => g.Key, g => V(g.First(), "value") ?? V(g.First(), "Value") ?? "", StringComparer.OrdinalIgnoreCase);

    internal static void ParseInstances(IEnumerable<XDocument> pages, string region, CloudSnapshot snap)
    {
        foreach (var doc in pages)
            foreach (var res in SetItems(doc.Root, "reservationSet"))
                foreach (var i in SetItems(res, "instancesSet"))
                {
                    var tags = Tags(i);
                    var state = V(Child(i, "instanceState"), "name");
                    var item = new CloudItem
                    {
                        Kind = "vm", ExternalId = V(i, "instanceId")!, Name = tags.GetValueOrDefault("Name") ?? V(i, "instanceId"), Region = region,
                        Group = V(i, "vpcId"), State = state, Size = V(i, "instanceType"),
                        Os = V(i, "platformDetails") ?? (V(i, "platform") == "windows" ? "Windows" : "Linux/UNIX"),
                        Hostname = V(i, "privateDnsName") is { } dns ? dns.Split('.')[0] : null, PrivateIp = V(i, "privateIpAddress"), PublicIp = V(i, "ipAddress"),
                        Tags = tags,
                    };
                    if (int.TryParse(V(Child(i, "cpuOptions"), "coreCount"), out var cores)) item.Cpu = cores * (int.TryParse(V(Child(i, "cpuOptions"), "threadsPerCore"), out var t) ? t : 1);
                    if (DateTimeOffset.TryParse(V(i, "launchTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lt)) item.Properties["launchTime"] = lt.ToString("o");
                    foreach (var n in SetItems(i, "networkInterfaceSet")) if (V(n, "macAddress") is { } mac) item.Macs.Add(AzureCollector.NormalizeMac(mac));
                    item.Properties["availabilityZone"] = V(Child(i, "placement"), "availabilityZone");
                    item.Properties["imageId"] = V(i, "imageId");
                    // "User initiated (2024-05-01 10:22:13 GMT)" tells since when a stopped instance is stopped.
                    if (state == "stopped" && V(i, "reason") is { } reason && System.Text.RegularExpressions.Regex.Match(reason, @"\((\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) GMT\)") is { Success: true } m
                        && DateTimeOffset.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since))
                        item.StateSince = since;
                    item.Properties["volumes"] = SetItems(i, "blockDeviceMapping").Select(b => V(b, "volumeId")).OfType<string>().ToList();
                    snap.Items.Add(item);
                }
    }

    internal static void ParseVolumes(IEnumerable<XDocument> pages, string region, CloudSnapshot snap)
    {
        foreach (var doc in pages)
            foreach (var v in SetItems(doc.Root, "volumeSet"))
            {
                var tags = Tags(v);
                var item = new CloudItem
                {
                    Kind = "disk", ExternalId = V(v, "volumeId")!, Name = tags.GetValueOrDefault("Name") ?? V(v, "volumeId"), Region = region,
                    State = V(v, "status"), Size = V(v, "volumeType"), Tags = tags,
                    ParentId = SetItems(v, "attachmentSet").Select(a => V(a, "instanceId")).FirstOrDefault(),
                };
                if (decimal.TryParse(V(v, "size"), CultureInfo.InvariantCulture, out var gb)) item.DiskGb = gb;
                item.Properties["attached"] = V(v, "status") != "available";
                item.Properties["encrypted"] = V(v, "encrypted") == "true";
                if (DateTimeOffset.TryParse(V(v, "createTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c)) item.CreatedAt = c;
                snap.Items.Add(item);
            }
    }

    internal static void ParseAddresses(IEnumerable<XDocument> pages, string region, CloudSnapshot snap)
    {
        foreach (var doc in pages)
            foreach (var a in SetItems(doc.Root, "addressesSet"))
            {
                var tags = Tags(a);
                var item = new CloudItem
                {
                    Kind = "public_ip", ExternalId = V(a, "allocationId") ?? V(a, "publicIp")!, Name = tags.GetValueOrDefault("Name") ?? V(a, "publicIp"),
                    Region = region, PublicIp = V(a, "publicIp"), PrivateIp = V(a, "privateIpAddress"), ParentId = V(a, "instanceId"), Tags = tags,
                    State = V(a, "associationId") is null ? "unassociated" : "associated",
                };
                item.Properties["associated"] = V(a, "associationId") is not null;
                snap.Items.Add(item);
            }
    }

    internal static void ParseSecurityGroups(IEnumerable<XDocument> pages, string region, CloudSnapshot snap)
    {
        foreach (var doc in pages)
            foreach (var g in SetItems(doc.Root, "securityGroupInfo"))
            {
                var open = new List<int>();
                var rules = new List<string>();
                foreach (var p in SetItems(g, "ipPermissions"))
                {
                    var anywhere = SetItems(p, "ipRanges").Any(r => V(r, "cidrIp") == "0.0.0.0/0") || SetItems(p, "ipv6Ranges").Any(r => V(r, "cidrIpv6") == "::/0");
                    if (!anywhere) continue;
                    var proto = V(p, "ipProtocol");
                    List<int> ports;
                    if (proto == "-1") ports = [0];
                    else if (proto is "tcp" or "6" && int.TryParse(V(p, "fromPort"), out var from) && int.TryParse(V(p, "toPort"), out var to))
                        ports = AzureCollector.OpenAdminPorts(from == to ? $"{from}" : $"{from}-{to}").ToList();
                    else ports = [];
                    if (ports.Count == 0) continue;
                    open.AddRange(ports);
                    rules.Add(proto == "-1" ? "all traffic from anywhere" : $"{proto} {V(p, "fromPort")}-{V(p, "toPort")} from anywhere");
                }
                var item = new CloudItem
                {
                    Kind = "security_group", ExternalId = V(g, "groupId")!, Name = V(g, "groupName"), Region = region, Group = V(g, "vpcId"), Tags = Tags(g),
                };
                item.Properties["openPorts"] = open.Distinct().ToList();
                item.Properties["openRules"] = rules;
                item.Properties["description"] = V(g, "groupDescription");
                snap.Items.Add(item);
            }
    }

    internal static void ParseVpcs(IEnumerable<XDocument> pages, string region, CloudSnapshot snap)
    {
        foreach (var doc in pages)
            foreach (var v in SetItems(doc.Root, "vpcSet"))
            {
                var tags = Tags(v);
                var item = new CloudItem { Kind = "network", ExternalId = V(v, "vpcId")!, Name = tags.GetValueOrDefault("Name") ?? V(v, "vpcId"), Region = region, State = V(v, "state"), Size = V(v, "cidrBlock"), Tags = tags };
                item.Properties["default"] = V(v, "isDefault") == "true";
                snap.Items.Add(item);
            }
    }

    internal static void ParseDatabases(IEnumerable<XDocument> pages, string region, CloudSnapshot snap)
    {
        foreach (var doc in pages)
            foreach (var d in Descendants(doc.Root, "DBInstance"))
            {
                var item = new CloudItem
                {
                    Kind = "database", ExternalId = V(d, "DBInstanceArn") ?? V(d, "DBInstanceIdentifier")!, Name = V(d, "DBInstanceIdentifier"), Region = region,
                    State = V(d, "DBInstanceStatus"), Size = V(d, "DBInstanceClass"), Os = $"{V(d, "Engine")} {V(d, "EngineVersion")}".Trim(),
                    Hostname = V(Child(d, "Endpoint"), "Address"), Group = V(Child(d, "DBSubnetGroup"), "VpcId"), Tags = Tags(d),
                };
                if (decimal.TryParse(V(d, "AllocatedStorage"), CultureInfo.InvariantCulture, out var gb)) item.DiskGb = gb;
                item.Properties["publiclyAccessible"] = V(d, "PubliclyAccessible") == "true";
                item.Properties["encrypted"] = V(d, "StorageEncrypted") == "true";
                item.Properties["multiAz"] = V(d, "MultiAZ") == "true";
                if (DateTimeOffset.TryParse(V(d, "InstanceCreateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c)) item.CreatedAt = c;
                snap.Items.Add(item);
            }
    }

    internal static void ParseLoadBalancers(IEnumerable<XDocument> pages, string region, CloudSnapshot snap)
    {
        foreach (var doc in pages)
            foreach (var lb in Descendants(doc.Root, "LoadBalancers").SelectMany(x => x.Elements().Where(e => e.Name.LocalName == "member")))
            {
                var item = new CloudItem
                {
                    Kind = "load_balancer", ExternalId = V(lb, "LoadBalancerArn")!, Name = V(lb, "LoadBalancerName"), Region = region, Group = V(lb, "VpcId"),
                    State = V(Child(lb, "State"), "Code"), Size = V(lb, "Type"), Hostname = V(lb, "DNSName"),
                };
                item.Properties["scheme"] = V(lb, "Scheme");
                if (DateTimeOffset.TryParse(V(lb, "CreatedTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c)) item.CreatedAt = c;
                snap.Items.Add(item);
            }
    }

    internal static void ParseFunctions(JsonElement root, string region, CloudSnapshot snap)
    {
        foreach (var f in root.GetProperty("Functions").EnumerateArray())
        {
            string? S(string n) => f.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var item = new CloudItem { Kind = "function", ExternalId = S("FunctionArn")!, Name = S("FunctionName"), Region = region, Os = S("Runtime"), State = S("State") ?? "Active" };
            if (f.TryGetProperty("MemorySize", out var mem) && mem.ValueKind == JsonValueKind.Number) item.MemoryMb = mem.GetInt64();
            snap.Items.Add(item);
        }
    }

    // ---------- requests ----------

    private async Task LambdaAsync(string region, CloudSnapshot snap, CancellationToken ct)
    {
        string? marker = null;
        do
        {
            var q = new List<KeyValuePair<string, string>> { new("MaxItems", "50") };
            if (marker is not null) q.Add(new("Marker", marker));
            using var doc = await RestJsonAsync("lambda", region, "/2015-03-31/functions/", q, ct);
            ParseFunctions(doc.RootElement, region, snap);
            marker = doc.RootElement.TryGetProperty("NextMarker", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        } while (marker is not null);
    }

    private async Task EksAsync(string region, CloudSnapshot snap, CancellationToken ct)
    {
        using var doc = await RestJsonAsync("eks", region, "/clusters", [], ct);
        foreach (var name in doc.RootElement.GetProperty("clusters").EnumerateArray().Select(x => x.GetString()).OfType<string>())
        {
            var item = new CloudItem { Kind = "kubernetes", ExternalId = $"eks:{region}:{name}", Name = name, Region = region };
            try
            {
                using var d = await RestJsonAsync("eks", region, $"/clusters/{AwsSigV4.Encode(name)}", [], ct);
                var c = d.RootElement.GetProperty("cluster");
                item.State = c.TryGetProperty("status", out var st) ? st.GetString() : null;
                item.Os = c.TryGetProperty("version", out var ver) ? "Kubernetes " + ver.GetString() : null;
                if (c.TryGetProperty("endpoint", out var ep)) item.Hostname = ep.GetString();
                if (c.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Object)
                    foreach (var t in tg.EnumerateObject()) item.Tags[t.Name] = t.Value.GetString() ?? "";
            }
            catch (AwsException) { /* list permission without describe: keep the name */ }
            snap.Items.Add(item);
        }
    }

    private async Task<List<XDocument>> PagedAsync(string service, string region, string action, string version, string tokenParam, string tokenElement, CancellationToken ct)
    {
        var pages = new List<XDocument>();
        string? token = null;
        do
        {
            var p = new List<KeyValuePair<string, string>>();
            if (service == "ec2" && action is "DescribeInstances" or "DescribeVolumes" or "DescribeSecurityGroups" or "DescribeVpcs") p.Add(new("MaxResults", "1000"));
            if (token is not null) p.Add(new(tokenParam, token));
            var doc = await QueryAsync(service, region, action, version, p, ct);
            pages.Add(doc);
            token = Descendants(doc.Root, tokenElement).FirstOrDefault()?.Value is { Length: > 0 } t ? t : null;
        } while (token is not null && pages.Count < 200);
        return pages;
    }

    /// <summary>AWS Query API (EC2, RDS, ELB, STS): form-encoded POST, XML answer.</summary>
    private async Task<XDocument> QueryAsync(string service, string region, string action, string version, List<KeyValuePair<string, string>> parameters, CancellationToken ct)
    {
        var body = AwsSigV4.CanonicalQuery(parameters.Prepend(new("Version", version)).Prepend(new("Action", action)));
        var text = await SendAsync(HttpMethod.Post, service, region, "/", "", body, "application/x-www-form-urlencoded; charset=utf-8", ct);
        return XDocument.Parse(text);
    }

    private async Task<XDocument> RestAsync(string service, string region, string path, List<KeyValuePair<string, string>> query, CancellationToken ct) =>
        XDocument.Parse(await SendAsync(HttpMethod.Get, service, region, path, AwsSigV4.CanonicalQuery(query), null, null, ct));

    private async Task<JsonDocument> RestJsonAsync(string service, string region, string path, List<KeyValuePair<string, string>> query, CancellationToken ct) =>
        JsonDocument.Parse(await SendAsync(HttpMethod.Get, service, region, path, AwsSigV4.CanonicalQuery(query), null, null, ct));

    private async Task<string> SendAsync(HttpMethod method, string service, string region, string path, string query, string? body, string? contentType, CancellationToken ct)
    {
        var baseUrl = _endpoint(service, region);
        var uri = new Uri(baseUrl + AwsSigV4.Encode(path, path: true) + (query.Length > 0 ? "?" + query : ""));
        for (var attempt = 1; ; attempt++)
        {
            var now = DateTime.UtcNow;
            var payloadHash = AwsSigV4.Sha256Hex(body ?? "");
            var headers = new Dictionary<string, string> { ["host"] = uri.Authority, ["x-amz-date"] = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) };
            if (contentType is not null) headers["content-type"] = contentType;
            if (service == "s3") headers["x-amz-content-sha256"] = payloadHash;
            var signingRegion = service is "sts" or "s3" ? "us-east-1" : region;
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
            if (service == "s3") req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            req.Headers.TryAddWithoutValidation("Authorization", AwsSigV4.Authorization(method.Method, AwsSigV4.Encode(path, path: true), query, headers, payloadHash, _key, _secret, signingRegion, service, now));
            if (body is not null)
            {
                req.Content = new StringContent(body, Encoding.UTF8);
                req.Content.Headers.Remove("Content-Type");
                req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType!);
            }
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new AwsException(CloudHttp.Unreachable(uri.Host, ex), fatal: service == "sts");
            }
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode) return text;
                var (code, message) = ParseError(text);
                if ((resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable || code is "Throttling" or "RequestLimitExceeded" or "ThrottlingException") && attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
                    continue;
                }
                var fatal = code is "InvalidClientTokenId" or "SignatureDoesNotMatch" or "AuthFailure" or "IncompleteSignature" or "ExpiredToken" || service == "sts";
                var explained = code switch
                {
                    "InvalidClientTokenId" => "the access key ID is not valid (deleted or mistyped)",
                    "SignatureDoesNotMatch" => "the secret access key is wrong",
                    "AuthFailure" => "AWS did not accept the key",
                    "UnauthorizedOperation" or "AccessDenied" or "AccessDeniedException" => "permission missing; attach the ReadOnlyAccess policy to the IAM user",
                    "OptInRequired" => "the region is not enabled for the account",
                    _ => message ?? $"HTTP {(int)resp.StatusCode}",
                };
                throw new AwsException(fatal ? $"AWS sign-in failed: {explained}." : explained, fatal);
            }
        }
    }

    internal static (string? Code, string? Message) ParseError(string text)
    {
        try
        {
            if (text.TrimStart().StartsWith('{'))
            {
                using var j = JsonDocument.Parse(text);
                var code = j.RootElement.TryGetProperty("__type", out var t) ? t.GetString() : j.RootElement.TryGetProperty("code", out var c2) ? c2.GetString() : null;
                var msg = j.RootElement.TryGetProperty("message", out var m) ? m.GetString() : j.RootElement.TryGetProperty("Message", out var m2) ? m2.GetString() : null;
                return (code?.Split('#').Last().Split(':')[0], msg);
            }
            var x = XDocument.Parse(text);
            return (V(x.Root, "Code"), V(x.Root, "Message"));
        }
        catch (Exception) { return (null, text.Length > 200 ? text[..200] : text); }
    }

    private sealed class AwsException(string message, bool fatal) : CloudException(message)
    {
        public bool Fatal { get; } = fatal;
    }
}
