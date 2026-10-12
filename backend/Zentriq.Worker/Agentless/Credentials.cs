using System.Net;
using System.Text.Json;
using Npgsql;
using Zentriq.Application.Discovery;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Worker.Agentless;

/// <summary>A decrypted credential, held in memory only for the duration of a scan.</summary>
public sealed record ScanCredential(Guid Id, string Name, string Kind, string? Username, CredentialSecret Secret, JsonElement Options, IReadOnlyList<string> AllowedScopes)
{
    public bool IsWindows => Kind is "windows_domain" or "windows_local";
    public bool IsSsh => Kind is "ssh_password" or "ssh_key";
    public bool IsSnmp => Kind is "snmp_v2c" or "snmp_v3";
    public bool IsLdap => Kind is "ldap_bind" or "windows_domain";

    /// <summary>A credential may only be sent to targets inside its allowed scope (empty = scan scopes).</summary>
    public bool AllowedFor(IPAddress ip) => AllowedScopes.Count == 0 || TargetParser.AddressInScope(ip, AllowedScopes);

    public int Int(string name, int fallback) =>
        Options.ValueKind == JsonValueKind.Object && Options.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;
    public string? Str(string name) =>
        Options.ValueKind == JsonValueKind.Object && Options.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public bool Bool(string name) =>
        Options.ValueKind == JsonValueKind.Object && Options.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Splits DOMAIN\user and user@domain.</summary>
    public (string? Domain, string User) SplitUser()
    {
        var u = Username ?? "";
        var i = u.IndexOf('\\');
        if (i > 0) return (u[..i], u[(i + 1)..]);
        return (Str("domain"), u);
    }

    public static async Task<List<ScanCredential>> LoadAsync(NpgsqlDataSource ds, CredentialProtector protector, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = new List<ScanCredential>();
        if (ids.Count == 0) return list;
        await using var cmd = ds.CreateCommand("SELECT id, name, kind, username, secret_protected, options::text, allowed_scope FROM zentriq.credentials WHERE id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetGuid(0);
            var scopes = r.IsDBNull(6) ? [] : r.GetString(6).Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            list.Add(new ScanCredential(id, r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                protector.Unprotect(id, r.GetString(4)), JsonDocument.Parse(r.GetString(5)).RootElement.Clone(), scopes));
        }
        // Keep the order the user chose.
        return ids.Select(i => list.FirstOrDefault(c => c.Id == i)).Where(c => c is not null).Select(c => c!).ToList();
    }

    public static async Task MarkUsedAsync(NpgsqlDataSource ds, Guid id)
    {
        try
        {
            await using var cmd = ds.CreateCommand("UPDATE zentriq.credentials SET last_used_at = now() WHERE id = @id");
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException) { /* best effort */ }
    }
}

/// <summary>Outcome of trying to inventory one target with the available credentials.</summary>
public sealed record CollectionAttempt(string Outcome, Zentriq.Inventory.InventoryDocument? Document, string? Protocol, Guid? CredentialId, string Detail)
{
    public const string Success = "success", AuthFailed = "auth_failed", Failed = "inventory_failed", NotAttempted = "none";
    public static CollectionAttempt None(string detail) => new(NotAttempted, null, null, null, detail);
}
