using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Lifecycle.Warranty;

public sealed class WarrantySettings
{
    /// <summary>Look up new devices and refresh the others in the background.</summary>
    public bool Auto { get; set; } = true;
    /// <summary>Days after which a found warranty is checked again (extensions bought later appear).</summary>
    public int RefreshDays { get; set; } = 30;
    /// <summary>Replace warranty dates entered by hand with the manufacturer's.</summary>
    public bool OverwriteManual { get; set; }
}

public sealed record WarrantyProviderInfo(string Vendor, string Label, bool Enabled, bool Configured, string? ClientId, string? ApiUrl, string DefaultUrl,
    DateTimeOffset? LastOkAt, string? LastError, DateTimeOffset? LastErrorAt, DateTimeOffset? UpdatedAt, string? UpdatedBy);

public sealed record WarrantyRunSummary(int Checked, int Found, int NotFound, int Errors, int NoKey, int Unsupported, List<string> Messages);

/// <summary>
/// Online warranty lookup: reads the warranty of Dell, Lenovo and HP devices from the manufacturer by serial number
/// (HP also by product number), keeps the answer per device (warranty_lookups) and fills the warranty of the asset's
/// lifecycle record (provider, type of cover, start and end) unless dates were entered by hand.
/// </summary>
public sealed class WarrantyService(NpgsqlDataSource ds, CredentialProtector protector, HttpMessageHandler? handler = null)
{
    public const string SettingsKey = "warranty.settings";
    public const string RequestedKey = "warranty.requested";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The id the vendor's secret is bound to (associated data of the encryption).</summary>
    public static Guid SecretId(string vendor) => new(MD5.HashData(Encoding.UTF8.GetBytes("zentriq-warranty:" + vendor)));

    public async Task<WarrantySettings> SettingsAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT value::text FROM zentriq.system_settings WHERE key = '{SettingsKey}'");
        return await cmd.ExecuteScalarAsync(ct) is string s ? JsonSerializer.Deserialize<WarrantySettings>(s, Json) ?? new() : new();
    }

    public async Task SaveSettingsAsync(WarrantySettings s, string? by, CancellationToken ct)
    {
        s.RefreshDays = Math.Clamp(s.RefreshDays, 7, 365);
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES (@k, @v::jsonb, now())
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("k", SettingsKey);
        cmd.Parameters.AddWithValue("v", JsonSerializer.Serialize(s, Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<WarrantyProviderInfo>> ProvidersAsync(CancellationToken ct)
    {
        var list = new List<WarrantyProviderInfo>();
        var rows = new Dictionary<string, WarrantyProviderInfo>();
        await using (var cmd = ds.CreateCommand("SELECT vendor, enabled, client_id, secret_protected IS NOT NULL, api_url, last_ok_at, last_error, last_error_at, updated_at, updated_by FROM zentriq.warranty_providers"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var v = r.GetString(0);
                var clientId = r.IsDBNull(2) ? null : r.GetString(2);
                var configured = !string.IsNullOrEmpty(clientId) && (v == "lenovo" || r.GetBoolean(3));
                rows[v] = new WarrantyProviderInfo(v, WarrantyClients.Label(v), r.GetBoolean(1), configured, clientId, r.IsDBNull(4) ? null : r.GetString(4), WarrantyClients.DefaultUrl(v),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
                    r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetString(9));
            }
        foreach (var v in WarrantyClients.Vendors)
            list.Add(rows.TryGetValue(v, out var p) ? p : new WarrantyProviderInfo(v, WarrantyClients.Label(v), true, false, null, null, WarrantyClients.DefaultUrl(v), null, null, null, null, null));
        return list;
    }

    /// <summary>Saves a vendor key. A null <paramref name="secret"/> keeps the stored one; an empty client id removes the key.</summary>
    public async Task SaveProviderAsync(string vendor, bool enabled, string? clientId, string? secret, string? apiUrl, string? by, CancellationToken ct)
    {
        string? prot = secret is { Length: > 0 } ? protector.Protect(SecretId(vendor), new CredentialSecret { Password = secret }) : null;
        var remove = string.IsNullOrWhiteSpace(clientId);
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.warranty_providers AS p (vendor, enabled, client_id, secret_protected, api_url, updated_at, updated_by)
            VALUES (@v, @e, @c, @s, @u, now(), @by)
            ON CONFLICT (vendor) DO UPDATE SET enabled = EXCLUDED.enabled, client_id = EXCLUDED.client_id,
                   secret_protected = CASE WHEN @remove THEN NULL ELSE COALESCE(EXCLUDED.secret_protected, p.secret_protected) END,
                   api_url = EXCLUDED.api_url, updated_at = now(), updated_by = EXCLUDED.updated_by, last_error = NULL, last_error_at = NULL
            """);
        cmd.Parameters.AddWithValue("v", vendor);
        cmd.Parameters.AddWithValue("e", enabled);
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = remove ? DBNull.Value : clientId!.Trim() });
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)prot ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(apiUrl) ? DBNull.Value : apiUrl.Trim() });
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Text) { Value = (object?)by ?? DBNull.Value });
        cmd.Parameters.AddWithValue("remove", remove);
        await cmd.ExecuteNonQueryAsync(ct);
        // Devices that could not be looked up for want of a key are looked up at the next run.
        await using var clear = ds.CreateCommand("DELETE FROM zentriq.warranty_lookups WHERE vendor = @v AND status IN ('no_key','error')");
        clear.Parameters.AddWithValue("v", vendor);
        await clear.ExecuteNonQueryAsync(ct);
    }

    private async Task<WarrantyProviderConfig?> ConfigAsync(string vendor, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT client_id, secret_protected, api_url FROM zentriq.warranty_providers WHERE vendor = @v AND enabled AND client_id IS NOT NULL");
        cmd.Parameters.AddWithValue("v", vendor);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string? secret = null;
        if (!r.IsDBNull(1)) secret = protector.Unprotect(SecretId(vendor), r.GetString(1)).Password;
        if (vendor != "lenovo" && string.IsNullOrEmpty(secret)) return null;
        return new WarrantyProviderConfig(vendor, r.GetString(0), secret, r.IsDBNull(2) ? null : r.GetString(2));
    }

    private HttpClient Http()
    {
        var http = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zentriq", "1.1"));
        return http;
    }

    /// <summary>Checks a vendor key with one serial number (a known device of the vendor when none is given).</summary>
    public async Task<(bool Ok, string Message)> TestAsync(string vendor, string? serial, CancellationToken ct)
    {
        var cfg = await ConfigAsync(vendor, ct);
        if (cfg is null) return (false, $"Enter the {WarrantyClients.Label(vendor)} API key first.");
        string? sku = null;
        if (string.IsNullOrWhiteSpace(serial))
        {
            foreach (var c in await CandidatesAsync(null, 500, false, ct))
                if (WarrantyClients.Detect(c.Manufacturer) == vendor) { serial = c.Serial; sku = c.Sku; break; }
        }
        var s = WarrantyClients.CleanSerial(serial);
        if (s is null) return (false, $"No {WarrantyClients.Label(vendor)} device with a serial number yet; enter a serial number to test.");
        using var http = Http();
        try
        {
            var answers = await WarrantyClients.LookupAsync(http, cfg, [(s, sku)], ct);
            await MarkProviderAsync(vendor, null, ct);
            if (!answers.TryGetValue(s, out var a) || !a.Found) return (true, $"The key works. {WarrantyClients.Label(vendor)} has no warranty for {s}{(a?.Message is { } m ? ": " + m : ".")}");
            return (true, $"The key works. {s}: {a.Product ?? "device"}, warranty until {a.End?.ToString("yyyy-MM-dd") ?? "unknown"}{(a.Latest?.Name is { } n ? $" ({n})" : "")}.");
        }
        catch (WarrantyApiException ex)
        {
            await MarkProviderAsync(vendor, ex.Message, ct);
            return (false, ex.Message);
        }
    }

    private async Task MarkProviderAsync(string vendor, string? error, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(error is null
            ? "UPDATE zentriq.warranty_providers SET last_ok_at = now(), last_error = NULL, last_error_at = NULL WHERE vendor = @v"
            : "UPDATE zentriq.warranty_providers SET last_error = @e, last_error_at = now() WHERE vendor = @v");
        cmd.Parameters.AddWithValue("v", vendor);
        if (error is not null) cmd.Parameters.AddWithValue("e", error.Length > 1000 ? error[..1000] : error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public sealed record Candidate(Guid AssetId, string? Manufacturer, string? Serial, string? Sku);

    /// <summary>
    /// Devices to look up: the given ones, or (background) Dell, Lenovo and HP devices never looked up, with a found
    /// warranty older than the refresh period, not found 30 days ago or failed a day ago.
    /// </summary>
    public async Task<List<Candidate>> CandidatesAsync(Guid[]? ids, int limit, bool dueOnly, CancellationToken ct)
    {
        var refresh = (await SettingsAsync(ct)).RefreshDays;
        await using var cmd = ds.CreateCommand("""
            SELECT v.asset_id, v.manufacturer, v.serial, d.document #>> '{device,sku}'
              FROM zentriq.v_asset_lifecycle v
              LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = v.asset_id
              LEFT JOIN zentriq.warranty_lookups w ON w.asset_id = v.asset_id
             WHERE v.serial IS NOT NULL
               AND (CASE WHEN @ids::uuid[] IS NOT NULL THEN v.asset_id = ANY(@ids)
                         ELSE v.stage <> 'disposed'
                              AND v.manufacturer ILIKE ANY (ARRAY['%dell%','%lenovo%','hp%','%hewlett%'])
                              AND (NOT @due OR w.asset_id IS NULL OR w.checked_at < now() - make_interval(days => CASE w.status WHEN 'found' THEN @refresh WHEN 'error' THEN 1 WHEN 'no_key' THEN 1 ELSE 30 END))
                    END)
             ORDER BY w.checked_at NULLS FIRST, v.asset_id
             LIMIT @l
            """);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = (object?)ids ?? DBNull.Value });
        cmd.Parameters.AddWithValue("due", dueOnly);
        cmd.Parameters.AddWithValue("refresh", refresh);
        cmd.Parameters.AddWithValue("l", limit);
        var list = new List<Candidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Candidate(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    /// <summary>Looks up the given devices now (from the console).</summary>
    public async Task<WarrantyRunSummary> LookupAsync(Guid[] ids, string? by, CancellationToken ct) =>
        await RunAsync(await CandidatesAsync(ids, Math.Min(ids.Length, 500), false, ct), explicitRequest: true, by, ct);

    /// <summary>Background run: devices due, at most <paramref name="limit"/>.</summary>
    public async Task<WarrantyRunSummary> RunDueAsync(int limit, CancellationToken ct) =>
        await RunAsync(await CandidatesAsync(null, limit, true, ct), explicitRequest: false, "Warranty lookup", ct);

    private async Task<WarrantyRunSummary> RunAsync(List<Candidate> candidates, bool explicitRequest, string? by, CancellationToken ct)
    {
        var sum = new WarrantyRunSummary(0, 0, 0, 0, 0, 0, []);
        int found = 0, notFound = 0, errors = 0, noKey = 0, unsupported = 0, checkedCount = 0;
        var settings = await SettingsAsync(ct);
        var byVendor = new Dictionary<string, List<(Candidate C, string Serial)>>();
        foreach (var c in candidates)
        {
            var vendor = WarrantyClients.Detect(c.Manufacturer);
            var serial = WarrantyClients.CleanSerial(c.Serial);
            if (vendor is null || serial is null) { unsupported++; continue; }
            if (!byVendor.TryGetValue(vendor, out var l)) byVendor[vendor] = l = [];
            l.Add((c, serial));
        }
        using var http = Http();
        foreach (var (vendor, items) in byVendor)
        {
            var cfg = await ConfigAsync(vendor, ct);
            if (cfg is null)
            {
                noKey += items.Count;
                sum.Messages.Add($"{items.Count} {WarrantyClients.Label(vendor)} device(s): no {WarrantyClients.Label(vendor)} API key (Asset Lifecycle › Settings).");
                if (explicitRequest)
                    foreach (var (c, s) in items) await SaveAsync(c.AssetId, vendor, s, "no_key", null, $"No {WarrantyClients.Label(vendor)} API key.", settings, by, ct);
                continue;
            }
            foreach (var batch in items.Chunk(WarrantyClients.BatchSize(vendor)))
            {
                Dictionary<string, WarrantyAnswer> answers;
                try
                {
                    answers = await WarrantyClients.LookupAsync(http, cfg, batch.Select(b => (b.Serial, b.C.Sku)).ToList(), ct);
                    await MarkProviderAsync(vendor, null, ct);
                }
                catch (WarrantyApiException ex)
                {
                    await MarkProviderAsync(vendor, ex.Message, ct);
                    errors += batch.Length;
                    if (!sum.Messages.Contains(ex.Message)) sum.Messages.Add(ex.Message);
                    foreach (var (c, s) in batch) await SaveAsync(c.AssetId, vendor, s, "error", null, ex.Message, settings, by, ct);
                    continue;
                }
                foreach (var (c, s) in batch)
                {
                    checkedCount++;
                    if (answers.TryGetValue(s, out var a) && a.Found && a.End is not null)
                    {
                        found++;
                        await SaveAsync(c.AssetId, vendor, s, "found", a, null, settings, by, ct);
                    }
                    else
                    {
                        notFound++;
                        await SaveAsync(c.AssetId, vendor, s, "not_found", a, a?.Message ?? $"{WarrantyClients.Label(vendor)} returned no warranty for this serial number.", settings, by, ct);
                    }
                }
            }
        }
        return sum with { Checked = checkedCount, Found = found, NotFound = notFound, Errors = errors, NoKey = noKey, Unsupported = unsupported };
    }

    private async Task SaveAsync(Guid assetId, string vendor, string serial, string status, WarrantyAnswer? a, string? message, WarrantySettings settings, string? by, CancellationToken ct)
    {
        var latest = a?.Latest;
        var ents = JsonSerializer.Serialize((a?.Entitlements ?? []).OrderByDescending(e => e.End).Take(50)
            .Select(e => new { name = e.Name, start = e.Start?.ToString("yyyy-MM-dd"), end = e.End?.ToString("yyyy-MM-dd"), type = e.Type }));
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var applied = false;
        if (status == "found" && a?.End is { } end)
        {
            await using var upd = new NpgsqlCommand("""
                UPDATE zentriq.asset_lifecycle
                   SET warranty_provider = @p, warranty_type = @t, warranty_start = COALESCE(@s, warranty_start), warranty_end = @e,
                       warranty_source = 'vendor', updated_at = now(), updated_by = @by, row_version = row_version + 1
                 WHERE asset_id = @a AND (@over OR warranty_source IS NULL OR warranty_source = 'vendor' OR warranty_end IS NULL)
                   AND (warranty_end IS DISTINCT FROM @e OR warranty_provider IS DISTINCT FROM @p OR warranty_type IS DISTINCT FROM @t OR warranty_source IS DISTINCT FROM 'vendor')
                """, conn, tx);
            upd.Parameters.AddWithValue("a", assetId);
            upd.Parameters.AddWithValue("p", WarrantyClients.Label(vendor));
            upd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = (object?)Trim(latest?.Name, 100) ?? DBNull.Value });
            upd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Date) { Value = (object?)a.Start ?? DBNull.Value });
            upd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Date) { Value = end });
            upd.Parameters.AddWithValue("over", settings.OverwriteManual);
            upd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Text) { Value = (object?)by ?? DBNull.Value });
            applied = await upd.ExecuteNonQueryAsync(ct) > 0;
            if (!applied)
            {
                await using var chk = new NpgsqlCommand("SELECT warranty_source = 'vendor' AND warranty_end = @e FROM zentriq.asset_lifecycle WHERE asset_id = @a", conn, tx);
                chk.Parameters.AddWithValue("a", assetId);
                chk.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Date) { Value = end });
                applied = await chk.ExecuteScalarAsync(ct) is true;
            }
        }
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.warranty_lookups AS w (asset_id, vendor, serial, status, checked_at, product, ship_date, warranty_start, warranty_end, service_level, entitlements, message, applied)
            VALUES (@a, @v, @sn, @st, now(), @prod, @ship, @s, @e, @lvl, @ents::jsonb, @m, @applied)
            ON CONFLICT (asset_id) DO UPDATE SET vendor = EXCLUDED.vendor, serial = EXCLUDED.serial, status = EXCLUDED.status, checked_at = now(),
                   product = COALESCE(EXCLUDED.product, CASE WHEN EXCLUDED.status = 'error' THEN w.product END),
                   ship_date = COALESCE(EXCLUDED.ship_date, CASE WHEN EXCLUDED.status = 'error' THEN w.ship_date END),
                   warranty_start = CASE WHEN EXCLUDED.status = 'error' THEN w.warranty_start ELSE EXCLUDED.warranty_start END,
                   warranty_end = CASE WHEN EXCLUDED.status = 'error' THEN w.warranty_end ELSE EXCLUDED.warranty_end END,
                   service_level = CASE WHEN EXCLUDED.status = 'error' THEN w.service_level ELSE EXCLUDED.service_level END,
                   entitlements = CASE WHEN EXCLUDED.status = 'error' THEN w.entitlements ELSE EXCLUDED.entitlements END,
                   message = EXCLUDED.message, applied = EXCLUDED.applied
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.AddWithValue("v", vendor);
            cmd.Parameters.AddWithValue("sn", serial);
            cmd.Parameters.AddWithValue("st", status);
            cmd.Parameters.Add(new NpgsqlParameter("prod", NpgsqlDbType.Text) { Value = (object?)Trim(a?.Product, 200) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ship", NpgsqlDbType.Date) { Value = (object?)a?.ShipDate ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Date) { Value = (object?)a?.Start ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Date) { Value = (object?)a?.End ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("lvl", NpgsqlDbType.Text) { Value = (object?)Trim(latest?.Name, 200) ?? DBNull.Value });
            cmd.Parameters.AddWithValue("ents", ents);
            cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Text) { Value = (object?)Trim(message, 1000) ?? DBNull.Value });
            cmd.Parameters.AddWithValue("applied", applied);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
}
