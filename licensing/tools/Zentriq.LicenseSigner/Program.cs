using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;

// zentriq-license-signer — VENDOR tool for offline license issuance. Not shipped to customers.
//
//   keygen  --key-id <id> --out <dir>
//       Creates an ECDSA P-256 key pair: <id>.private.pem (keep offline / HSM) and <id>.public.pem
//       (add to backend/Zentriq.Infrastructure/Resources/license-public-keys.json and release a build).
//
//   sign    --key <private.pem> --key-id <id> --request <activation-request.json>
//           --customer <name> --expires <yyyy-mm-dd> [--not-before <yyyy-mm-dd>] [--grace <days>]
//           --entitle endpoint=500 --entitle server=50 [--edition standard|advanced] [--modules *|inventory,discovery,...]
//           (--edition alone licenses that edition's modules; --modules adds more; without either: all modules)
//           [--license-id <id>] --out <license.zlic>
//           Licensee details (schema 2, shown to the customer): [--company <legal name>] [--contact-name ..]
//           [--contact-email ..] [--contact-phone ..] [--address ..] [--country ..] [--tax-id ..]
//           Metrics: --entitle devices=<IT devices> --entitle nonit_assets=<250|500|1000|2000|4000> (leave out = unlimited)
//           --entitle cloud_instances=<n> --entitle technicians=<n>
//           Schema 3: [--type subscription|perpetual] [--years 1|3|5] [--support-until <yyyy-mm-dd>] (perpetual: --expires is ignored)
//
//   inspect --license <file.zlic> --public <public.pem>
//       Verifies the signature and prints the payload.
return Signer.Run(args);

internal static class Signer
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static int Run(string[] argv)
    {
        try
        {
            if (argv.Length == 0) { Console.Error.WriteLine("Usage: zentriq-license-signer keygen|sign|inspect [options]  (see source header)"); return 2; }
            var opts = Parse(argv.Skip(1).ToArray());
            return argv[0] switch
            {
                "keygen" => KeyGen(opts),
                "sign" => Sign(opts),
                "inspect" => Inspect(opts),
                _ => throw new ArgumentException($"Unknown command {argv[0]}"),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or CryptographicException or JsonException)
        {
            Console.Error.WriteLine($"ERROR {ex.Message}");
            return 1;
        }
    }

    private static int KeyGen(Dictionary<string, List<string>> o)
    {
        var id = One(o, "key-id");
        var dir = One(o, "out");
        Directory.CreateDirectory(dir);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var priv = Path.Combine(dir, $"{id}.private.pem");
        if (File.Exists(priv)) throw new IOException($"{priv} already exists; refusing to overwrite a signing key.");
        File.WriteAllText(priv, key.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(priv, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(Path.Combine(dir, $"{id}.public.pem"), key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine($"Created {priv} (PRIVATE - store offline) and {id}.public.pem");
        return 0;
    }

    private static int Sign(Dictionary<string, List<string>> o)
    {
        var request = JsonSerializer.Deserialize<ActivationRequest>(File.ReadAllText(One(o, "request")))
                      ?? throw new FormatException("Activation request is empty.");
        if (request.Format != LicenseFormats.ActivationRequest) throw new FormatException("Not a Zentriq activation request.");
        if (request.InstallationId == Guid.Empty) throw new FormatException("Activation request has no installation ID.");

        var entitlements = new Dictionary<string, int>();
        foreach (var e in o.GetValueOrDefault("entitle") ?? [])
        {
            var parts = e.Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var qty) || qty < 0) throw new ArgumentException($"Bad --entitle '{e}' (use workload=quantity).");
            if (!Workloads.IsLimitKey(parts[0])) throw new ArgumentException($"Unknown metric '{parts[0]}'. Known: {string.Join(", ", Workloads.Metrics.Concat(Workloads.All))}.");
            entitlements[parts[0]] = qty;
        }
        if (entitlements.Count == 0) throw new ArgumentException("At least one --entitle is required.");

        var edition = Opt(o, "edition")?.Trim().ToLowerInvariant();
        if (edition is not null && Editions.ModulesOf(edition).Count == 0) throw new ArgumentException($"Unknown edition '{edition}' (standard or advanced).");
        var modules = (Opt(o, "modules") ?? (edition is null ? "*" : "")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var m in modules) if (m != "*" && !Modules.All.Contains(m)) throw new ArgumentException($"Unknown module '{m}'.");

        var now = DateTimeOffset.UtcNow;
        var customer = One(o, "customer");
        var type = (Opt(o, "type") ?? LicenseFormats.Subscription).ToLowerInvariant();
        if (type is not (LicenseFormats.Subscription or LicenseFormats.Perpetual)) throw new ArgumentException("--type is subscription or perpetual.");
        var perpetual = type == LicenseFormats.Perpetual;
        DateTimeOffset? supportUntil = Opt(o, "support-until") is { } su ? DateTimeOffset.Parse(su + "T23:59:59Z") : null;
        if (perpetual && supportUntil is null) throw new ArgumentException("A perpetual license needs --support-until.");
        var expires = perpetual ? LicenseFormats.PerpetualExpiry : DateTimeOffset.Parse(One(o, "expires") + "T23:59:59Z");
        var licensee = new Licensee(Opt(o, "company") ?? customer, Opt(o, "contact-name"), Opt(o, "contact-email"),
            Opt(o, "contact-phone"), Opt(o, "address"), Opt(o, "country"), Opt(o, "tax-id"));
        var payload = new LicensePayload(
            SchemaVersion: LicenseFormats.CurrentSchemaVersion,
            LicenseId: Opt(o, "license-id") ?? $"ZQ-{now:yyyyMMdd}-{RandomNumberGenerator.GetHexString(8)}",
            Customer: customer,
            InstallationId: request.InstallationId,
            IssuedAt: now,
            NotBefore: Opt(o, "not-before") is { } nb ? DateTimeOffset.Parse(nb + "T00:00:00Z") : now.AddMinutes(-5),
            ExpiresAt: expires,
            GraceDays: perpetual ? 0 : int.Parse(Opt(o, "grace") ?? "14"),
            Modules: modules,
            Entitlements: entitlements,
            RequestId: request.RequestId,
            Licensee: licensee,
            Edition: edition,
            LicenseType: type,
            TermYears: perpetual ? null : (Opt(o, "years") is { } y ? int.Parse(y) : null),
            SupportUntil: perpetual ? supportUntil : expires,
            Activation: LicenseFormats.Offline);

        using var key = Zentriq.Licensing.Signing.LicenseSigning.LoadPrivateKey(File.ReadAllText(One(o, "key")));
        var outFile = One(o, "out");
        File.WriteAllText(outFile, Zentriq.Licensing.Signing.LicenseSigning.Sign(payload, key, One(o, "key-id")));
        Console.WriteLine($"Issued {payload.LicenseId} for {payload.Customer} (installation {payload.InstallationId}), {type}{(perpetual ? $", support until {supportUntil:yyyy-MM-dd}" : $", expires {payload.ExpiresAt:yyyy-MM-dd}")}: {outFile}");
        return 0;
    }

    private static int Inspect(Dictionary<string, List<string>> o)
    {
        var doc = JsonSerializer.Deserialize<SignedDocument>(File.ReadAllText(One(o, "license"))) ?? throw new FormatException("Empty license.");
        var payload = Convert.FromBase64String(doc.Payload);
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(One(o, "public")));
        var ok = key.VerifyData(payload, Convert.FromBase64String(doc.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Console.WriteLine($"Signature: {(ok ? "VALID" : "INVALID")} (key {doc.KeyId}, {doc.Algorithm})");
        Console.WriteLine(JsonSerializer.Serialize(JsonDocument.Parse(payload), Pretty));
        return ok ? 0 : 1;
    }

    private static Dictionary<string, List<string>> Parse(string[] a)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= a.Length) throw new ArgumentException($"Unexpected argument '{a[i]}'.");
            var k = a[i][2..];
            if (!d.TryGetValue(k, out var list)) d[k] = list = [];
            list.Add(a[++i]);
        }
        return d;
    }

    private static string One(Dictionary<string, List<string>> o, string k) => Opt(o, k) ?? throw new ArgumentException($"--{k} is required.");
    private static string? Opt(Dictionary<string, List<string>> o, string k) => o.TryGetValue(k, out var v) ? v[^1] : null;
}
