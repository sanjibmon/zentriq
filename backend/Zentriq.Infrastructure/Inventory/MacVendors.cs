using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Inventory;

/// <summary>
/// MAC address vendors from the IEEE registration authority (MA-L, MA-M and MA-S registries, public CSV files). The
/// worker downloads them weekly when the server has internet access; an offline server can import the CSV files from
/// the console. Used to name the vendor of devices found on switch ports.
/// </summary>
public static class MacVendors
{
    public static readonly string[] Sources =
    [
        "https://standards-oui.ieee.org/oui/oui.csv",
        "https://standards-oui.ieee.org/oui28/mam.csv",
        "https://standards-oui.ieee.org/oui36/oui36.csv",
    ];

    public const string StatusKey = "network.mac_vendors";

    /// <summary>Parses an IEEE registry CSV (Registry,Assignment,Organization Name,Organization Address) into (prefix, vendor).</summary>
    public static List<(string Prefix, string Vendor)> Parse(string csv)
    {
        var list = new List<(string, string)>();
        foreach (var line in Lines(csv))
        {
            var f = Fields(line);
            if (f.Count < 3) continue;
            var prefix = new string(f[1].Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
            if (prefix.Length is not (6 or 7 or 9)) continue;   // the header row and anything malformed
            var vendor = f[2].Trim();
            if (vendor.Length == 0) continue;
            list.Add((prefix, vendor.Length > 200 ? vendor[..200] : vendor));
        }
        return list;
    }

    /// <summary>Stores prefixes (insert or update). Returns the number stored.</summary>
    public static async Task<int> StoreAsync(NpgsqlDataSource ds, IReadOnlyCollection<(string Prefix, string Vendor)> rows, string source, CancellationToken ct)
    {
        var unique = rows.GroupBy(r => r.Prefix).Select(g => g.First()).ToList();
        await using var conn = await ds.OpenConnectionAsync(ct);
        foreach (var chunk in unique.Chunk(5000))
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.mac_vendors (prefix, vendor) SELECT * FROM unnest(@p, @v)
                ON CONFLICT (prefix) DO UPDATE SET vendor = EXCLUDED.vendor WHERE mac_vendors.vendor <> EXCLUDED.vendor
                """, conn);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = chunk.Select(c => c.Prefix).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = chunk.Select(c => c.Vendor).ToArray() });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var st = new NpgsqlCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES (@k, @v, now())
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """, conn))
        {
            st.Parameters.AddWithValue("k", StatusKey);
            st.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(new { updatedAt = DateTimeOffset.UtcNow, source, prefixes = unique.Count, error = (string?)null }),
            });
            await st.ExecuteNonQueryAsync(ct);
        }
        return unique.Count;
    }

    public static async Task RecordErrorAsync(NpgsqlDataSource ds, string error, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES (@k, jsonb_build_object('error', @e::text, 'errorAt', now()), now())
            ON CONFLICT (key) DO UPDATE SET value = system_settings.value || jsonb_build_object('error', @e::text, 'errorAt', now()), updated_at = now()
            """);
        cmd.Parameters.AddWithValue("k", StatusKey);
        cmd.Parameters.AddWithValue("e", error.Length > 500 ? error[..500] : error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Downloads the three registries (system proxy) and stores them.</summary>
    public static async Task<int> DownloadAsync(NpgsqlDataSource ds, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        using var http = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials })
            : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zentriq", "1.1"));
        var rows = new List<(string, string)>();
        foreach (var url in Sources)
        {
            string text;
            try { text = await http.GetStringAsync(url, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Could not download {url}: {ex.Message}. The server needs outbound HTTPS to standards-oui.ieee.org, or import the files under Network Inventory › Connected Devices.", ex);
            }
            rows.AddRange(Parse(text));
        }
        if (rows.Count < 1000) throw new InvalidOperationException("The IEEE registry returned too few entries; nothing was changed.");
        return await StoreAsync(ds, rows, "download", ct);
    }

    private static IEnumerable<string> Lines(string csv)
    {
        // Organisation addresses can contain quoted line breaks: split on line breaks outside quotes.
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in csv)
        {
            if (c == '"') quoted = !quoted;
            if ((c == '\n' || c == '\r') && !quoted)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static List<string> Fields(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { list.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        list.Add(sb.ToString());
        return list;
    }
}
