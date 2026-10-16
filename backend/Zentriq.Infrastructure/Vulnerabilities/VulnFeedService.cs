using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Vulnerabilities;

/// <summary>
/// Runs a vulnerability feed update: new software titles are mapped, the CISA KEV catalogue is applied, NVD CVEs are
/// fetched for every mapped product (incrementally after the first run), EPSS scores are refreshed and findings are
/// re-evaluated. Each stage records its own status, so one unreachable source does not stop the others.
/// </summary>
public sealed class VulnFeedService(NpgsqlDataSource ds, VulnStore store, CredentialProtector protector)
{
    public static readonly Guid ApiKeyAad = new("2b8e0f7a-4c1d-4e57-9a3b-71f0d6c2e5a9");

    public async Task<string?> NvdApiKeyAsync(CancellationToken ct)
    {
        var v = await store.SettingAsync("vuln.nvd_api_key", ct);
        if (v is not { ValueKind: JsonValueKind.String } s || string.IsNullOrEmpty(s.GetString())) return null;
        try { return protector.Unprotect(ApiKeyAad, s.GetString()!).Password; }
        catch (Exception) { return null; }
    }

    public string ProtectApiKey(string key) => protector.Protect(ApiKeyAad, new CredentialSecret { Password = key.Trim() });

    public sealed record SyncSummary(int NewTitles, int Products, int Cves, int Kev, int Epss, EvaluationResult? Evaluation, List<string> Problems)
    {
        public int WindowsCves { get; init; }
    }

    /// <summary>How many monthly Microsoft security updates to keep loaded (1–36; default 12).</summary>
    public async Task<int> MsrcMonthsAsync(CancellationToken ct) =>
        await store.SettingAsync("vuln.msrc_months", ct) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var m) ? Math.Clamp(m, 1, 36) : 12;

    /// <summary>
    /// Loads Microsoft's monthly Security Update documents for the configured window. A month is downloaded again only
    /// when Microsoft has revised it (its current release date changed). Months already loaded stay, so devices that are
    /// far behind still show older vulnerabilities.
    /// </summary>
    public async Task<(int Months, int Downloaded, int Cves, int Rules)> SyncMsrcAsync(FeedClient client, Func<string, Task> log, CancellationToken ct)
    {
        var months = await MsrcMonthsAsync(ct);
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        if (await store.SettingAsync("vuln.msrc_loaded", ct) is { ValueKind: JsonValueKind.Object } o)
            foreach (var p in o.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) loaded[p.Name] = p.Value.GetString()!;
        var oldest = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-(months - 1));
        var updates = (await client.MsrcUpdatesAsync(ct))
            .Where(u => DateTime.TryParseExact(u.Id, "yyyy-MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d >= oldest)
            .ToList();
        int downloaded = 0, cves = 0, rules = 0;
        foreach (var u in updates)
        {
            var stamp = u.CurrentRelease?.ToString("o", CultureInfo.InvariantCulture) ?? "";
            if (stamp.Length > 0 && loaded.TryGetValue(u.Id, out var prev) && prev == stamp) continue;
            var doc = await client.MsrcDocumentAsync(u.Id, ct);
            if (doc is null) continue;
            var (c, r) = await store.UpsertMsrcAsync(doc, ct);
            downloaded++; cves += c; rules += r;
            loaded[u.Id] = stamp.Length > 0 ? stamp : DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            await store.SetSettingAsync("vuln.msrc_loaded", JsonSerializer.Serialize(loaded), ct);
            await log($"Microsoft {u.Id}: {c} Windows CVE(s), {r} fixed build(s).");
        }
        return (updates.Count, downloaded, cves, rules);
    }

    public async Task<SyncSummary> SyncAsync(Func<string, Task> log, Func<int, string, Task> progress, CancellationToken ct)
    {
        var problems = new List<string>();
        var newTitles = await store.EnsureMappingsAsync(ct);
        await log($"{newTitles} new software title(s) added to the product mapping.");
        using var client = new FeedClient(await NvdApiKeyAsync(ct));

        // 0. Microsoft Security Updates (Windows) — before KEV, so new Windows CVEs get their KEV flag in this run.
        var windowsCves = 0;
        await progress(2, "Microsoft security updates (Windows)");
        try
        {
            var (months, downloaded, mc, mr) = await SyncMsrcAsync(client, log, ct);
            windowsCves = mc;
            await using var count = ds.CreateCommand("SELECT count(DISTINCT cve_id), count(DISTINCT month) FROM zentriq.vuln_os_rules");
            await using var cr = await count.ExecuteReaderAsync(ct);
            var (total, loadedMonths) = await cr.ReadAsync(ct) ? (cr.GetInt64(0), cr.GetInt64(1)) : (0L, 0L);
            await store.SetFeedStatusAsync("msrc", true, (int)total,
                $"{loadedMonths} monthly update(s) loaded ({downloaded} of the last {months} downloaded now): {total} Windows CVE(s) with fixed builds.", ct);
            await log($"Microsoft: {months} month(s) checked, {downloaded} downloaded, {mc} CVE(s), {mr} fixed build(s).");
        }
        catch (Exception ex) when (ex is FeedException or JsonException)
        {
            problems.Add("Microsoft: " + ex.Message);
            await store.SetFeedStatusAsync("msrc", false, null, ex.Message, ct);
            await log("Microsoft security updates failed: " + ex.Message);
        }

        // 1. CISA KEV
        var kevCount = 0;
        await progress(8, "CISA Known Exploited Vulnerabilities");
        try
        {
            var kev = await client.KevAsync(ct);
            kevCount = await store.ApplyKevAsync(kev, ct);
            await store.SetFeedStatusAsync("kev", true, kev.Count, $"{kev.Count} CVEs in the catalogue; {kevCount} of them in Zentriq's CVE data.", ct);
            await log($"CISA KEV: {kev.Count} known exploited CVEs.");
        }
        catch (Exception ex) when (ex is FeedException or JsonException)
        {
            problems.Add("CISA KEV: " + ex.Message);
            await store.SetFeedStatusAsync("kev", false, null, ex.Message, ct);
            await log("CISA KEV failed: " + ex.Message);
        }

        // 2. NVD per mapped product
        var products = await store.MappedProductsAsync(ct);
        var cveCount = 0;
        var nvdErrors = 0;
        for (var i = 0; i < products.Count; i++)
        {
            var (vendor, product, synced) = products[i];
            await progress(10 + 70 * i / Math.Max(1, products.Count), $"NVD: {vendor}:{product}");
            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                var n = 0;
                await foreach (var page in client.NvdCvesForProductAsync(vendor, product, synced, ct))
                    n += await store.UpsertCvesAsync(page, ct);
                await store.MarkSyncedAsync(vendor, product, startedAt, ct);
                cveCount += n;
                await log($"NVD {vendor}:{product}: {n} CVE(s) {(synced is null ? "loaded" : "updated")}.");
            }
            catch (Exception ex) when (ex is FeedException or JsonException)
            {
                nvdErrors++;
                problems.Add($"NVD {vendor}:{product}: {ex.Message}");
                await log($"NVD {vendor}:{product} failed: {ex.Message}");
                if (nvdErrors >= 3 && cveCount == 0 && i < 3) { await log("Stopping NVD updates: the service is not reachable."); break; }
            }
        }
        await store.SetFeedStatusAsync("nvd", nvdErrors == 0, cveCount,
            nvdErrors == 0 ? $"{products.Count} product(s) checked, {cveCount} CVE record(s) loaded or updated." : $"{nvdErrors} of {products.Count} product(s) failed; {problems.LastOrDefault()}", ct);

        // 3. EPSS
        var epssCount = 0;
        await progress(85, "FIRST EPSS");
        try
        {
            var need = await store.CvesNeedingEpssAsync(20000, ct);
            if (need.Count > 0)
            {
                var scores = await client.EpssAsync(need, ct);
                epssCount = await store.ApplyEpssAsync(scores, ct);
            }
            await store.SetFeedStatusAsync("epss", true, epssCount, $"{epssCount} CVE(s) scored.", ct);
            await log($"EPSS: {epssCount} CVE(s) scored.");
        }
        catch (Exception ex) when (ex is FeedException or JsonException)
        {
            problems.Add("EPSS: " + ex.Message);
            await store.SetFeedStatusAsync("epss", false, null, ex.Message, ct);
            await log("EPSS failed: " + ex.Message);
        }

        // 4. Findings
        await progress(92, "Evaluating installed software");
        var eval = await EvaluateAsync(ct);
        await log($"Evaluation: {eval.Open} open finding(s); {eval.New} new, {eval.Fixed} fixed, {eval.Reopened} reopened.");
        return new SyncSummary(newTitles, products.Count, cveCount, kevCount, epssCount, eval, problems) { WindowsCves = windowsCves };
    }

    public async Task<EvaluationResult> EvaluateAsync(CancellationToken ct)
    {
        await store.EnsureMappingsAsync(ct);
        var eval = await store.EvaluateAsync(ct);
        await store.SetFeedStatusAsync("evaluate", true, eval.Open,
            $"{eval.Installs} installation(s) of {eval.Products} product(s) and {eval.OsAssessed} Windows device(s) checked" +
            (eval.OsNotAssessed > 0 ? $" ({eval.OsNotAssessed} Windows device(s) without a full build number: update their agent or collect inventory again)" : "") +
            $"; {eval.Open} open, {eval.New} new, {eval.Fixed} fixed.", ct);
        return eval;
    }

    /// <summary>Processes an uploaded feed file (NVD CVE 2.0 JSON, CISA KEV JSON, EPSS CSV or API JSON; optionally gzip).</summary>
    public async Task<(int Items, string Message)> ImportAsync(string source, byte[] content, CancellationToken ct)
    {
        await using var stream = FeedClient.OpenMaybeGzip(content);
        switch (source)
        {
            case "nvd":
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var (cves, _) = FeedParsers.ParseNvd(doc.RootElement);
                if (cves.Count == 0) throw new FeedException("The file contains no CVE records (expected NVD CVE JSON 2.0: { \"vulnerabilities\": [ { \"cve\": … } ] }).");
                var n = 0;
                foreach (var chunk in cves.Chunk(1000)) n += await store.UpsertCvesAsync(chunk, ct);
                await store.SetFeedStatusAsync("nvd", true, n, $"Imported {n} CVE record(s) from a file.", ct);
                return (n, $"{n} CVE record(s) imported.");
            }
            case "kev":
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var kev = FeedParsers.ParseKev(doc.RootElement);
                if (kev.Count == 0) throw new FeedException("The file contains no KEV entries (expected known_exploited_vulnerabilities.json from CISA).");
                var known = await store.ApplyKevAsync(kev, ct);
                await store.SetFeedStatusAsync("kev", true, kev.Count, $"Imported {kev.Count} KEV entries from a file; {known} in Zentriq's CVE data.", ct);
                return (kev.Count, $"{kev.Count} known exploited CVE(s) imported.");
            }
            case "epss":
            {
                List<EpssEntry> scores;
                var head = content.Length > 0 && content[0] == 0x1f ? (byte)0 : content.FirstOrDefault(b => b > 32);
                if (head == (byte)'{')
                {
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    scores = FeedParsers.ParseEpssJson(doc.RootElement);
                }
                else
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    scores = FeedParsers.ParseEpssCsv(reader).ToList();
                }
                if (scores.Count == 0) throw new FeedException("The file contains no EPSS scores (expected epss_scores-YYYY-MM-DD.csv(.gz) from FIRST).");
                var n = await store.ApplyEpssAsync(scores, ct);
                await store.SetFeedStatusAsync("epss", true, n, $"Imported {scores.Count} EPSS score(s) from a file; {n} matched CVEs in Zentriq.", ct);
                return (scores.Count, $"{scores.Count} EPSS score(s) imported ({n} for CVEs in Zentriq).");
            }
            case "msrc":
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var msrc = Msrc.ParseDocument(doc.RootElement);
                if (msrc.Cves.Count == 0)
                    throw new FeedException("The file contains no Windows fixes (expected a Microsoft CVRF JSON document, e.g. from https://api.msrc.microsoft.com/cvrf/v3.0/cvrf/2026-Oct downloaded with 'Accept: application/json').");
                var (c, r) = await store.UpsertMsrcAsync(msrc, ct);
                await store.SetFeedStatusAsync("msrc", true, c, $"Imported {msrc.Id} from a file: {c} Windows CVE(s), {r} fixed build(s).", ct);
                return (c, $"{msrc.Id}: {c} Windows CVE(s) and {r} fixed build(s) imported.");
            }
            default:
                throw new FeedException($"Unknown feed '{source}'.");
        }
    }

    public NpgsqlDataSource DataSource => ds;
}
