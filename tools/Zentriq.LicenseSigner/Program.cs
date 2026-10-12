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
//           --entitle endpoint=500 --entitle server=50 [--modules *|inventory,discovery,...]
//           [--license-id <id>] --out <license.zlic>
//           Licensee details (schema 2, shown to the customer): [--company <legal name>] [--contact-name ..]
//           [--contact-email ..] [--contact-phone ..] [--address ..] [--country ..] [--tax-id ..]
//           A total cap across all device types can be given with --entitle devices=<n>.
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
            if (!Workloads.IsLimitKey(parts[0])) throw new ArgumentException($"Unknown workload '{parts[0]}'. Known: {string.Join(", ", Workloads.All)}, {Workloads.TotalDevices}.");
            entitlements[parts[0]] = qty;
        }
        if (entitlements.Count == 0) throw new ArgumentException("At least one --entitle is required.");

        var modules = (Opt(o, "modules") ?? "*").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var m in modules) if (m != "*" && !Modules.All.Contains(m)) throw new ArgumentException($"Unknown module '{m}'.");

        var now = DateTimeOffset.UtcNow;
        var customer = One(o, "customer");
        var licensee = new Licensee(Opt(o, "company") ?? customer, Opt(o, "contact-name"), Opt(o, "contact-email"),
            Opt(o, "contact-phone"), Opt(o, "address"), Opt(o, "country"), Opt(o, "tax-id"));
        var payload = new LicensePayload(
            SchemaVersion: LicenseFormats.CurrentSchemaVersion,
            LicenseId: Opt(o, "license-id") ?? $"ZQ-{now:yyyyMMdd}-{RandomNumberGenerator.GetHexString(8)}",
            Customer: customer,
            InstallationId: request.InstallationId,
            IssuedAt: now,
            NotBefore: Opt(o, "not-before") is { } nb ? DateTimeOffset.Parse(nb + "T00:00:00Z") : now.AddMinutes(-5),
            ExpiresAt: DateTimeOffset.Parse(One(o, "expires") + "T23:59:59Z"),
            GraceDays: int.Parse(Opt(o, "grace") ?? "14"),
            Modules: modules,
            Entitlements: entitlements,
            RequestId: request.RequestId,
            Licensee: licensee);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(One(o, "key")));
        if (key.KeySize != 256) throw new CryptographicException("Signing key must be ECDSA P-256.");
        var signature = key.SignData(payloadBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var doc = new SignedDocument(LicenseFormats.License, One(o, "key-id"), LicenseFormats.Algorithm,
            Convert.ToBase64String(payloadBytes), Convert.ToBase64String(signature));
        var outFile = One(o, "out");
        File.WriteAllText(outFile, JsonSerializer.Serialize(doc, Pretty));
        Console.WriteLine($"Issued {payload.LicenseId} for {payload.Customer} (installation {payload.InstallationId}), expires {payload.ExpiresAt:yyyy-MM-dd}: {outFile}");
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
